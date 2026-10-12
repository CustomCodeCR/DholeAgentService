#!/usr/bin/env python3
"""Synchronize ONLY a stopped, pre-provisioned Agent standby with active images.

A release can refresh prod without refreshing prod1/prod2. This protected
operation re-creates standby API + dormant Worker from the verified local images,
never starts another Worker, never alters Chromium/key/Hermes volumes and
invalidates old operator attestations before changing any container.
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
import uuid

ROOT = Path(__file__).resolve().parents[1]
CONFIRMATION = "SYNC_DORMANT_STANDBY_IMAGES"


def fail(code):
    print("GENERATION_STANDBY_SYNC_REFUSED: " + code, file=sys.stderr)
    raise SystemExit(1)


def load_helper(filename, name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run(args, env=None, timeout=90):
    try:
        outcome = subprocess.run(args, text=True, capture_output=True, env=env,
                                 timeout=timeout, check=False)
    except (OSError, subprocess.SubprocessError):
        fail("docker_command_unavailable")
    if outcome.returncode:
        # Never expose command output, configuration or provider tokens.
        fail("docker_operation_failed")
    return outcome.stdout.strip()


def image_id(tag):
    value = run(["docker", "image", "inspect", "--format", "{{.Id}}", tag], timeout=15)
    if not value.startswith("sha256:"):
        fail("approved_local_image_missing")
    return value


def require_state(executor, source, api, worker, hermes):
    if source["State"]["Status"] != "running":
        fail("primary_worker_not_running")
    if not executor.healthy(api) or not executor.healthy(hermes):
        fail("standby_services_not_healthy")
    if worker["State"]["Status"] not in ("created", "exited"):
        fail("standby_worker_must_remain_inactive")


def check_runtime(executor, primary, standby):
    a, b = executor.container_env(primary), executor.container_env(standby)
    for key in ("Postgres__ConnectionString", "Redis__ConnectionString",
                "CredentialProtection__KeyRingPath"):
        if not a.get(key) or a[key] != b.get(key):
            fail("standby_runtime_configuration_mismatch")
    for key in ("MaerskCircuit__Enabled", "MaerskMonitoring__Enabled"):
        if a.get(key, "").lower() != "true" or b.get(key, "").lower() != "true":
            fail("standby_protection_disabled")
    if a.get("AgentQueue__ConcurrentDispatcherEnabled", "").lower() != b.get(
            "AgentQueue__ConcurrentDispatcherEnabled", "").lower():
        fail("standby_queue_dispatch_mode_mismatch")


def inspect_state(executor, target):
    active, primary_worker = executor.find_running_generation()
    if active != "prod":
        fail("only_prod_primary_is_supported")
    source_project, source_api_service, _, _ = executor.generation_services(active)
    project, api_service, worker_service, hermes_service = executor.generation_services(target)
    primary_api = executor.inspect(source_project, source_api_service)
    api = executor.inspect(project, api_service)
    worker = executor.inspect(project, worker_service)
    hermes = executor.inspect(project, hermes_service)
    require_state(executor, primary_worker, api, worker, hermes)
    if not executor.healthy(primary_api):
        fail("primary_api_unhealthy")
    # Avoid propagating an old 'latest' tag when a protected deploy just failed.
    if (image_id("dhole/agent-workers:latest") != primary_worker["Image"] or
            image_id("dhole/agent-api:latest") != primary_api["Image"]):
        fail("local_latest_not_equal_to_running_primary")
    check_runtime(executor, primary_worker, worker)
    mounts = {
        "worker_browser": executor.mount_name(worker, "/data/browser-profiles"),
        "worker_keys": executor.mount_name(worker, "/data/agent-keys"),
        "api_keys": executor.mount_name(api, "/data/agent-keys"),
        "hermes_state": executor.mount_name(hermes, "/opt/data")
    }
    if mounts["worker_browser"] == executor.mount_name(primary_worker, "/data/browser-profiles"):
        fail("standby_browser_volume_not_isolated")
    if mounts["worker_keys"] == executor.mount_name(primary_worker, "/data/agent-keys"):
        fail("standby_keyring_not_isolated")
    if mounts["api_keys"] != mounts["worker_keys"]:
        fail("standby_api_worker_keyring_mismatch")
    return {
        "source_worker": primary_worker, "source_api": primary_api,
        "worker": worker, "api": api, "hermes": hermes,
        "mounts": mounts, "project": project, "worker_service": worker_service,
        "api_service": api_service
    }


def invalidate_readiness(state_dir, target, env_file, database_helper):
    # A container change invalidates the earlier technical check. Preserve the
    # old record in a private archive for audit, never reuse it for handoff.
    root = Path(state_dir).expanduser()
    ready = root / "ready"
    marker = ready / (target + ".json")
    if root.is_symlink() or ready.is_symlink() or marker.is_symlink():
        fail("unsafe_readiness_path")
    if marker.exists():
        archive = ready / "superseded"
        if archive.is_symlink():
            fail("unsafe_readiness_archive")
        archive.mkdir(mode=0o700, exist_ok=True)
        os.chmod(archive, 0o700)
        stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        marker.rename(archive / (target + "-" + stamp + "-" + uuid.uuid4().hex + ".json"))
    # A missing local marker is already safe. PostgreSQL must also stop
    # advertising a previously confirmed authorization.
    db = database_helper.parse_db(env_file)
    result = database_helper.sql(db, f"""
        UPDATE agent.runtime_generation_readiness
        SET ready=false, provider_authorized=false
        WHERE generation='{target}';
        SELECT 'READINESS_INVALIDATED';
    """, stage="standby_image_sync_invalidate")
    if result.splitlines()[-1] != "READINESS_INVALIDATED":
        fail("database_readiness_not_invalidated")
    print("GENERATION_STANDBY_AUTHORIZATION_INVALIDATED target=" + target)


def verify_http(target):
    api_host = "dhole-agent-api-prod1" if target == "prod1" else "agent-api-" + target
    address = "http://" + api_host + ":8080/health"
    # Probe from the existing protected runtime network, not public ingress.
    for _ in range(20):
        try:
            outcome = subprocess.run([
                "docker", "run", "--rm", "--pull=never", "--network", "dhole",
                "curlimages/curl:8.12.1", "--fail", "--silent", "--output", "/dev/null",
                "--connect-timeout", "2", "--max-time", "4", address
            ], capture_output=True, text=True, timeout=12)
            if outcome.returncode == 0:
                return
        except (OSError, subprocess.SubprocessError):
            pass
    fail("refreshed_standby_api_http_unhealthy")


def sync(args):
    if args.generation not in ("prod1", "prod2"):
        fail("unsupported_generation")
    if (os.environ.get("GITHUB_REF") != "refs/heads/master" or
            os.environ.get("GITHUB_EVENT_NAME") not in ("push", "workflow_dispatch")):
        fail("protected_master_release_required")
    if args.mode == "apply" and args.confirmation != CONFIRMATION:
        fail("explicit_sync_confirmation_required")
    if not Path(args.env_file).is_file():
        fail("protected_environment_missing")
    executor = load_helper("production-generation-executor.py", "generation_executor")
    before = inspect_state(executor, args.generation)

    manifest = ("docker-compose.agent-prod1.yml" if args.generation == "prod1"
                else "docker-compose.agent-generation.yml")
    manifest_path = Path(args.system) / "deploy" / manifest
    if not manifest_path.is_file():
        fail("trusted_compose_manifest_missing")
    environment = dict(os.environ)
    environment.update(AGENT_GENERATION=args.generation, DHOLE_NETWORK="dhole",
                       DHOLE_ENV_FILE=str(Path(args.env_file).resolve()),
                       DHOLE_IMAGE_TAG="latest")
    compose = ["docker", "compose", "--env-file", args.env_file,
               "-f", str(manifest_path), "-p", before["project"],
               "--profile", "activated"]
    run(compose + ["config", "-q"], env=environment, timeout=30)
    mismatch = (before["worker"]["Image"] != before["source_worker"]["Image"] or
                before["api"]["Image"] != before["source_api"]["Image"])
    print("GENERATION_STANDBY_IMAGE_PREFLIGHT target=" + args.generation +
          " mismatched=" + str(mismatch).lower() + " active_worker=prod")
    if args.mode == "check":
        return
    if not mismatch:
        print("GENERATION_STANDBY_IMAGES_ALREADY_MATCH; no changes made")
        return

    # Must be invalidated BEFORE any change. A fresh independent human
    # attestation will be required after the technical refresh.
    database_helper = load_helper("production-generation-apply-request.py", "generation_apply")
    invalidate_readiness(args.state_dir, args.generation, args.env_file, database_helper)

    # Upgrading a standby must NEVER modify the primary Worker or start a
    # second one. --no-start is mandatory for the dormant standby Worker.
    run(compose + ["up", "--no-start", "--no-deps", "--no-build",
                   "--pull", "never", "--force-recreate",
                   before["worker_service"]], env=environment, timeout=90)
    intermediate = executor.inspect(before["project"], before["worker_service"])
    if intermediate["State"]["Status"] not in ("created", "exited"):
        fail("standby_worker_accidentally_started")
    run(compose + ["up", "-d", "--no-deps", "--no-build",
                   "--pull", "never", "--force-recreate",
                   before["api_service"]], env=environment, timeout=90)

    after = inspect_state(executor, args.generation)
    if (after["worker"]["Image"] != after["source_worker"]["Image"] or
            after["api"]["Image"] != after["source_api"]["Image"]):
        fail("standby_image_still_mismatched")
    if after["mounts"] != before["mounts"]:
        fail("persistent_mounts_changed")
    if after["hermes"]["Id"] != before["hermes"]["Id"]:
        fail("hermes_container_changed")
    if (after["source_worker"]["Id"] != before["source_worker"]["Id"] or
            after["source_api"]["Id"] != before["source_api"]["Id"]):
        fail("production_primary_changed_during_sync")
    verify_http(args.generation)
    print("GENERATION_STANDBY_IMAGES_SYNCHRONIZED target=" + args.generation)
    print("GENERATION_STANDBY_WORKER_INACTIVE")
    print("GENERATION_FRESH_OPERATOR_ATTESTATION_REQUIRED")
    print("GENERATION_HANDOFF_NOT_EXECUTED")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--generation", required=True)
    parser.add_argument("--system", required=True)
    parser.add_argument("--env-file", required=True)
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--mode", choices=("check", "apply"), required=True)
    parser.add_argument("--confirmation", default="")
    opts = parser.parse_args()
    try:
        sync(opts)
    except SystemExit:
        raise
    except (RuntimeError, ValueError, OSError, TypeError, KeyError,
            subprocess.SubprocessError):
        fail("standby_sync_preflight_or_execution_failed")
