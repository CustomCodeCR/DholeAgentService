#!/usr/bin/env python3
"""Prepare/close a browser-only standby viewer; never start its queue Worker.

The live queue remains in the active generation. Only the isolated, persistently
mounted standby Chromium directory is exposed through loopback-only SSH VNC.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "generation_executor", ROOT / "scripts/production-generation-executor.py")
executor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(executor)

GEN = re.compile(r"prod([2-9]|[1-9][0-9]{1,2})\Z")
CREDENTIAL = re.compile(r"[a-f0-9]{32}\Z")


def guard_generation(value):
    match = GEN.fullmatch(value or "")
    if not match:
        raise RuntimeError("invalid_standby_generation")
    index = int(value[4:])
    if index > 200:
        raise RuntimeError("generation_out_of_allowed_range")
    return index


def viewer_port(generation):
    return 6081 + guard_generation(generation)


def viewer_name(generation):
    guard_generation(generation)
    return "dhole-standby-auth-" + generation


def safe_run(args, timeout=30):
    try:
        result = subprocess.run(args, capture_output=True, text=True,
                                timeout=timeout, check=False)
    except (OSError, subprocess.SubprocessError) as e:
        raise RuntimeError("standby_docker_operation_failed") from e
    if result.returncode:
        raise RuntimeError("standby_docker_operation_failed")
    return result.stdout.strip()


def find_viewer(name):
    result = subprocess.run(["docker", "ps", "-aq", "--filter", "name=^/" + name + "$"],
                            capture_output=True, text=True, timeout=15)
    if result.returncode:
        raise RuntimeError("viewer_discovery_failed")
    ids = result.stdout.splitlines()
    if len(ids) > 1:
        raise RuntimeError("ambiguous_standby_viewer")
    return ids[0] if ids else None


def inspect_existing(generation, credential):
    name = viewer_name(generation)
    existing = find_viewer(name)
    if not existing:
        raise RuntimeError("standby_viewer_not_running")
    viewer = json.loads(safe_run(["docker", "inspect", existing], 15))[0]
    labels = viewer["Config"].get("Labels") or {}
    if labels.get("dhole.standby.auth.generation") != generation or (
        labels.get("dhole.standby.auth.credential") != credential
    ):
        raise RuntimeError("standby_viewer_identity_mismatch")
    return existing, viewer


def guard_standby(generation):
    active, active_worker = executor.find_running_generation()
    if active == generation:
        raise RuntimeError("target_already_active")
    project, api_name, worker_name, hermes_name = executor.generation_services(generation)
    standby = executor.inspect(project, worker_name)
    api = executor.inspect(project, api_name)
    hermes = executor.inspect(project, hermes_name)
    if standby["State"]["Status"] not in ("created", "exited"):
        raise RuntimeError("standby_worker_must_remain_stopped")
    if not executor.healthy(api) or not executor.healthy(hermes):
        raise RuntimeError("standby_infrastructure_not_healthy")
    source_mount = executor.mount_name(active_worker, "/data/browser-profiles")
    target_mount = executor.mount_name(standby, "/data/browser-profiles")
    if source_mount == target_mount or target_mount[0] != "volume":
        raise RuntimeError("standby_browser_volume_not_isolated")
    if standby["Image"] != active_worker["Image"]:
        raise RuntimeError("standby_worker_image_mismatch")
    return standby, target_mount[1]


def prepare(generation, credential, script_path):
    guard_generation(generation)
    if not CREDENTIAL.fullmatch(credential):
        raise RuntimeError("credential_id_invalid")
    if script_path.is_symlink() or not script_path.is_file():
        raise RuntimeError("trusted_viewer_script_missing")
    worker, volume = guard_standby(generation)
    name = viewer_name(generation)
    existing = find_viewer(name)
    if existing:
        # Never terminate a different existing viewer or reuse its profile.
        _, info = inspect_existing(generation, credential)
        if info["State"]["Status"] != "exited":
            raise RuntimeError("standby_viewer_already_exists")
        safe_run(["docker", "rm", existing])
    port = viewer_port(generation)
    # Docker publishes only 127.0.0.1, reachable to the operator by SSH.
    safe_run([
        "docker", "run", "--detach", "--pull=never", "--name", name,
        "--restart", "no", "--stop-timeout", "25",
        "--label", "dhole.standby.auth.generation=" + generation,
        "--label", "dhole.standby.auth.credential=" + credential,
        "--publish", f"127.0.0.1:{port}:6080",
        "--network", "dhole", "--shm-size", "2g",
        "--security-opt", "no-new-privileges",
        "--mount", "type=volume,source=" + volume + ",target=/data/browser-profiles",
        "--mount", "type=bind,source=" + str(script_path.resolve()) + ",target=/app/start-standby-auth.sh,readonly",
        "--entrypoint", "/bin/bash",
        worker["Config"]["Image"],
        "/app/start-standby-auth.sh", credential
    ], 70)
    for _ in range(30):
        _, info = inspect_existing(generation, credential)
        if info["State"]["Status"] != "running":
            raise RuntimeError("standby_viewer_failed_to_start")
        try:
            p = subprocess.run([
                "curl", "-fsS", "--connect-timeout", "1", "--max-time", "2",
                "--output", "/dev/null", f"http://127.0.0.1:{port}/vnc.html"
            ], capture_output=True, timeout=4)
            if p.returncode == 0:
                print("STANDBY_AUTH_VIEWER_READY generation=" + generation)
                print("STANDBY_AUTH_HOST_LOOPBACK_PORT=" + str(port))
                print("STANDBY_AUTH_WORKER_REMAINS_INACTIVE")
                return
        except (OSError, subprocess.SubprocessError):
            pass
        time.sleep(1)
    raise RuntimeError("standby_viewer_health_timeout")


def finish(generation, credential):
    guard_standby(generation)
    name = viewer_name(generation)
    identity, viewer = inspect_existing(generation, credential)
    if viewer["State"]["Status"] != "running":
        raise RuntimeError("standby_viewer_no_longer_running")
    # Only stop/remove this ephemeral viewer container. Docker volume, profile,
    # existing Worker and every other generation remain completely intact.
    safe_run(["docker", "stop", "--time", "25", identity], 45)
    safe_run(["docker", "rm", identity], 25)
    print("STANDBY_AUTH_VIEWER_CLOSED_NO_PROFILE_DELETION")
    print("STANDBY_AUTH_WORKER_REMAINS_INACTIVE")



def inspect_account(generation, credential, output):
    """Read only the expanded Account panel in this generation's live Chrome."""
    guard_standby(generation)
    container_id, viewer = inspect_existing(generation, credential)
    if viewer["State"]["Status"] != "running":
        raise RuntimeError("standby_viewer_no_longer_running")
    # Never print this command's stdout/stderr: they may include account details.
    outcome = subprocess.run(
        ["docker", "exec", container_id, "dotnet",
         "/app/Dhole.Agent.Workers.dll", "--standby-account-inspect"],
        capture_output=True, text=True, timeout=30, check=False)
    if outcome.returncode != 0:
        raise RuntimeError("standby_account_not_visible_in_official_menu")
    identity = json.loads(outcome.stdout)
    if not isinstance(identity, dict) or sorted(identity) != [
            "CustomerId", "DisplayName", "Username"]:
        raise RuntimeError("invalid_standby_identity")
    if not isinstance(identity["DisplayName"], str) or not 4 <= len(identity["DisplayName"]) <= 100:
        raise RuntimeError("invalid_standby_identity")
    if not re.fullmatch(r"[0-9]{8,15}", identity["CustomerId"]) or (
            not re.fullmatch(r"[A-Za-z0-9_.-]{3,64}", identity["Username"])):
        raise RuntimeError("invalid_standby_identity")
    if output.is_symlink() or output.exists():
        raise RuntimeError("unsafe_identity_output")
    fd = output.open("x", encoding="utf-8")
    try:
        output.chmod(0o600)
        with fd:
            json.dump(identity, fd, ensure_ascii=False)
    finally:
        if output.is_file():
            output.chmod(0o600)
    print("STANDBY_MAERSK_ACCOUNT_DETECTED")
    print("STANDBY_AUTH_WORKER_REMAINS_INACTIVE")


