#!/usr/bin/env python3
"""Copy effective JWT settings from the healthy production API into a private
prepared env file. No secret is written to stdout or GitHub logs.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

JWT_KEYS = (
    "Auth__Jwt__Issuer",
    "Auth__Jwt__Audience",
    "Auth__Jwt__SecretKey",
    "Auth__Jwt__ExpirationMinutes",
)
OPTIONAL_KEYS = ("Auth__Jwt__RequireHttpsMetadata",)


def call(*args):
    return subprocess.run(args, check=True, text=True, capture_output=True,
                          timeout=30).stdout.strip()


def quote(value):
    if not value or any(ch in value for ch in "\r\n\x00"):
        raise ValueError("Missing or malformed effective JWT option")
    # Docker Compose dotenv recognizes single-quoted literal values.
    return "'" + value.replace("\\", "\\\\").replace("'", "\\'") + "'"


def main():
    if len(sys.argv) not in (2, 3):
        raise ValueError("Usage: prepare-prod1-jwt-env.py <prepared-env> [--mirror-circuit]")
    if len(sys.argv) == 3 and sys.argv[2] != "--mirror-circuit":
        raise ValueError("Unsupported option")
    target = Path(sys.argv[1]).resolve(strict=True)
    if not target.is_file() or not os.access(target, os.W_OK):
        raise ValueError("Prepared env must be an existing writable private file")

    ids = call("docker", "ps", "-q",
               "--filter", "label=com.docker.compose.project=dhole",
               "--filter", "label=com.docker.compose.service=dhole-agent-api").splitlines()
    if len(ids) != 1:
        raise ValueError("Expected exactly one running primary Agent API")
    info = json.loads(call("docker", "inspect", ids[0]))[0]
    if info.get("State", {}).get("Status") != "running":
        raise ValueError("Primary Agent API is not running")
    env = dict(item.split("=", 1) for item in info["Config"].get("Env", [])
               if "=" in item)
    values = {}
    for key in JWT_KEYS:
        values[key] = quote(env.get(key, ""))
    for key in OPTIONAL_KEYS:
        if env.get(key):
            values[key] = quote(env[key])
    if len(sys.argv) == 3:
        circuit = env.get("MaerskCircuit__Enabled", "").lower()
        if circuit not in ("true", "false"):
            raise ValueError("Primary API circuit setting is not explicit")
        values["MaerskCircuit__Enabled"] = circuit

    contents = target.read_text(encoding="utf-8").splitlines(keepends=True)
    contents = [line for line in contents
                if not any(line.startswith(key + "=") for key in values)]
    if contents and not contents[-1].endswith("\n"):
        contents[-1] += "\n"
    contents.extend(key + "=" + value + "\n" for key, value in values.items())
    descriptor, temporary = tempfile.mkstemp(prefix=".jwt-env-", dir=target.parent)
    try:
        os.fchmod(descriptor, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            output.writelines(contents)
        os.replace(temporary, target)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    print("PROD1_JWT_RUNTIME_PREPARED: required options copied from active primary API (values redacted)")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.SubprocessError, json.JSONDecodeError) as exc:
        # Don't emit arbitrary exception messages: they may contain secrets.
        print("PROD1_JWT_RUNTIME_REFUSED: unable to verify source or prepared env",
              file=sys.stderr)
        sys.exit(2)
