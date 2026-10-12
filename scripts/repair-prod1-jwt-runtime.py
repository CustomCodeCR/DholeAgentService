#!/usr/bin/env python3
"""Restore prod1 API and Worker from the trusted active primary JWT config.

Only currently deployed images are reused. Database is read-only during
preflight, no browser profiles/cookies/keyrings are modified, and the
original host env is never changed. No JWT or connection string is logged.
"""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

PROJECT = "dhole-agent-prod1"
SERVICES = ("dhole-agent-api-prod1", "dhole-agent-workers-prod1")
JWT = ("Auth__Jwt__Issuer", "Auth__Jwt__Audience",
       "Auth__Jwt__SecretKey", "Auth__Jwt__ExpirationMinutes")


def safe_run(command, *, env=None, timeout=40, check=True):
    result = subprocess.run(command, env=env, text=True, capture_output=True,
                            timeout=timeout)
    if check and result.returncode:
        raise RuntimeError("Guarded subprocess failed (output redacted)")
    return result


def find(project, service, *, running=False):
    result = safe_run(["docker", "ps", "-q" if running else "-aq",
                      "--filter", "label=com.docker.compose.project=" + project,
                      "--filter", "label=com.docker.compose.service=" + service])
    ids = result.stdout.splitlines()
    if len(ids) != 1:
        raise RuntimeError("Expected exactly one service container: " + project + "/" + service)
    return ids[0]


def inspect(container):
    return json.loads(safe_run(["docker", "inspect", container]).stdout)[0]


def environment(info):
    return dict(entry.split("=", 1) for entry in info["Config"]["Env"] if "=" in entry)


def mounts(info):
    return {x["Destination"]: (x.get("Type"), x.get("Name"), x.get("Source"))
            for x in info["Mounts"]
            if x["Destination"] in ("/data/browser-profiles", "/data/agent-keys")}


def assert_drain(primary):
    values = environment(primary)
    conn = dict(part.split("=", 1) for part in
                values.get("Postgres__ConnectionString", "").split(";") if "=" in part)
    if not all(conn.get(x) for x in ("Host", "Database", "Username", "Password")):
        raise RuntimeError("Primary database connection configuration incomplete")
    e = os.environ.copy()
    e.update(PGPASSWORD=conn["Password"], PGOPTIONS="-c default_transaction_read_only=on")
    command = ["docker", "run", "--rm", "--pull", "never", "--network", "dhole",
               "-e", "PGPASSWORD", "-e", "PGOPTIONS", "postgres:17-alpine",
               "psql", "-X", "-t", "-A", "-v", "ON_ERROR_STOP=1",
               "-h", conn["Host"], "-p", conn.get("Port", "5432"),
               "-U", conn["Username"], "-d", conn["Database"], "-c",
               """SELECT COUNT(*) FROM agent."AgentExecutions" WHERE status='Running'"""]
    result = safe_run(command, env=e, timeout=70)
    if result.stdout.strip() != "0":
        raise RuntimeError("Refusing repair while any Agent execution is Running")
    print("PROD1_JWT_REPAIR_DRAIN_OK running=0")


