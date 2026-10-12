#!/usr/bin/env python3
"""Read-only diagnostics for preexisting operational handoff gates.

Never creates provider readiness, changes circuit states, accesses browser
profiles, starts or stops Docker containers, or exposes credentials.
"""
import argparse
import datetime as dt
import importlib.util
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "generation_executor", ROOT / "scripts/production-generation-executor.py")
executor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(executor)


def status(gate, result):
    print(f"GENERATION_HANDOFF_GATE {gate}={result}")


def verified_readiness(path, target, policy):
    if path.is_symlink() or not path.is_file():
        status("operator_readiness", "missing")
        return False
    try:
        record = json.loads(path.read_text())
        if not isinstance(record, dict):
            raise ValueError("bad format")
        if record.get("generation") != target or record.get("ready") is not True:
            status("operator_readiness", "generation_or_readiness_mismatch")
            return False
        if (record.get("provider_authorized") is not True
                or not isinstance(record.get("verified_by"), str)
                or len(record["verified_by"]) < 3):
            status("operator_readiness", "provider_authorization_not_attested")
            return False
        age = dt.datetime.now(dt.timezone.utc) - executor.parse_time(
            record["verified_at_utc"])
        max_age = int(policy["readiness_max_age_seconds"])
        if not dt.timedelta(0) <= age <= dt.timedelta(seconds=max_age):
            status("operator_readiness", "expired")
            return False
        status("operator_readiness", "present_and_fresh")
        return True
    except (ValueError, KeyError, TypeError, OSError, RuntimeError):
        status("operator_readiness", "invalid_record")
        return False


def inspect_pair(target, active):
    try:
        proj, api_service, worker_service, hermes_service = executor.generation_services(target)
        api = executor.inspect(proj, api_service)
        worker = executor.inspect(proj, worker_service)
        hermes = (executor.inspect("dhole-infrastructure", "hermes-agent")
                  if hermes_service is None else executor.inspect(proj, hermes_service))
        status("destination_api_hermes", "healthy" if executor.healthy(api) and executor.healthy(hermes) else "unhealthy")
        status("destination_worker", worker["State"]["Status"])
        source_proj, source_api, _, _ = executor.generation_services(active[0])
        source_api_container = executor.inspect(source_proj, source_api)
        status("worker_image_revision", "matches" if active[1]["Image"] == worker["Image"] else "different")
        status("api_image_revision", "matches" if source_api_container["Image"] == api["Image"] else "different")
        source_env, worker_env = executor.container_env(active[1]), executor.container_env(worker)
        for key, identifier in [
            ("Postgres__ConnectionString", "postgres"),
            ("Redis__ConnectionString", "redis"),
            ("CredentialProtection__KeyRingPath", "keyring_path")
        ]:
            matched = bool(source_env.get(key)) and source_env[key] == worker_env.get(key)
            status(identifier + "_configuration", "matches" if matched else "different_or_missing")
        for key, identifier in [
            ("MaerskCircuit__Enabled", "maersk_circuit"),
            ("MaerskMonitoring__Enabled", "maersk_monitoring")
        ]:
            good = source_env.get(key, "").lower() == "true" and worker_env.get(key, "").lower() == "true"
            status(identifier, "enabled_both" if good else "disabled_or_missing")
        same_mode = source_env.get("AgentQueue__ConcurrentDispatcherEnabled", "").lower() == worker_env.get(
            "AgentQueue__ConcurrentDispatcherEnabled", "").lower()
        status("queue_mode", "matches" if same_mode else "different")
        for destination, identifier in [
            ("/data/agent-keys", "keyring_volume"),
            ("/data/browser-profiles", "browser_profile_volume")
        ]:
            try:
                same = executor.mount_name(active[1], destination) == executor.mount_name(worker, destination)
                status(identifier, "shared_not_permitted" if same else "isolated")
            except RuntimeError:
                status(identifier, "missing")
    except (RuntimeError, OSError, ValueError, KeyError, TypeError):
        status("docker_operational_check", "failed")
        return


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--env-file", required=True)
    args = parser.parse_args()
    state = Path(args.state_dir).expanduser()
    policy = json.loads((ROOT / "config/production-generation-orchestrator.json").read_text())
    try:
        active = executor.find_running_generation()
        status("active_generation", active[0])
    except (RuntimeError, OSError, ValueError, KeyError, TypeError):
        active = None
        status("active_generation", "not_uniquely_resolved")
    try:
        db_config = executor.load_connection(args.env_file)
        executor.database_clear(db_config)
        status("provider_circuit_and_running_jobs", "clear")
    except (RuntimeError, OSError, ValueError, KeyError, TypeError):
        status("provider_circuit_and_running_jobs", "blocked_or_unavailable")
    for target in ("prod1", "prod2"):
        print(f"GENERATION_HANDOFF_DIAGNOSTIC_START target={target}")
        verified_readiness(state / "ready" / (target + ".json"), target, policy)
        if active is not None:
            inspect_pair(target, active)
        print(f"GENERATION_HANDOFF_DIAGNOSTIC_END target={target}")


if __name__ == "__main__":
    main()
