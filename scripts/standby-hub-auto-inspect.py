#!/usr/bin/env python3
"""Conservatively attest already signed-in Maersk Hubs for any prod2..prod200.

Runs on the protected production runner. Requires a fresh API-authorized
Ready request; never starts/stops Chromium, changes its profile, transitions
the active generation, closes a breaker, or submits credentials.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def load_module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


apply = load_module("generation_apply", "production-generation-apply-request.py")
auth = load_module("standby_authentication", "standby-authentication.py")
state = load_module("standby_auth_state", "standby-auth-state.py")

GENERATION = re.compile(r"prod(?:[2-9]|[1-9][0-9]{1,2})\Z")
CREDENTIAL = re.compile(r"[0-9a-f]{32}\Z")


def ready_candidates(db):
    # The API owns the authorization. A request cannot be rearmed here and
    # the operator may revoke it by expiring or completing the request.
    rows = apply.sql(db, """
        SELECT generation || '|' || replace(credential_id::text, '-', '')
        FROM agent.runtime_generation_auth_sessions
        WHERE status='Ready'
          AND requested_at_utc > now() - interval '90 minutes'
          AND account_verified_at_utc IS NULL
        ORDER BY requested_at_utc DESC
        LIMIT 10;
    """, stage="standby_hub_candidates")
    result = []
    for line in rows.splitlines():
        parts = line.split("|")
        if len(parts) != 2:
            continue
        generation, credential = parts
        if not GENERATION.fullmatch(generation) or int(generation[4:]) > 200:
            continue
        if not CREDENTIAL.fullmatch(credential):
            continue
        result.append((generation, credential))
    return result


def inspect_once(env_file, generation, credential):
    # Files live under a private temporary directory and are immediately
    # disposed. No browser-account identity, cookies or page HTML is stored.
    with tempfile.TemporaryDirectory(prefix="standby-hub-proof-") as name:
        os.chmod(name, 0o700)
        proof = Path(name) / "proof.json"
        auth.inspect_account(generation, credential, proof)
        state.record_identity(env_file, generation, credential, proof)


def run(env_file):
    db = apply.parse_db(env_file)
    items = ready_candidates(db)
    print("STANDBY_HUB_AUTO_CHECK_CANDIDATES=" + str(len(items)))
    for generation, credential in items:
        try:
            inspect_once(env_file, generation, credential)
            print("STANDBY_HUB_AUTO_VERIFIED generation=" + generation)
        except (RuntimeError, ValueError, OSError, json.JSONDecodeError,
                apply.GateError) as error:
            # Only non-sensitive, explicitly approved reason codes may leave
            # the runner. Never print provider text, identity or raw errors.
            allowed = {
                "standby_inspector_image_missing",
                "standby_hub_not_authenticated",
                "invalid_standby_hub_proof",
                "standby_viewer_not_running",
                "standby_viewer_no_longer_running",
                "standby_infrastructure_not_healthy",
                "standby_viewer_identity_mismatch",
                "stale_or_mismatched_standby_identity",
            }
            reason = str(error) if isinstance(error, RuntimeError) and str(error) in allowed else (
                str(error) if isinstance(error, ValueError) and str(error) in allowed else
                "inspection_or_database_unavailable"
            )
            print("STANDBY_HUB_AUTO_PENDING generation=" + generation +
                  " reason=" + reason)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--env-file", required=True)
    args = parser.parse_args()
    run(args.env_file)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, ValueError, OSError, apply.GateError):
        print("STANDBY_HUB_AUTO_CHECK_UNAVAILABLE")
        raise SystemExit(2)