def main():
    if os.environ.get("GITHUB_REF") != "refs/heads/master":
        raise RuntimeError("Production repair requires master")
    if len(sys.argv) != 2:
        raise RuntimeError("Usage: repair-prod1-jwt-runtime.py <DholeSystem checkout>")
    manifest = Path(sys.argv[1]).resolve() / "deploy/docker-compose.agent-prod1.yml"
    if not manifest.is_file():
        raise RuntimeError("Approved prod1 manifest not found")
    source = Path("/opt/dhole/.env")
    if not source.is_file():
        raise RuntimeError("Production env not readable")
    primary_id = find("dhole", "dhole-agent-api", running=True)
    primary = inspect(primary_id)
    if primary["State"]["Status"] != "running":
        raise RuntimeError("Primary API unavailable")
    if safe_run(["docker", "ps", "-q",
                 "--filter", "label=com.docker.compose.project=dhole",
                 "--filter", "label=com.docker.compose.service=dhole-agent-workers"]).stdout.strip():
        raise RuntimeError("Legacy primary Worker must remain inactive")
    all_running = safe_run(["docker", "ps", "--format",
                            '{{.Label "com.docker.compose.project"}}|{{.Label "com.docker.compose.service"}}'])
    for line in all_running.stdout.splitlines():
        if re.fullmatch(r"dhole-agent-prod\d+\|dhole-agent-workers(?:-prod\d+)?", line):
            if line != PROJECT + "|dhole-agent-workers-prod1":
                raise RuntimeError("Another production generation owns a running Worker")

    original = {}
    already_healthy = True
    for service in SERVICES:
        cid = find(PROJECT, service)
        info = inspect(cid)
        image = "dhole/agent-workers:latest" if "workers" in service else "dhole/agent-api:latest"
        expected = safe_run(["docker", "image", "inspect", "-f", "{{.Id}}", image]).stdout.strip()
        if info["Image"] != expected:
            raise RuntimeError("Refusing image change for " + service)
        original[service] = {"mounts": mounts(info), "image": info["Image"]}
        current = environment(info)
        already_healthy &= (info["State"]["Status"] == "running"
                            and all(current.get(key) == environment(primary).get(key)
                                    for key in JWT)
                            and current.get("MaerskCircuit__Enabled", "").lower() == "false")
        print("PROD1_JWT_REPAIR_PREFLIGHT", service, "state=" + info["State"]["Status"])

    if already_healthy:
        print("PROD1_JWT_REPAIR_ALREADY_HEALTHY: both services running, JWT verified,"
              " circuit disabled; no container changed")
        return

    assert_drain(primary)
    with tempfile.TemporaryDirectory(prefix="prod1-jwt-repair-",
                                     dir=os.environ.get("RUNNER_TEMP") or "/tmp") as work:
        os.chmod(work, 0o700)
        runtime = Path(work) / "runtime.env"
        shutil.copyfile(source, runtime)
        os.chmod(runtime, 0o600)
        safe_run(["python3", "scripts/prepare-prod1-jwt-env.py",
                  str(runtime), "--mirror-circuit"])
        env = os.environ.copy()
        env.update(DHOLE_ENV_FILE=str(runtime), DHOLE_NETWORK="dhole",
                   DHOLE_IMAGE_TAG="latest")
        command = ["docker", "compose", "--env-file", str(runtime), "-f", str(manifest),
                   "-p", PROJECT, "--profile", "activated"]
        resolved = safe_run(command + ["config", "--format", "json"], env=env)
        services = json.loads(resolved.stdout)["services"]
        trusted = environment(primary)
        for service in SERVICES:
            values = services[service]["environment"]
            if any(not trusted.get(key) or values.get(key) != trusted[key] for key in JWT):
                raise RuntimeError("Effective Compose JWT mismatch in " + service)
            if str(values.get("MaerskCircuit__Enabled", "")).lower() != "false":
                raise RuntimeError("Original testing circuit configuration not preserved")
        print("PROD1_JWT_REPAIR_CONFIG_OK secrets=redacted")
        safe_run(command + ["up", "-d", "--no-deps", "--no-build", "--pull", "never",
                            "--force-recreate", *SERVICES], env=env, timeout=160)
        new = {}
        for service in SERVICES:
            info = inspect(find(PROJECT, service))
            if info["Image"] != original[service]["image"] or mounts(info) != original[service]["mounts"]:
                raise RuntimeError("Image or persistent mount mismatch: " + service)
            new[service] = info["Id"]
        print("PROD1_JWT_REPAIR_VOLUMES_OK")
        healthy = False
        for _ in range(30):
            if all(inspect(cid)["State"]["Status"] == "running" for cid in new.values()):
                check = safe_run(["docker", "run", "--rm", "--pull", "never",
                                  "--network", "dhole", "curlimages/curl:8.12.1",
                                  "-fsS", "--max-time", "5",
                                  "http://dhole-agent-api-prod1:8080/health"],
                                 timeout=15, check=False)
                if check.returncode == 0:
                    healthy = True
                    break
            time.sleep(3)
        if not healthy:
            states = ",".join(service + "=" + inspect(new[service])["State"]["Status"]
                              for service in SERVICES)
            print("PROD1_JWT_REPAIR_HEALTH_TIMEOUT statuses=" + states,
                  file=sys.stderr)
            raise RuntimeError("prod1 API or Worker HTTP health did not become healthy")
        time.sleep(7)
        if not all(inspect(cid)["State"]["Status"] == "running" for cid in new.values()):
            raise RuntimeError("prod1 failed stable-running recheck")
        logs = safe_run(["docker", "logs", "--since", "3m",
                         new["dhole-agent-workers-prod1"]]).stdout
        if not re.search(r"AGENT_QUEUE_(PUMP|CONCURRENT)_STARTED", logs):
            raise RuntimeError("No Agent queue dispatcher startup log found")
        print("PROD1_JWT_REPAIR_SUCCESS prod1_api=healthy worker=running queue_pump=started")
        print("PROD1_JWT_REPAIR_NOTE provider_authentication_is_still_required")


if __name__ == "__main__":
    try:
        main()
    except Exception:
        print("PROD1_JWT_REPAIR_FAILED: health, safety, or configuration gate"
              " (values and credentials withheld)", file=sys.stderr)
        sys.exit(1)
