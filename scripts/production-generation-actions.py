#!/usr/bin/env python3
"""GitHub Actions generation control. Never clears provider restrictions or fabricates authorization."""
import argparse
import datetime as dt
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
NAME = re.compile(r"^prod[1-9][0-9]*$")


class ActionRefused(Exception):
    pass


def numbered_targets(active, candidate_names):
    """Prefer increasing numbered destinations, wrapping after the highest."""
    n = int(active[4:]) if active != "prod" and NAME.fullmatch(active) else 0
    valid = {v for v in candidate_names if NAME.fullmatch(v) and
             len(v) <= 64 and v != active}
    return sorted(valid, key=lambda v: (int(v[4:]) <= n, int(v[4:])))


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def executor():
    return load_module("guarded_generation_executor", "scripts/production-generation-executor.py")


# Only allowlisted machine-readable diagnostics can leave a subprocess.
# Raw Docker/env/HTTP outputs may contain credentials and are never echoed.
SAFE_FAILURE_CODES = {
    "GENERATION_INVALID_NAME", "GENERATION_INVALID_MODE",
    "GENERATION_MANIFEST_OR_ENV_MISSING", "GENERATION_API_CONFIG_INCOMPLETE",
    "GENERATION_EXISTING_INCOMPLETE", "GENERATION_HERMES_NOT_RUNNING",
    "GENERATION_ACTIVE_WORKER_PROTECTED", "GENERATION_MULTIPLE_STANDBY_WORKERS",
    "GENERATION_ACTIVE_STANDBY_WORKER_REFUSED", "GENERATION_REQUIRED_SERVICE_MISSING",
    "GENERATION_WORKER_PERSISTENCE_MISSING", "GENERATION_WORKER_NOVNC_CONFIG_MISSING",
    "GENERATION_WORKER_MISSING", "GENERATION_WORKER_UNEXPECTEDLY_ACTIVE",
    "GENERATION_INFRASTRUCTURE_HEALTH_FAILED", "GENERATION_INSUFFICIENT_ROOT_SPACE",
    "GENERATION_PROVIDER_AUTHORIZATION_NOT_VERIFIED",
}
def safe_failure_code(stdout, stderr):
    failures = []
    for line in (stderr + "\n" + stdout).splitlines():
        match = re.match(r"^(GENERATION_[A-Z0-9_]+)(?:[ :]|$)", line.strip())
        if match and match.group(1) in SAFE_FAILURE_CODES:
            failures.append(match.group(1).lower())
    # The explicit final failure supersedes informational preceding lines.
    return failures[-1] if failures else "guarded_command_failed"

def call(argv, timeout=210):
    outcome = subprocess.run(argv, capture_output=True, text=True, timeout=timeout)
    if outcome.returncode:
        raise ActionRefused(safe_failure_code(outcome.stdout, outcome.stderr))
    return outcome.stdout.strip()


def policy_config():
    policy = json.loads((ROOT / "config/production-generation-orchestrator.json").read_text())
    policy["enabled"] = True
    # This ephemeral policy enables only operations invoked by this workflow.
    # Existing circuit, readiness, isolation, cooldown and rollback checks remain.
    policy["events"]["manual"]["enabled"] = True
    policy["events"]["technical_failure"]["enabled"] = True
    return policy


def marker_verified(path, target, max_age):
    if path.is_symlink() or not path.is_file():
        return False
    try:
        record = json.loads(path.read_text())
        verified = dt.datetime.fromisoformat(
            record["verified_at_utc"].replace("Z", "+00:00"))
        age = dt.datetime.now(dt.timezone.utc) - verified.astimezone(dt.timezone.utc)
        return (record.get("generation") == target and
                record.get("ready") is True and
                record.get("provider_authorized") is True and
                isinstance(record.get("verified_by"), str) and
                len(record["verified_by"]) >= 3 and
                dt.timedelta(0) <= age <= dt.timedelta(seconds=max_age))
    except (OSError, ValueError, KeyError, TypeError):
        return False


