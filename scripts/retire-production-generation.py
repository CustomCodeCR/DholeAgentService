#!/usr/bin/env python3
"""Remove an obsolete numbered Agent generation after a verified Worker handoff.

This deliberately never resets Maersk's global provider circuit or deletes
shared credentials, executions, schedules, or audit events.
"""
import argparse
import importlib.util
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class RetirementRefused(Exception):
    pass


def load_module(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def generation_number(generation):
    if generation == "prod":
        return 0
    if not isinstance(generation, str) or not re.fullmatch(r"prod[1-9][0-9]*", generation) or len(generation) > 64:
        raise RetirementRefused("invalid_generation")
    return int(generation[4:])


def names(source):
    if source == "prod1":
        return ("dhole-agent-prod1",
                {"dhole-agent-api-prod1", "dhole-agent-workers-prod1",
                 "hermes-agent-prod1", "hermes-config-init-prod1", "hermes-config-init"},
                { "dhole-agent-prod1_agent-browser-profiles-prod1",
                  "dhole-agent-prod1_hermes-prod1-data", "dhole_agent_prod1_keys"})
    return (f"dhole-agent-{source}",
            {"dhole-agent-api", "dhole-agent-workers", "hermes-agent", "hermes-config-init"},
            {f"dhole-agent-{source}_browser-profiles",
             f"dhole-agent-{source}_hermes-state",
             f"dhole_agent_{source}_keys"})


def docker(*args, allow_missing=False):
    r = subprocess.run(["docker", *args], capture_output=True, text=True, timeout=40)
    if r.returncode and not allow_missing:
        raise RetirementRefused("docker_preflight_failed")
    return r.stdout.strip() if r.returncode == 0 else ""


def inspect(container):
    return json.loads(docker("inspect", container))[0]


def active_owner():
    executor = load_module("generation_executor", "scripts/production-generation-executor.py")
    generation, data = executor.find_running_generation()
    return generation, data


def source_containers(project, allowed):
    ids = docker("ps", "-aq", "--filter", f"label=com.docker.compose.project={project}").splitlines()
    for container_id in ids:
        data = inspect(container_id)
        labels = data["Config"]["Labels"] or {}
        service = labels.get("com.docker.compose.service")
        if labels.get("com.docker.compose.project") != project or service not in allowed:
            raise RetirementRefused("unexpected_retired_container")
        if "workers" in service and data["State"]["Status"] == "running":
            raise RetirementRefused("source_worker_still_running")
    return ids


def all_volume_candidates(project, allowed):
    names = set(docker("volume", "ls", "-q", "--filter",
                       f"label=com.docker.compose.project={project}").splitlines())
    # The keyring was created externally, not through docker compose.
    for name in allowed:
        if docker("volume", "inspect", name, allow_missing=True):
            names.add(name)
    if not names.issubset(allowed):
        raise RetirementRefused("unexpected_retired_volume")
    return names


def confirm_no_other_owners(volumes, retired_container_ids):
    if not volumes:
        return
    other_containers = docker("ps", "-aq").splitlines()
    for container_id in other_containers:
        if container_id in retired_container_ids:
            continue
        data = inspect(container_id)
        if any(m.get("Type") == "volume" and m.get("Name") in volumes
               for m in data.get("Mounts", [])):
            raise RetirementRefused("retired_volume_still_used_elsewhere")


def delete_generation_rows(db, source, sql):
    # Shared provider circuits and audit rows remain intact. Only the retired
    # generation's owned runtime metadata and legacy original profile are erased.
    escaped = source  # validated against a strict generation regex above
    statement = f"""
    BEGIN;
    DO $$
    BEGIN
      IF EXISTS (SELECT 1 FROM agent.runtime_generation_state
                 WHERE singleton=true AND active_generation='{escaped}') THEN
        RAISE EXCEPTION 'Cannot retire active generation';
      END IF;
    END $$;
    DELETE FROM agent."BrowserProfiles"
    WHERE provider_id IN (SELECT id FROM agent."AgentProviders" WHERE code='MAERSK')
      AND (profile_key LIKE 'maersk-{escaped}-%'
           OR ('{escaped}'='prod1' AND
               (profile_key LIKE 'maersk-original-%'
                OR name = 'Maersk prod1 - original Chromium profile')));
    DELETE FROM agent.runtime_generation_auth_sessions WHERE generation='{escaped}';
    DELETE FROM agent.runtime_generation_readiness WHERE generation='{escaped}';
    DELETE FROM agent.runtime_generation_requests
      WHERE target_generation='{escaped}' AND status NOT IN ('Pending','Processing');
    DELETE FROM agent.runtime_generation_provisions WHERE generation='{escaped}';
    DELETE FROM agent.runtime_generations WHERE name='{escaped}';
    COMMIT;
    """
    sql(db, statement, stage="retired_generation_cleanup")


def run(source, env_file, apply, confirmation):
    if generation_number(source) == 0:
        raise RetirementRefused("legacy_prod_is_not_retirable")
    apply_module = load_module("generation_apply", "scripts/production-generation-apply-request.py")
    db = apply_module.parse_db(env_file)
    database_generation = apply_module.sql(
        db, "SELECT active_generation FROM agent.runtime_generation_state WHERE singleton=true;",
        stage="read_active_generation")
    actual_generation, running = active_owner()
    if (actual_generation != database_generation or
            generation_number(actual_generation) <= generation_number(source) or
            running["State"]["Status"] != "running"):
        raise RetirementRefused("active_generation_not_verified")

    in_flight = apply_module.sql(
        db, "SELECT count(*) FROM agent.runtime_generation_requests WHERE status IN ('Pending','Processing');",
        stage="verify_no_pending_handoff")
    if in_flight != "0":
        raise RetirementRefused("handoff_still_pending")

    project, allowed_services, allowed_volumes = names(source)
    containers = source_containers(project, allowed_services)
    volumes = all_volume_candidates(project, allowed_volumes)
    confirm_no_other_owners(volumes, containers)
    if not apply:
        print(f"RETIREMENT_CHECK_OK source={source} active={actual_generation} containers={len(containers)} volumes={len(volumes)}")
        return
    if confirmation != "DELETE_RETIRED_GENERATION_DATA":
        raise RetirementRefused("explicit_confirmation_required")

    # Erase only the retired project, never use docker compose down -v or
    # wildcard volume cleanup. Any unexpected external mount refuses deletion.
    for container_id in containers:
        docker("rm", "-f", container_id)
    confirm_no_other_owners(volumes, [])
    for volume in sorted(volumes):
        docker("volume", "rm", volume)

    # Revalidate ownership before removing the source's DB records.
    again, data = active_owner()
    if again != actual_generation or data["State"]["Status"] != "running":
        raise RetirementRefused("active_worker_changed_during_retirement")
    delete_generation_rows(db, source, apply_module.sql)
    print(f"RETIRED_GENERATION_REMOVED source={source} active={actual_generation}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("--env-file", required=True)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--confirmation", default="")
    args = parser.parse_args()
    try:
        run(args.source, args.env_file, args.apply, args.confirmation)
    except (RetirementRefused, OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        reason = str(error) if isinstance(error, RetirementRefused) else type(error).__name__
        print("GENERATION_RETIREMENT_REFUSED: " + reason, file=sys.stderr)
        sys.exit(1)
