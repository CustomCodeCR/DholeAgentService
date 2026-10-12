#!/usr/bin/env python3
"""Record provisioning result only; never touch provider sessions or Workers."""
import argparse
import os
import re
import subprocess
import sys
from pathlib import Path


def connection(path):
    raw = next((row.split("=", 1)[1] for row in Path(path).read_text().splitlines()
                if row.startswith("AGENT_POSTGRES_CONNECTION_STRING=")), "")
    settings = dict(part.split("=", 1) for part in raw.split(";") if "=" in part)
    if any(not settings.get(k) for k in ("Username", "Password", "Database")):
        raise ValueError("Database configuration unavailable")
    return settings


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--generation", required=True)
    parser.add_argument("--env-file", required=True)
    parser.add_argument("--status", choices=["Provisioned", "Failed"], required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"prod[1-9][0-9]*", args.generation) or len(args.generation) > 64:
        raise ValueError("Invalid generation")
    settings = connection(args.env_file)
    if not re.fullmatch(r"[A-Za-z0-9_]+", settings["Database"]):
        raise ValueError("Invalid DB")
    code = "infrastructure_provisioned" if args.status == "Provisioned" else "infrastructure_provision_failed"
    query = f"""
       UPDATE agent.runtime_generation_provisions
       SET status='{args.status}', diagnostic_code='{code}',
           updated_at_utc=now()
       WHERE generation='{args.generation}'
         AND status IN ('Dispatched','Dispatching','Failed');
    """
    environment = os.environ.copy()
    environment["PGPASSWORD"] = settings["Password"]
    result = subprocess.run([
        "docker", "run", "--rm", "--network", "dhole", "-e", "PGPASSWORD",
        "postgres:16-alpine", "psql", "-X", "-q", "-v", "ON_ERROR_STOP=1",
        "-h", settings.get("Host", "postgres"),
        "-p", settings.get("Port", "5432"),
        "-U", settings["Username"], "-d", settings["Database"],
        "-c", query], capture_output=True, text=True, timeout=30, env=environment)
    if result.returncode:
        raise ValueError("Provisioning state update failed")
    print("GENERATION_PROVISION_STATE_RECORDED generation=" + args.generation + " status=" + args.status)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        print("GENERATION_PROVISION_STATE_ERROR: " + type(error).__name__, file=sys.stderr)
        sys.exit(1)