def preflight(target, active, state, env_file, policy):
    marker = state / "ready" / (target + ".json")
    if not marker_verified(marker, target, int(policy["readiness_max_age_seconds"])):
        raise ActionRefused("standby_authorization_not_verified")
    # Check via the existing executor using an isolated temporary request.
    # There are no changes to the host pending queue or cooldown on a check.
    with tempfile.TemporaryDirectory(prefix="generation-actions-preflight-") as tmp:
        directory = Path(tmp)
        (directory / "ready").mkdir()
        (directory / "pending").mkdir()
        (directory / "ready" / (target + ".json")).write_bytes(marker.read_bytes())
        request_id = str(uuid.uuid4())
        (directory / "pending" / (request_id + ".json")).write_text(json.dumps({
            "id": request_id, "status": "pending", "target": target,
            "event": "manual", "requires_executor_verification": True,
        }))
        config = directory / "policy.json"
        config.write_text(json.dumps(policy))
        call([sys.executable, str(ROOT / "scripts/production-generation-executor.py"),
              "--config", str(config), "--state-dir", str(directory),
              "--env-file", str(env_file), "--request-id", request_id,
              "--mode", "check"], timeout=85)
    print(f"GENERATION_ACTIONS_PREFLIGHT_OK source={active} target={target}")


def technical_component(active, worker, e):
    project, api, _, hermes = e.generation_services(active)
    inspected = [worker, e.inspect(project, api)]
    if hermes:
        inspected.append(e.inspect(project, hermes))
    for container in inspected:
        state = container.get("State", {})
        if (state.get("Status") in ("exited", "dead", "restarting") or
                (state.get("Health") or {}).get("Status") == "unhealthy"):
            return container["Id"]
    return None


def ready_target(active, state, env_file, policy):
    ready = state / "ready"
    if not ready.is_dir() or ready.is_symlink():
        return None
    for target in numbered_targets(active, (p.stem for p in ready.glob("prod*.json"))):
        try:
            preflight(target, active, state, env_file, policy)
            return target
        except (ActionRefused, RuntimeError, OSError, ValueError,
                subprocess.SubprocessError):
            pass
    return None


def handoff(target, active, env_file, state, event, component, policy):
    with tempfile.TemporaryDirectory(prefix="generation-actions-policy-") as tmp:
        config = Path(tmp) / "policy.json"
        config.write_text(json.dumps(policy))
        result = call([
            sys.executable, str(ROOT / "scripts/production-generation-orchestrator.py"),
            "--config", str(config), "--state-dir", str(state),
            "--event", event, "--target", target, "--mode", "submit",
            "--component", component or "",
            "--confirmation", "AUTHORIZE_GENERATION_EVENT",
            "--reason", "GitHub Actions verified generation operation",
        ], timeout=35)
        event_id = str(uuid.UUID(json.loads(result)["event_id"]))
        invocation = [
            sys.executable, str(ROOT / "scripts/production-generation-executor.py"),
            "--config", str(config), "--state-dir", str(state),
            "--env-file", str(env_file), "--request-id", event_id,
        ]
        # Existing executor checks provider circuits, leases and destination.
        # If activation fails after stopping the source Worker, it rolls back.
        call(invocation + ["--mode", "check"], timeout=85)
        call(invocation + ["--mode", "apply", "--confirmation",
                           "AUTHORIZE_VERIFIED_WORKER_HANDOFF"], timeout=210)
        call(["bash", str(ROOT / "scripts/production-generation-status-sync.sh"),
              str(env_file)], timeout=75)
        confirmed, _ = executor().find_running_generation()
        if confirmed != target:
            raise ActionRefused("post_handoff_worker_identity_mismatch")
        print(f"GENERATION_ACTIONS_HANDOFF_CONFIRMED source={active} active={target} event={event_id}")


