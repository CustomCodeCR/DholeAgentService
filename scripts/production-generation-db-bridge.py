#!/usr/bin/env python3
"""Bridge UI requests stored in PostgreSQL to the guarded host executor.

All provider restrictions, manual readiness attestation, leases and real
container checks remain enforced by production-generation-executor.py.
Never copies or destroys browser profiles and never resets provider circuits.
"""
import argparse
import fcntl
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]


def abort(message):
    raise RuntimeError(message)


def connection_from_env(path):
    raw = next((line.split("=", 1)[1].strip() for line in Path(path).read_text().splitlines()
                if line.startswith("AGENT_POSTGRES_CONNECTION_STRING=")), "")
    settings = dict(pair.split("=", 1) for pair in raw.split(";") if "=" in pair)
    for name in ("Database", "Username", "Password"):
        if not settings.get(name):
            abort("missing PostgreSQL configuration")
    if not re.fullmatch(r"[A-Za-z0-9_]+", settings["Database"]):
        abort("unsafe database name")
    return settings


def sql_query(conn, sql):
    environment = dict(os.environ)
    environment["PGPASSWORD"] = conn["Password"]
    args = ["docker", "run", "--rm", "--network", "dhole",
            "-e", "PGPASSWORD", "postgres:17-alpine",
            "psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-At",
            "-h", conn.get("Host", "postgres"), "-p", conn.get("Port", "5432"),
            "-U", conn["Username"], "-d", conn["Database"], "-c", sql]
    result = subprocess.run(args, env=environment, capture_output=True,
                            text=True, timeout=60)
    if result.returncode:
        abort("database command failed without changing browser profiles")
    return result.stdout.strip()


def next_request(conn):
    sql = """
    SELECT row_to_json(t)::text FROM (
      SELECT id::text AS id, target_generation AS target,
             trigger_code AS event, status
      FROM agent.runtime_generation_requests
      WHERE status = 'Pending'
      ORDER BY created_at_utc, id LIMIT 1
    ) t;
    """
    result = sql_query(conn, sql)
    return json.loads(result.splitlines()[0]) if result else None


def mark(conn, event_id, status, code=""):
    if status not in ("Processing", "Completed", "Failed", "Rejected"):
        abort("invalid event status")
    if code and not re.fullmatch(r"[a-z_]{1,80}", code):
        abort("invalid diagnostic code")
    sql_query(conn, f"""
       UPDATE agent.runtime_generation_requests
       SET status='{status}',
           diagnostic_code={'NULL' if not code else chr(39)+code+chr(39)},
           completed_at_utc={'now()' if status != 'Processing' else 'NULL'}
       WHERE id='{event_id}'::uuid
         AND status IN ('Pending','Processing');
    """)


def sync_active(conn, event_id, target):
    # One transaction: successful handoff and its active-generation record.
    if target != "prod1":
        abort("target generation is unsupported")
    sql_query(conn, f"""
       BEGIN;
       UPDATE agent.runtime_generation_state
         SET active_generation='prod1', updated_at_utc=now()
         WHERE singleton=true AND active_generation='prod';
       UPDATE agent.runtime_generation_requests
         SET status='Completed', completed_at_utc=now(),
             diagnostic_code='verified_worker_handoff'
         WHERE id='{event_id}'::uuid AND status='Processing';
       COMMIT;
    """)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--env-file", required=True)
    p.add_argument("--state-dir", required=True)
    p.add_argument("--mode", choices=["check", "apply"], default="check")
    p.add_argument("--confirmation", default="")
    args = p.parse_args()
    state = Path(args.state_dir).expanduser()
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(state, 0o700)
    with (state / "database-bridge.lock").open("a+") as lock:
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        conn = connection_from_env(args.env_file)
        event = next_request(conn)
        if event is None:
            print("GENERATION_DB_NO_PENDING_EVENTS")
            return
        event_id = str(uuid.UUID(event["id"]))
        if event["target"] != "prod1" or event["event"] not in (
                "manual", "scheduled", "technical_failure", "data_quality"):
            abort("unsupported event request")
        print(json.dumps({"id": event_id, "event": event["event"],
                          "destination": event["target"], "mode": args.mode}))
        if args.mode == "check":
            return
        if args.confirmation != "AUTHORIZE_VERIFIED_WORKER_HANDOFF":
            abort("explicit executor authorisation required")

        rule = sql_query(conn, "SELECT enabled::text FROM agent.runtime_generation_rules "
                                   f"WHERE code='{event['event']}';")
        if rule.strip() != "true":
            abort("event rule is no longer enabled")
        config = json.loads((ROOT / "config/production-generation-orchestrator.json").read_text())
        config["enabled"] = True
        config["events"][event["event"]]["enabled"] = True
        config["allowed_destinations"] = ["prod1"]
        protected = state / "runtime-config.json"
        protected.write_text(json.dumps(config) + "\n")
        os.chmod(protected, 0o600)

        pending = state / "pending"
        pending.mkdir(exist_ok=True, mode=0o700)
        event_file = pending / (event_id + ".json")
        if event_file.exists() or event_file.is_symlink():
            abort("request already staged for execution")
        event_file.write_text(json.dumps({
            "id": event_id, "event": event["event"], "target": "prod1",
            "status": "pending", "requires_executor_verification": True
        }))
        os.chmod(event_file, 0o600)
        try:
            executor = ROOT / "scripts/production-generation-executor.py"
            # Run the executor's independent checks FIRST. No Docker changes.
            checked = subprocess.run([sys.executable, str(executor),
                "--config", str(protected), "--state-dir", str(state),
                "--env-file", args.env_file, "--request-id", event_id,
                "--mode", "check"], capture_output=True, text=True, timeout=90)
            if checked.returncode:
                mark(conn, event_id, "Rejected", "handoff_preflight_failed")
                event_file.unlink(missing_ok=True)
                print("GENERATION_DB_EVENT_REJECTED: executor preflight failed")
                return
            mark(conn, event_id, "Processing")
            performed = subprocess.run([sys.executable, str(executor),
                "--config", str(protected), "--state-dir", str(state),
                "--env-file", args.env_file, "--request-id", event_id,
                "--mode", "apply", "--confirmation", args.confirmation],
                capture_output=True, text=True, timeout=240)
            if performed.returncode:
                mark(conn, event_id, "Failed", "handoff_execution_failed")
                print("GENERATION_DB_EVENT_FAILED: primary rollback requested")
                return
            sync_active(conn, event_id, "prod1")
            print("GENERATION_DB_PROD1_ACTIVE")
        finally:
            event_file.unlink(missing_ok=True)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, ValueError, KeyError,
            subprocess.SubprocessError) as error:
        print("GENERATION_DB_BRIDGE_REFUSED: " +
              (str(error) if isinstance(error, RuntimeError)
               else type(error).__name__), file=sys.stderr)
        sys.exit(1)
