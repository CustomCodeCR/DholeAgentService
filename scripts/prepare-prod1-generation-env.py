#!/usr/bin/env python3
"""Inject a scoped GitHub Actions dispatch token into a *temporary* prod1 env.

Never copy it to /opt/dhole/.env, source control or stdout. The deploy's
Docker Compose env_file supplies the exact same settings to public API,
prod1 API and the currently active Worker.
"""
import os
from pathlib import Path
import re
import stat
import sys
import tempfile

KEYS = (
    "GenerationOrchestrator__Enabled",
    "GenerationOrchestrator__GitHubToken",
    "GenerationOrchestrator__ActionsRef",
)
TOKEN = re.compile(r"[A-Za-z0-9_.-]{20,1024}\Z")


def prepare(target: Path, token: str) -> None:
    if not TOKEN.fullmatch(token):
        raise ValueError("GitHub Actions dispatcher credential missing or invalid")
    if target.is_symlink() or not target.is_file():
        raise ValueError("Expected a regular protected temporary runtime env file")
    if stat.S_IMODE(target.stat().st_mode) & 0o077:
        raise ValueError("Protected runtime env must not be readable by group/other")
    lines = target.read_text(encoding="utf-8").splitlines()
    remaining = [
        line for line in lines
        if not any(line.startswith(key + "=") for key in KEYS)
    ]
    remaining.extend((
        "GenerationOrchestrator__Enabled=true",
        "GenerationOrchestrator__GitHubToken=" + token,
        "GenerationOrchestrator__ActionsRef=master",
    ))
    fd, temporary = tempfile.mkstemp(prefix=".prod1-orchestrator-", dir=str(target.parent))
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            stream.write("\n".join(remaining) + "\n")
        os.chmod(temporary, 0o600)
        os.replace(temporary, target)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


if __name__ == "__main__":
    try:
        if len(sys.argv) != 2:
            raise ValueError("Usage: prepare-prod1-generation-env.py <private-env>")
        prepare(Path(sys.argv[1]), os.environ.get("GENERATION_ORCHESTRATOR_GITHUB_TOKEN", ""))
    except (OSError, UnicodeError, ValueError):
        print("PROD1_GENERATION_DISPATCH_REFUSED: missing or invalid scoped credential/private env", file=sys.stderr)
        sys.exit(2)
    print("PROD1_GENERATION_DISPATCH_ENV_PREPARED")