def register_generation(target, env_file):
    db = load_module("generation_actions_db",
                     "scripts/production-generation-apply-request.py")
    settings = db.parse_db(str(env_file))
    # target has already been strictly validated to ^prod[1-9][0-9]*$.
    db.sql(settings, f"""
        INSERT INTO agent.runtime_generations(name) VALUES ('{target}')
          ON CONFLICT (name) DO NOTHING;
        INSERT INTO agent.runtime_generation_provisions(generation,status)
          VALUES ('{target}','Pending') ON CONFLICT (generation) DO NOTHING;
    """, stage="actions_generation_registration")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--operation", required=True, choices=(
        "inspect", "provision-check", "provision", "reconcile",
        "preflight", "switch", "technical-recovery", "attest"))
    parser.add_argument("--target", default="")
    parser.add_argument("--env-file", required=True)
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--system-dir", default="")
    parser.add_argument("--confirmation", default="")
    parser.add_argument("--action-request-id", default="")
    args = parser.parse_args()
    env_file, state = Path(args.env_file), Path(args.state_dir).expanduser()
    if not env_file.is_file() or state.is_symlink():
        raise ActionRefused("protected_host_configuration_missing")
    e = executor()
    policy = policy_config()
    try:
        active, worker = e.find_running_generation()
    except (RuntimeError, OSError, ValueError, subprocess.SubprocessError):
        raise ActionRefused("active_worker_not_uniquely_identified")

    if args.operation == "attest":
        if not args.action_request_id:
            raise ActionRefused("web_authorization_id_required")
        if not NAME.fullmatch(args.target) or args.target == active:
            raise ActionRefused("invalid_attestation_target")
        call([
            sys.executable,
            str(ROOT / "scripts/operator-attest-generation-readiness.py"),
            "--generation", args.target,
            "--confirmation", "I_INDEPENDENTLY_VERIFIED_AUTHORIZED_PROVIDER_ACCESS",
            "--web-request-id", args.action_request_id,
            "--env-file", str(env_file),
            "--state-dir", str(state),
        ], timeout=120)
        print("GENERATION_WEB_AUTHORIZED_STANDBY=" + args.target)
        return

    if args.operation in ("provision-check", "provision"):
        target = args.target
        if not NAME.fullmatch(target) or len(target) > 64 or target == active:
            raise ActionRefused("invalid_or_active_provision_target")
        system = Path(args.system_dir)
        if not (system / "deploy/docker-compose.agent-generation.yml").is_file():
            raise ActionRefused("trusted_system_manifest_missing")
        if args.operation == "provision" and args.confirmation != "PROVISION_INDEPENDENT_GENERATION":
            raise ActionRefused("provision_confirmation_required")
        if args.operation == "provision":
            register_generation(target, env_file)
        call(["bash", str(ROOT / "scripts/provision-production-generation.sh"),
              target, "apply" if args.operation == "provision" else "check",
              str(system), str(env_file)], timeout=1000)
        if args.operation == "provision":
            call([sys.executable,
                  str(ROOT / "scripts/production-generation-provision-status.py"),
                  "--generation", target, "--status", "Provisioned",
                  "--env-file", str(env_file)], timeout=90)
            print("GENERATION_ACTIONS_DORMANT_STANDBY_PROVISIONED=" + target)
        else:
            print("GENERATION_ACTIONS_PROVISION_CHECK_OK=" + target)
        return

    if args.operation == "reconcile":
        call(["bash", str(ROOT / "scripts/production-generation-status-sync.sh"),
              str(env_file)], timeout=75)
        print("GENERATION_ACTIONS_RECONCILED=" + active)
        return

    if args.operation == "inspect":
        print("GENERATION_ACTIONS_ACTIVE_WORKER=" + active)
        print("GENERATION_ACTIONS_TECHNICAL_FAILURE=" +
              ("present" if technical_component(active, worker, e) else "absent"))
        try:
            e.database_clear(e.load_connection(env_file))
            print("GENERATION_ACTIONS_PROVIDER_CIRCUIT=clear")
        except (RuntimeError, OSError, ValueError, subprocess.SubprocessError):
            print("GENERATION_ACTIONS_PROVIDER_CIRCUIT=blocked_or_unavailable")
        return

    if args.operation == "technical-recovery":
        component = technical_component(active, worker, e)
        if not component:
            print("GENERATION_ACTIONS_AUTO_SKIP=no_verified_docker_failure")
            return
        target = ready_target(active, state, env_file, policy)
        if not target:
            print("GENERATION_ACTIONS_AUTO_SKIP=no_authorized_healthy_standby_or_provider_circuit_blocked")
            return
        handoff(target, active, env_file, state, "technical_failure", component, policy)
        return

    target = args.target
    if not NAME.fullmatch(target) or len(target) > 64 or target == active:
        raise ActionRefused("invalid_or_active_target")
    preflight(target, active, state, env_file, policy)
    if args.operation == "preflight":
        return
    if args.confirmation != "AUTHORIZE_VERIFIED_WORKER_HANDOFF":
        raise ActionRefused("operator_confirmation_required")
    handoff(target, active, env_file, state, "manual", None, policy)


if __name__ == "__main__":
    try:
        main()
    except (ActionRefused, RuntimeError, OSError, ValueError, KeyError, TypeError,
            subprocess.SubprocessError) as exc:
        code = str(exc) if isinstance(exc, ActionRefused) else type(exc).__name__
        print("GENERATION_ACTIONS_REFUSED: " + code, file=sys.stderr)
        sys.exit(1)