def restart_viewer(generation, credential, script_path):
    """Reopen the same persisted profile with local-only CDP; never copy cookies."""
    guard_standby(generation)
    container_id, viewer = inspect_existing(generation, credential)
    if viewer["State"]["Status"] == "running":
        safe_run(["docker", "stop", "--time", "25", container_id], 45)
    # The prepare function verifies the exact viewer identity before removing it.
    prepare(generation, credential, script_path)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--operation", choices=("prepare", "restart", "inspect", "finish"), required=True)
    parser.add_argument("--generation", required=True)
    parser.add_argument("--credential", required=True)
    parser.add_argument("--identity-output", default="")
    args = parser.parse_args()
    guard_generation(args.generation)
    if not CREDENTIAL.fullmatch(args.credential):
        raise RuntimeError("credential_id_invalid")
    if args.operation == "prepare":
        prepare(args.generation, args.credential, ROOT / "docker/start-standby-auth.sh")
    elif args.operation == "restart":
        restart_viewer(args.generation, args.credential, ROOT / "docker/start-standby-auth.sh")
    elif args.operation == "inspect":
        if not args.identity_output:
            raise RuntimeError("identity_output_required")
        inspect_account(args.generation, args.credential, Path(args.identity_output))
    else:
        finish(args.generation, args.credential)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, ValueError, KeyError, TypeError,
            subprocess.SubprocessError):
        # No Docker inspect data, credentials or raw provider output in logs.
        print("STANDBY_AUTH_OPERATION_REFUSED: safety_preflight_or_runtime_failed",
              file=sys.stderr)
        sys.exit(2)
