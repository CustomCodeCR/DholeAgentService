#!/usr/bin/env python3
"""Execute one DB-authorized operational generation request on the self-hosted runner.

Only the already-provisioned, preauthorized standby may start. This script
NEVER provisions a browser identity, rotates sessions or clears provider blocks.
All execution is bounded to the GitHub Actions job and worker deadline.
"""
import argparse
import datetime as dt
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
GENERATION = re.compile(r"^prod[1-9][0-9]*$")


class GateError(Exception):
    pass


def parse_db(path):
    connection = ""
    for line in Path(path).read_text().splitlines():
        if line.startswith("AGENT_POSTGRES_CONNECTION_STRING="):
            connection = line.split("=", 1)[1].strip()
            break
    values = dict(part.split("=", 1) for part in connection.split(";") if "=" in part)
    for item in ("Username", "Database", "Password"):
        if not values.get(item):
            raise GateError("Missing protected PostgreSQL configuration")
    if not re.fullmatch(r"[A-Za-z0-9_]+", values["Database"]):
        raise GateError("Invalid database name")
    return values


def sql(db, query):
    env = os.environ.copy()
    env["PGPASSWORD"] = db["Password"]
    cmd = [
        "docker", "run", "--rm", "--network", "dhole", "-e", "PGPASSWORD",
        "postgres:16-alpine", "psql", "-X", "-q", "-At",
        "-v", "ON_ERROR_STOP=1", "-h", db.get("Host", "postgres"),
        "-p", db.get("Port", "5432"), "-U", db["Username"],
        "-d", db["Database"], "-c", query
    ]
    outcome = subprocess.run(cmd, capture_output=True, text=True, timeout=20, env=env)
    if outcome.returncode:
        raise GateError("PostgreSQL state verification failed")
    return outcome.stdout.strip()


def set_terminal(db, request_id, status, code):
    if status not in ("Failed", "Rejected") or not re.fullmatch(r"[a-z_]{1,80}", code):
        raise GateError("Unexpected status")
    sql(db, f"""
        UPDATE agent.runtime_generation_requests
        SET status='{status}', diagnostic_code='{code}', completed_at_utc=now()
        WHERE id='{request_id}'::uuid AND status='Processing';
    """)


