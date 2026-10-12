#!/usr/bin/env python3
"""Manual, independent provider authorization attestation for an existing standby.

Must only run as an operator-dispatched production workflow AFTER the human has
independently confirmed authorized provider access for this exact generation.
Never logs in to the provider, bypasses a challenge, changes a session, or
starts/stops a Worker. No attestation is written if technical gates fail.
"""
import argparse
import datetime as dt
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
CONFIRMATION = "I_INDEPENDENTLY_VERIFIED_AUTHORIZED_PROVIDER_ACCESS"


def refuse(code):
    print("GENERATION_READINESS_REFUSED: " + code, file=sys.stderr)
    raise SystemExit(1)


def verified_health_url(url):
    # Independently confirm real HTTP health; container running is not enough.
    try:
        result = subprocess.run(
            ["docker", "run", "--rm", "--pull=never", "--network", "dhole",
             "curlimages/curl:8.12.1", "--silent", "--fail", "--output", "/dev/null",
             "--connect-timeout", "3", "--max-time", "6", url],
            capture_output=True, text=True, timeout=15, check=False)
        return result.returncode == 0
    except (OSError, subprocess.SubprocessError):
        return False


def check_infrastructure(executor, target, env_file):
    try:
        active, current_worker = executor.find_running_generation()
        if active == target:
            refuse("target_already_active")
        source_project, source_api_name, _, _ = executor.generation_services(active)
        target_project, target_api_name, target_worker_name, target_hermes_name = (
            executor.generation_services(target))
        source_api = executor.inspect(source_project, source_api_name)
        target_api = executor.inspect(target_project, target_api_name)
        target_worker = executor.inspect(target_project, target_worker_name)
        target_hermes = executor.inspect(target_project, target_hermes_name)
    except (RuntimeError, OSError, ValueError, KeyError, TypeError,
            subprocess.SubprocessError):
        refuse("container_discovery_failed")

    if not (executor.healthy(source_api) and executor.healthy(target_api)
            and executor.healthy(target_hermes)):
        refuse("api_or_hermes_unhealthy")
    if target_worker["State"]["Status"] not in ("created", "exited"):
        refuse("target_worker_must_remain_stopped")
    if current_worker["State"]["Status"] != "running":
        refuse("source_worker_not_running")
    if target_worker["Image"] != current_worker["Image"]:
        refuse("worker_image_revision_mismatch")
    if target_api["Image"] != source_api["Image"]:
        refuse("api_image_revision_mismatch")

    original = executor.container_env(current_worker)
    standby = executor.container_env(target_worker)
    for name in ("Postgres__ConnectionString", "Redis__ConnectionString",
                 "CredentialProtection__KeyRingPath"):
        if not original.get(name) or original[name] != standby.get(name):
            refuse("runtime_configuration_mismatch")
    for name in ("MaerskCircuit__Enabled", "MaerskMonitoring__Enabled"):
        if (original.get(name, "").lower() != "true"
                or standby.get(name, "").lower() != "true"):
            refuse("circuit_or_monitoring_disabled")
    if (original.get("AgentQueue__ConcurrentDispatcherEnabled", "").lower() !=
            standby.get("AgentQueue__ConcurrentDispatcherEnabled", "").lower()):
        refuse("queue_dispatch_mode_mismatch")

    try:
        for mount in ("/data/agent-keys", "/data/browser-profiles"):
            if executor.mount_name(current_worker, mount) == executor.mount_name(target_worker, mount):
                refuse("persistent_volume_not_isolated")
    except (RuntimeError, OSError, ValueError):
        refuse("persistent_mount_missing")

    api_host = "dhole-agent-api-prod1" if target == "prod1" else f"agent-api-{target}"
    hermes_host = "hermes-agent-prod1" if target == "prod1" else f"hermes-agent-{target}"
    if not verified_health_url(f"http://{api_host}:8080/health"):
        refuse("api_http_health_failed")
    if not verified_health_url(f"http://{hermes_host}:8642/health"):
        refuse("hermes_http_health_failed")
    try:
        executor.database_clear(executor.load_connection(env_file))
    except (RuntimeError, OSError, ValueError, KeyError, TypeError,
            subprocess.SubprocessError):
        refuse("provider_circuit_or_running_jobs_blocked")
    print("GENERATION_READINESS_TECHNICAL_GATES_PASSED generation=" + target)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--generation", required=True)
    parser.add_argument("--confirmation", required=True)
    parser.add_argument("--env-file", required=True)
    parser.add_argument("--state-dir", required=True)
    args = parser.parse_args()

    actor = os.environ.get("GITHUB_ACTOR", "")
    if (os.environ.get("GITHUB_EVENT_NAME") != "workflow_dispatch"
            or os.environ.get("GITHUB_REF") != "refs/heads/master"
            or not re.fullmatch(r"[A-Za-z0-9-]{3,39}", actor)):
        refuse("manual_master_operator_workflow_required")
    if args.confirmation != CONFIRMATION:
        refuse("independent_provider_verification_not_attested")
    if args.generation not in ("prod1", "prod2"):
        refuse("unsupported_target")

    spec = importlib.util.spec_from_file_location(
        "standby_executor", ROOT / "scripts/production-generation-executor.py")
    executor = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(executor)
    check_infrastructure(executor, args.generation, args.env_file)

    # Attestation represents the operator's independent provider verification.
    # Only that operator may trigger this step; technical health cannot prove it.
    root = Path(args.state_dir).expanduser()
    if root.is_symlink():
        refuse("unsafe_state_directory")
    directory = root / "ready"
    if directory.is_symlink():
        refuse("unsafe_readiness_directory")
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(directory, 0o700)
    path = directory / (args.generation + ".json")
    if path.is_symlink():
        refuse("unsafe_existing_readiness_record")
    record = {
        "generation": args.generation,
        "ready": True,
        "provider_authorized": True,
        "verified_by": actor,
        "verified_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
    }
    fd, temporary = tempfile.mkstemp(prefix=".operator-", suffix=".tmp", dir=directory)
    try:
        with os.fdopen(fd, "w") as output:
            json.dump(record, output, sort_keys=True)
            output.write("\n")
        os.chmod(temporary, 0o600)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    print("GENERATION_OPERATOR_ATTESTATION_RECORDED generation=" + args.generation)
    print("GENERATION_READINESS_VALIDITY_SECONDS=3600")
    print("GENERATION_READINESS_OPERATOR=" + actor)
    print("GENERATION_HANDOFF_NOT_EXECUTED")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        refuse("readiness_attestation_failed")
