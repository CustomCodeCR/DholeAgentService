#!/usr/bin/env python3
"""Supply a stateless Agent API with its existing, protected service auth settings.

Only registered application JWT/DB configuration is mirrored, never browser
cookies, Chromium profiles, sessions, provider credentials, or volume files.
No values are printed. The destination is the ephemeral runner env file.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

REQUIRED = (
    "Auth__Jwt__SecretKey",
    "Auth__Jwt__Issuer",
    "Auth__Jwt__Audience",
    "Auth__Jwt__ExpirationMinutes",
    "Auth__Jwt__RequireHttpsMetadata",
    "ConnectionStrings__Postgres",
)


class ConfigError(Exception):
    pass


def get_primary_env():
    result = subprocess.check_output([
        "docker", "ps", "-aq",
        "--filter", "label=com.docker.compose.project=dhole",
        "--filter", "label=com.docker.compose.service=dhole-agent-api",
    ], text=True, timeout=10).splitlines()
    if len(result) != 1 or not result[0]:
        raise ConfigError("Exactly one primary Agent API must be present")
    raw = subprocess.check_output([
        "docker", "inspect", "--format", "{{json .Config.Env}}", result[0],
    ], text=True, timeout=10)
    values = json.loads(raw)
    if not isinstance(values, list):
        raise ConfigError("Invalid primary Agent API environment")
    return dict(value.split("=", 1) for value in values
                if isinstance(value, str) and "=" in value)


def quote_env_value(value):
    if not isinstance(value, str) or not value or any(ch in value for ch in "\r\n\x00"):
        raise ConfigError("Required service configuration is empty or invalid")
    return "'" + value.replace("'", "\\'") + "'"


def prepare(path, values, check_only):
    expected = {key: values.get(key) for key in REQUIRED}
    missing = [key for key in REQUIRED if not expected[key]]
    if missing:
        raise ConfigError("Primary API is missing required setting names: " + ",".join(missing))
    if not path.is_file() or path.is_symlink():
        raise ConfigError("Protected prepared environment is unavailable")
    text = path.read_text(encoding="utf-8")
    if check_only:
        print("GENERATION_AUTH_CONFIG_SOURCE_VERIFIED")
        return
    # Replace only these six keys in a freshly prepared, runner-private env.
    retained = [line for line in text.splitlines()
                if not any(line.startswith(key + "=") for key in REQUIRED)]
    retained.extend(key + "=" + quote_env_value(expected[key]) for key in REQUIRED)
    fd, temporary = tempfile.mkstemp(prefix=".prodN-auth-", dir=path.parent)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as target:
            target.write("\n".join(retained) + "\n")
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    print("GENERATION_AUTH_CONFIG_PREPARED: required primary JWT and Postgres settings mirrored")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--prepared-env", required=True)
    parser.add_argument("--check-only", action="store_true")
    args = parser.parse_args()
    prepare(Path(args.prepared_env), get_primary_env(), args.check_only)


if __name__ == "__main__":
    try:
        main()
    except (ConfigError, OSError, subprocess.SubprocessError, ValueError,
            TypeError, json.JSONDecodeError) as error:
        print("GENERATION_AUTH_CONFIG_FAILED: " +
              (str(error) if isinstance(error, ConfigError)
               else type(error).__name__), file=sys.stderr)
        sys.exit(1)