def run(request_id, env_file, state_dir, check_only):
    db = parse_db(env_file)
    # No provider session challenge is a valid switch trigger.
    row = sql(db, f"""
        SELECT target_generation || '|' || trigger_code || '|' ||
            (EXTRACT(EPOCH FROM now()-created_at_utc))::integer::text
        FROM agent.runtime_generation_requests
        WHERE id='{request_id}'::uuid AND status='Processing'
          AND diagnostic_code='github_workflow_dispatched'
          AND created_at_utc > now() - interval '4 minutes 45 seconds';
    """)
    if not row or len(row.split("|")) != 3:
        raise GateError("Stale, absent or unapproved DB request")
    target, trigger, age = row.split("|")
    if not GENERATION.fullmatch(target) or len(target) > 64:
        raise GateError("Request target must be an existing standby generation")
    if trigger not in ("manual", "scheduled", "technical_failure", "data_quality"):
        raise GateError("Provider access-denial events cannot trigger generation switch")
    enabled = sql(db, f"""
        SELECT enabled FROM agent.runtime_generation_rules WHERE code='{trigger}';
    """)
    if enabled != "t":
        set_terminal(db, request_id, "Rejected", "trigger_rule_disabled")
        raise GateError("Rule no longer enabled")
    registered = sql(db, f"""
        SELECT EXISTS(SELECT 1 FROM agent.runtime_generations
                      WHERE name='{target}');
    """)
    if registered != "t":
        set_terminal(db, request_id, "Rejected", "unregistered_generation")
        raise GateError("Destination was never registered")
    # Signal to the dispatcher that GitHub has started processing. Stale
    # workflow_dispatch events may expire, but a live handoff is never
    # marked failed in the middle of a safely bounded Docker transition.
    begun = sql(db, f"""
        UPDATE agent.runtime_generation_requests
        SET diagnostic_code='github_workflow_running'
        WHERE id='{request_id}'::uuid AND status='Processing'
          AND diagnostic_code='github_workflow_dispatched'
        RETURNING id::text;
    """)
    if begun != request_id:
        raise GateError("Request has already been started or completed by another runner")
    # The executor validates live queues/circuits and the independent provider
    # authorization record on every invocation. Do not synthesize readiness.
    state = Path(state_dir).expanduser().resolve()
    (state / "pending").mkdir(parents=True, mode=0o700, exist_ok=True)
    config = json.loads((ROOT / "config/production-generation-orchestrator.json").read_text())
    config["enabled"] = True
    config["allowed_destinations"] = [target]
    if trigger not in config["events"]:
        raise GateError("Unknown configured event")
    config["events"][trigger]["enabled"] = True

    with tempfile.TemporaryDirectory(prefix="generation-policy-") as temporary:
        configuration = Path(temporary) / "policy.json"
        configuration.write_text(json.dumps(config))
        event_file = state / "pending" / (request_id + ".json")
        # Ensure a repeated workflow cannot stage/execute the same request twice.
        flags = os.O_CREAT | os.O_EXCL | os.O_WRONLY
        try:
            file_descriptor = os.open(event_file, flags, 0o600)
        except FileExistsError:
            raise GateError("Another workflow is handling this request")
        try:
            with os.fdopen(file_descriptor, "w") as output:
                json.dump({"id": request_id, "status": "pending", "target": target,
                           "event": trigger, "requires_executor_verification": True}, output)
            executable = ROOT / "scripts/production-generation-executor.py"
            args = [
                sys.executable, str(executable), "--config", str(configuration),
                "--state-dir", str(state), "--env-file", env_file,
                "--request-id", request_id
            ]
            preflight = subprocess.run(args + ["--mode", "check"],
                                      capture_output=True, text=True, timeout=65)
            if preflight.returncode != 0:
                set_terminal(db, request_id, "Rejected", "standby_preflight_failed")
                raise GateError("Standby failed independent health, circuit or authorization gate")
            if check_only:
                print("GENERATION_READY_FOR_AUTHORIZED_HANDOFF")
                return
            # Do not start after the 4m45 dispatch deadline.
            valid = sql(db, f"""
                SELECT EXISTS(SELECT 1 FROM agent.runtime_generation_requests
                WHERE id='{request_id}'::uuid AND status='Processing'
                  AND created_at_utc > now() - interval '4 minutes 45 seconds');
            """)
            if valid != "t":
                raise GateError("Request exceeded the five-minute orchestration deadline")
            applied = subprocess.run(args + [
                "--mode", "apply", "--confirmation", "AUTHORIZE_VERIFIED_WORKER_HANDOFF"
            ], capture_output=True, text=True, timeout=180)
            if applied.returncode:
                set_terminal(db, request_id, "Failed", "verified_handoff_failed")
                raise GateError("Worker handoff failed; executor attempted rollback")
            response = json.loads(applied.stdout)
            source = response["source"]
            if source != "prod" and not GENERATION.fullmatch(source):
                raise GateError("Unsafe active generation identifier")
            completed = sql(db, f"""
                BEGIN;
                UPDATE agent.runtime_generation_state
                SET active_generation='{target}', updated_at_utc=now()
                WHERE singleton=true AND active_generation='{source}';
                UPDATE agent.runtime_generation_requests
                SET status='Completed', diagnostic_code='verified_worker_handoff',
                    completed_at_utc=now()
                WHERE id='{request_id}'::uuid AND status='Processing'
                  AND created_at_utc > now() - interval '5 minutes';
                COMMIT;
                SELECT status FROM agent.runtime_generation_requests
                WHERE id='{request_id}'::uuid;
            """)
            if completed.splitlines()[-1] != "Completed":
                raise GateError("Worker switched but persistent completion state needs reconciliation")
            print(f"GENERATION_HANDOFF_CONFIRMED request={request_id} active={target}")
        finally:
            event_file.unlink(missing_ok=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--request-id", required=True)
    parser.add_argument("--env-file", required=True)
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--check-only", action="store_true")
    arguments = parser.parse_args()
    try:
        request_id = str(uuid.UUID(arguments.request_id))
        if request_id != arguments.request_id:
            raise ValueError()
        run(request_id, arguments.env_file, arguments.state_dir, arguments.check_only)
    except (GateError, OSError, ValueError, subprocess.SubprocessError) as error:
        print("GENERATION_OPERATION_REFUSED: " +
              (str(error) if isinstance(error, GateError) else type(error).__name__),
              file=sys.stderr)
        sys.exit(1)
