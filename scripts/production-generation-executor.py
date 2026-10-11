#!/usr/bin/env python3
"""Guarded handoff to a PRE-EXISTING and VERIFIED production standby Worker.

Never provisions a new browser identity, repairs authentication or clears a
provider circuit. Rejected provider authentication is an explicit NO-GO.
The existing public Agent API remains the stable ingress.
"""
import argparse
import datetime as dt
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid


def stop(message):
    raise RuntimeError("GENERATION_SWITCH_REFUSED: " + message)


def command(args, *, environment=None, timeout=35):
    result = subprocess.run(args, capture_output=True, text=True,
                            check=True, timeout=timeout, env=environment)
    return (result.stdout + "\n" + result.stderr).strip()


def parse_time(value):
    result = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.tzinfo is None:
        stop("timestamp must contain UTC offset")
    return result.astimezone(dt.timezone.utc)


def inspect(project, service):
    ids = command(["docker", "ps", "-aq", "--filter",
                   "label=com.docker.compose.project=" + project,
                   "--filter", "label=com.docker.compose.service=" + service]).splitlines()
    if len(ids) != 1 or not ids[0]:
        stop("exactly one expected container is required: " + project + "/" + service)
    return json.loads(command(["docker", "inspect", ids[0]]))[0]


def generation_services(generation):
    """Compose project / API / Worker / Hermes naming; prod1 remains legacy-compatible."""
    if generation == "prod":
        return ("dhole", "dhole-agent-api", "dhole-agent-workers", None)
    if not isinstance(generation, str) or not re.fullmatch(r"prod[1-9][0-9]*", generation) or len(generation) > 64:
        stop("invalid generation")
    if generation == "prod1":
        return ("dhole-agent-prod1", "dhole-agent-api-prod1",
                "dhole-agent-workers-prod1", "hermes-agent-prod1")
    return (f"dhole-agent-{generation}", "dhole-agent-api",
            "dhole-agent-workers", "hermes-agent")


def find_running_generation():
    # Discover actual Worker owner, never trust only the stored current-generation label.
    ids = command(["docker", "ps", "-q",
                   "--filter", "label=com.docker.compose.project"]).splitlines()
    found = []
    for container_id in ids:
        info = json.loads(command(["docker", "inspect", container_id]))[0]
        labels = info.get("Config", {}).get("Labels", {}) or {}
        project = labels.get("com.docker.compose.project", "")
        service = labels.get("com.docker.compose.service", "")
        if project == "dhole" and service == "dhole-agent-workers":
            found.append(("prod", info))
        elif re.fullmatch(r"dhole-agent-prod[1-9][0-9]*", project):
            generation = project.removeprefix("dhole-agent-")
            allowed = generation_services(generation)[2]
            if service == allowed:
                found.append((generation, info))
    if len(found) != 1:
        stop("exactly one active Agent Worker across all generations is required")
    return found[0]


def container_env(container):
    return dict(s.split("=", 1) for s in container["Config"]["Env"] if "=" in s)


def mount_name(container, target):
    for mount in container["Mounts"]:
        if mount["Destination"] == target:
            return (mount.get("Type"), mount.get("Name") or mount.get("Source"))
    stop("required persistent mount missing: " + target)


def healthy(container):
    state = container["State"]
    return state["Status"] == "running" and state.get("Health", {}).get("Status", "healthy") == "healthy"


def load_connection(env_file):
    config = {}
    for line in Path(env_file).read_text().splitlines():
        if line.startswith("AGENT_POSTGRES_CONNECTION_STRING="):
            connection = line.split("=", 1)[1]
            config = dict(value.split("=", 1) for value in connection.split(";") if "=" in value)
            break
    if not config.get("Database") or not config.get("Username") or not config.get("Password"):
        stop("PostgreSQL credentials missing from protected configuration")
    if not re.fullmatch(r"[A-Za-z0-9_]+", config["Database"]):
        stop("unsafe PostgreSQL database")
    return config


