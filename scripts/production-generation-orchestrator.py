#!/usr/bin/env python3
"""Policy-gated generation event publisher; never changes Docker or session state.

A separate deployment executor must independently validate DB leases, circuit
state, container readiness, authorized destination and safe rollback.
"""
import argparse
import datetime as dt
import fcntl
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import uuid


def fail(message):
    print(f"GENERATION_EVENT_BLOCKED: {message}", file=sys.stderr)
    raise SystemExit(2)


def utcnow():
    return dt.datetime.now(dt.timezone.utc)


def parse_time(value):
    result = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.tzinfo is None:
        raise ValueError("A timezone is required")
    return result.astimezone(dt.timezone.utc)


def technical_failure_verified(container):
    if not container or not re.fullmatch(r"[A-Za-z0-9_.-]{3,128}", container):
        return False
    try:
        result = subprocess.run(
            ["docker", "inspect", "--format",
             "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}",
             container],
            text=True, capture_output=True, check=True, timeout=10)
    except (OSError, subprocess.SubprocessError):
        return False
    fields = result.stdout.strip().split("|")
    return len(fields) == 2 and (
        fields[0] in {"exited", "dead", "restarting"} or fields[1] == "unhealthy"
    )


def ensure_readiness(state_dir, target, max_age, now):
    marker = state_dir / "ready" / f"{target}.json"
    if not marker.is_file() or marker.is_symlink():
        fail("destination has no trusted readiness evidence")
    try:
        data = json.loads(marker.read_text(encoding="utf-8"))
        verified = parse_time(data["verified_at_utc"])
    except (OSError, ValueError, KeyError, TypeError):
        fail("destination readiness evidence is invalid")
    if data.get("generation") != target or data.get("ready") is not True:
        fail("destination readiness is not verified")
    if not (dt.timedelta(0) <= now - verified <= dt.timedelta(seconds=max_age)):
        fail("destination readiness evidence has expired")


def main():
    parser = argparse.ArgumentParser(description="Schedule a guarded production generation event")
    parser.add_argument("--config", required=True)
    parser.add_argument("--event", choices=("manual", "scheduled", "technical_failure"), required=True)
    parser.add_argument("--target", default=None)
    parser.add_argument("--mode", choices=("plan", "submit"), default="plan")
    parser.add_argument("--state-dir", default=str(Path.home() / ".local/state/dhole-production-generation"))
    parser.add_argument("--confirmation", default="")
    parser.add_argument("--component", default="")
    parser.add_argument("--reason", default="")
    args = parser.parse_args()
    if len(args.reason) > 240:
        fail("event reason is too long")
    try:
        config = json.loads(Path(args.config).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        fail("configuration cannot be read")
    if config.get("schema_version") != 1:
        fail("unsupported configuration version")
    target = args.target or config.get("default_destination")
    if not isinstance(target, str) or not re.fullmatch(r"prod[1-9][0-9]*", target):
        fail("destination must be a named standby production generation")
    destinations = config.get("allowed_destinations", [])
    if target not in destinations and "prod*" not in destinations:
        fail("destination is not permitted by policy")
    rule = config.get("events", {}).get(args.event, {})
    if not config.get("enabled") or not rule.get("enabled"):
        fail("this event source is disabled")
    if args.event == "technical_failure" and not technical_failure_verified(args.component):
        fail("technical failure must be verified from the unhealthy container")
    if args.mode == "plan":
        print(json.dumps({"event": args.event, "target": target, "mode": "plan",
                          "next": "verify standby readiness and execute submission explicitly"}))
        return
    if args.confirmation != "AUTHORIZE_GENERATION_EVENT":
        fail("submission requires explicit confirmation")

    state_dir = Path(args.state_dir).expanduser()
    state_dir.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(state_dir, 0o700)
    with (state_dir / "orchestrator.lock").open("a+") as lock:
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
        now = utcnow()
        if config.get("policy", {}).get("require_preprovisioned_destination") is not True:
            fail("fail-closed policy requires a pre-provisioned standby")
        ensure_readiness(state_dir, target, int(config["readiness_max_age_seconds"]), now)
        events_dir = state_dir / "pending"
        events_dir.mkdir(mode=0o700, exist_ok=True)
        if sum(1 for _ in events_dir.glob("*.json")) >= int(config["pending_event_limit"]):
            fail("pending generation request already exists")
        last_file = state_dir / "last-event.json"
        if last_file.exists():
            try:
                last = parse_time(json.loads(last_file.read_text())["created_at_utc"])
            except (ValueError, KeyError, OSError, TypeError):
                fail("existing cooldown state is unreadable")
            if now - last < dt.timedelta(seconds=int(config["cooldown_seconds"])):
                fail("generation event cooldown is active")

        request = {
            "id": str(uuid.uuid4()), "created_at_utc": now.isoformat(),
            "event": args.event, "target": target,
            "reason": args.reason, "status": "pending",
            "requires_executor_verification": True
        }
        fd, pathname = tempfile.mkstemp(prefix=".generation-", dir=events_dir)
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as output:
                json.dump(request, output, indent=2)
                output.write("\n")
            destination = events_dir / (request["id"] + ".json")
            os.replace(pathname, destination)
            last_file.write_text(json.dumps({"created_at_utc": request["created_at_utc"]}) + "\n")
            os.chmod(last_file, 0o600)
        finally:
            if os.path.exists(pathname):
                os.unlink(pathname)
        print(json.dumps({"status": "queued_for_validation", "event_id": request["id"],
                          "target": target, "executed": False}))


if __name__ == "__main__":
    main()