def database_clear(config):
    # This reads *live* provider restrictions and jobs from the shared database.
    # An open circuit or an active job prohibits every cutover, regardless of event.
    sql = ("""SELECT CASE WHEN
      EXISTS (SELECT 1 FROM agent.maersk_circuits
              WHERE state <> 'Closed' OR requires_operator)
      OR EXISTS (SELECT 1 FROM agent."AgentExecutions"
                 WHERE status = 'Running')
      THEN 'BLOCKED' ELSE 'CLEAR' END;""")
    environment = os.environ.copy()
    environment["PGPASSWORD"] = config["Password"]
    result = command([
        "docker", "run", "--rm", "--network", "dhole", "-e", "PGPASSWORD",
        "postgres:17-alpine", "psql", "-X", "-v", "ON_ERROR_STOP=1", "-At",
        "-h", config.get("Host", "postgres"),
        "-p", config.get("Port", "5432"), "-U", config["Username"],
        "-d", config["Database"], "-c", sql], environment=environment, timeout=45)
    if result != "CLEAR":
        stop("live circuit restrictions or active Agent executions prohibit handoff")


def validate(state_dir, request, config):
    if config.get("enabled") is not True:
        stop("orchestrator is not enabled")
    target = request.get("target")
    if (not isinstance(target, str) or len(target) > 64 or
            not (target == "prod" or re.fullmatch(r"prod[1-9][0-9]*", target))):
        stop("invalid registered generation")
    allowed = config.get("allowed_destinations", [])
    if target not in allowed and "prod*" not in allowed:
        stop("destination is not permitted")
    source = request.get("event")
    if source not in ("manual", "scheduled", "technical_failure"):
        stop("unsupported event")
    if not config.get("events", {}).get(source, {}).get("enabled"):
        stop("event is disabled")
    if request.get("status") != "pending" or request.get("requires_executor_verification") is not True:
        stop("request was not registered by publisher")
    marker = state_dir / "ready" / (target + ".json")
    if marker.is_symlink() or not marker.is_file():
        stop("standby has no operator-verified readiness record")
    readiness = json.loads(marker.read_text())
    if readiness.get("generation") != target or readiness.get("ready") is not True:
        stop("destination was not verified")
    if (readiness.get("provider_authorized") is not True or
            not isinstance(readiness.get("verified_by"), str) or
            len(readiness["verified_by"]) < 3):
        stop("standby provider authorization requires independent operator attestation")
    now = dt.datetime.now(dt.timezone.utc)
    age = now - parse_time(readiness["verified_at_utc"])
    if not dt.timedelta(0) <= age <= dt.timedelta(
            seconds=int(config["readiness_max_age_seconds"])):
        stop("standby readiness is stale")
    return target


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--env-file", required=True)
    parser.add_argument("--request-id", required=True)
    parser.add_argument("--mode", choices=("check", "apply"), default="check")
    parser.add_argument("--confirmation", default="")
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9a-f-]{36}", args.request_id):
        stop("invalid request ID")
    if str(uuid.UUID(args.request_id)) != args.request_id:
        stop("request ID must be canonical")
    state_dir = Path(args.state_dir).expanduser()
    config = json.loads(Path(args.config).read_text())
    request_file = state_dir / "pending" / (args.request_id + ".json")
    if not request_file.is_file() or request_file.is_symlink():
        stop("no pending event with that ID")
    request = json.loads(request_file.read_text())
    if request.get("id") != args.request_id:
        stop("request ID mismatch")
    target = validate(state_dir, request, config)

    active_name, original = find_running_generation()
    if active_name == target:
        stop("requested destination is already the active Worker")
    source_project, source_api, _, _ = generation_services(active_name)
    target_project, target_api, target_worker, target_hermes = generation_services(target)
    alternate = inspect(target_project, target_worker)
    api = inspect(target_project, target_api)
    hermes = (inspect("dhole-infrastructure", "hermes-agent")
              if target_hermes is None else inspect(target_project, target_hermes))
    if not healthy(api) or not healthy(hermes):
        stop("standby API and Hermes must already be running and healthy")
    if alternate["State"]["Status"] not in ("created", "exited"):
        stop("standby Worker must be stopped before handoff")
    if original["State"]["Status"] != "running":
        stop("primary Worker must be running")
    if original["Image"] != alternate["Image"]:
        stop("standby Worker must use the same image revision as primary")
    primary_api = inspect(source_project, source_api)
    if primary_api["Image"] != api["Image"]:
        stop("standby API must use the same image revision as primary")
    original_env, alternate_env = container_env(original), container_env(alternate)
    for key in ("Postgres__ConnectionString", "Redis__ConnectionString",
                "CredentialProtection__KeyRingPath"):
        if not original_env.get(key) or original_env.get(key) != alternate_env.get(key):
            stop("source and destination must share identical " + key + " configuration")
    for key in ("MaerskCircuit__Enabled", "MaerskMonitoring__Enabled"):
        if original_env.get(key, "").lower() != "true" or alternate_env.get(key, "").lower() != "true":
            stop("both Workers must have active Maersk circuit and monitoring")
    if (original_env.get("AgentQueue__ConcurrentDispatcherEnabled", "").lower() !=
            alternate_env.get("AgentQueue__ConcurrentDispatcherEnabled", "").lower()):
        stop("source and destination queue dispatch mode must match")
    if mount_name(original, "/data/agent-keys") == mount_name(alternate, "/data/agent-keys"):
        stop("keyring volume must be isolated per generation; compatibility requires prior verification")
    if mount_name(original, "/data/browser-profiles") == mount_name(alternate, "/data/browser-profiles"):
        stop("isolated Workers must have different browser profile volumes")
    config_db = load_connection(args.env_file)
    database_clear(config_db)
    if args.mode == "check":
        print(json.dumps({"status": "READY_FOR_OPERATOR_CONTROLLED_HANDOFF",
                          "target": target, "source": active_name, "event_id": args.request_id,
                          "executed": False}))
        return
    if args.confirmation != "AUTHORIZE_VERIFIED_WORKER_HANDOFF":
        stop("explicit confirmation required for live Worker handoff")

    # Only the selected job owner is stopped. No volume delete, credential
    # reset, browser reset, API restart, queue mutation or circuit reset.
    original_id, alternate_id = original["Id"], alternate["Id"]
    command(["docker", "stop", "--time", "60", original_id], timeout=90)
    try:
        database_clear(config_db)
        command(["docker", "start", alternate_id], timeout=60)
        success = False
        for _ in range(20):
            time.sleep(3)
            current = json.loads(command(["docker", "inspect", alternate_id]))[0]
            if current["State"]["Status"] != "running":
                break
            logs = command(["docker", "logs", "--since", "2m", alternate_id], timeout=10)
            if re.search(r"AGENT_QUEUE_(PUMP|CONCURRENT)_STARTED", logs):
                success = True
                break
        if not success:
            stop("standby Worker failed health gate")
    except Exception:
        subprocess.run(["docker", "stop", "--time", "10", alternate_id],
                       capture_output=True, timeout=25)
        subprocess.run(["docker", "start", original_id],
                       capture_output=True, timeout=25)
        raise

    completed_dir = state_dir / "completed"
    completed_dir.mkdir(mode=0o700, exist_ok=True)
    request["status"] = "completed"
    request["completed_at_utc"] = dt.datetime.now(dt.timezone.utc).isoformat()
    destination = completed_dir / request_file.name
    temporary = completed_dir / ("." + request_file.name)
    temporary.write_text(json.dumps(request, indent=2) + "\n")
    os.replace(temporary, destination)
    request_file.unlink()
    print(json.dumps({"status": "WORKER_HANDOFF_COMPLETED",
                      "target": target, "source": active_name, "event_id": args.request_id,
                      "executed": True}))


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, ValueError, KeyError, TypeError,
            subprocess.SubprocessError) as exc:
        # Never echo secret environment or SQL error output.
        detail = str(exc) if isinstance(exc, RuntimeError) else type(exc).__name__
        print("GENERATION_SWITCH_FAILED: " + detail, file=sys.stderr)
        sys.exit(1)
