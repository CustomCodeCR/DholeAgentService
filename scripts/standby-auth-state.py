#!/usr/bin/env python3
"""Report a trusted standby-auth workflow result to the shared PostgreSQL DB."""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "generation_apply", ROOT / "scripts/production-generation-apply-request.py")
apply = importlib.util.module_from_spec(spec)
spec.loader.exec_module(apply)

def update(env_file, generation, credential, status):
    if not re.fullmatch(r"prod(?:[2-9]|[1-9][0-9]{1,2})", generation) or (
        int(generation[4:]) > 200
    ):
        raise ValueError("invalid_generation")
    if not re.fullmatch(r"[0-9a-f]{32}", credential):
        raise ValueError("invalid_credential")
    if status not in ("Preparing", "Ready", "Verified", "Failed"):
        raise ValueError("invalid_status")
    # Restart is an authorized viewer refresh, not a new authentication
    # request. A session already Ready remains Ready after a healthy restart.
    # Do not reopen a Failed or Verified session.
    allowed = {
        "Preparing": ("Requested",),
        "Ready": ("Preparing", "Ready"),
        "Verified": ("Finishing",),
        "Failed": ("Requested", "Preparing", "Ready", "Finishing"),
    }[status]
    where = "AND status IN (" + ",".join("'" + state + "'" for state in allowed) + ")"
    diagnostic = ("NULL" if status != "Failed" else "'standby_auth_workflow_failed'")
    query = f"""
      UPDATE agent.runtime_generation_auth_sessions
      SET status='{status}', diagnostic_code={diagnostic},
          updated_at_utc=now()
      WHERE generation='{generation}'
        AND credential_id='{credential}'::uuid
        {where}
      RETURNING generation;
    """
    result = apply.sql(apply.parse_db(env_file), query, stage="standby_auth_status")
    if status != "Failed" and result.strip() != generation:
        raise ValueError("standby_auth_state_changed")


def restart_ready(env_file, generation, credential):
    """Re-arm only a healthy, explicitly restarted standby viewer for a new proof."""
    if not re.fullmatch(r"prod(?:[2-9]|[1-9][0-9]{1,2})", generation) or (
            int(generation[4:]) > 200) or not re.fullmatch(r"[0-9a-f]{32}", credential):
        raise ValueError("invalid_standby_target")
    viewer_name = "dhole-standby-auth-" + generation
    inspection = subprocess.run(
        ["docker", "inspect", viewer_name], capture_output=True, text=True,
        timeout=15, check=False)
    if inspection.returncode or not inspection.stdout:
        raise ValueError("restarted_viewer_not_found")
    viewer = json.loads(inspection.stdout)[0]
    labels = viewer.get("Config", {}).get("Labels") or {}
    if (labels.get("dhole.standby.auth.generation") != generation or
            labels.get("dhole.standby.auth.credential") != credential or
            viewer.get("State", {}).get("Status") != "running" or
            not re.fullmatch(r"dhole/agent-workers:standby-inspector-[0-9a-f]{12}",
                             viewer.get("Config", {}).get("Image") or "")):
        raise ValueError("restarted_viewer_not_healthy")
    probe = subprocess.run(
        ["docker", "exec", viewer_name, "curl", "-fsS", "--max-time", "3",
         "--output", "/dev/null", "http://127.0.0.1:9222/json/version"],
        capture_output=True, timeout=8, check=False)
    if probe.returncode:
        raise ValueError("restarted_chromium_not_ready")
    # Failed may be retried only after this explicit, healthy restart.
    # Invalidate any previous identity proof; the next inspect must re-attest.
    query = f"""
      ALTER TABLE agent.runtime_generation_auth_sessions
        ADD COLUMN IF NOT EXISTS account_name text,
        ADD COLUMN IF NOT EXISTS account_username text,
        ADD COLUMN IF NOT EXISTS account_customer_id text,
        ADD COLUMN IF NOT EXISTS account_verified_at_utc timestamptz;
      UPDATE agent.runtime_generation_auth_sessions
      SET status='Ready', diagnostic_code=NULL,
          account_name=NULL, account_username=NULL, account_customer_id=NULL,
          account_verified_at_utc=NULL,
          requested_at_utc=now(), updated_at_utc=now()
      WHERE generation='{generation}'
        AND credential_id='{credential}'::uuid
        AND status IN ('Preparing','Ready','Failed')
      RETURNING generation;
    """
    result = apply.sql(apply.parse_db(env_file), query, stage="standby_auth_restart_ready")
    if result.splitlines()[-1:] != [generation]:
        raise ValueError("restarted_standby_state_changed")
    print("STANDBY_AUTH_STATE_RECORDED status=Ready")


def record_identity(env_file, generation, credential, identity_file):
    if not re.fullmatch(r"prod(?:[2-9]|[1-9][0-9]{1,2})", generation) or (
        int(generation[4:]) > 200
    ) or not re.fullmatch(r"[0-9a-f]{32}", credential):
        raise ValueError("invalid_standby_target")
    if identity_file.is_symlink() or not identity_file.is_file() or (
            identity_file.stat().st_mode & 0o077):
        raise ValueError("unprotected_identity_file")
    record = json.loads(identity_file.read_text(encoding="utf-8"))
    if not isinstance(record, dict) or set(record) != {"DisplayName","Username","CustomerId"}:
        raise ValueError("invalid_identity")
    name, username, customer = (
        record["DisplayName"], record["Username"], record["CustomerId"])
    if not isinstance(name, str) or not 4 <= len(name) <= 100 or (
        not all(c.isalpha() or c in " -.'" for c in name)):
        raise ValueError("invalid_name")
    if not isinstance(username, str) or not re.fullmatch(r"(?:[A-Za-z0-9_.-]{3,64}|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})", username):
        raise ValueError("invalid_username")
    if not isinstance(customer, str) or not re.fullmatch(r"[0-9]{8,15}", customer):
        raise ValueError("invalid_customer_id")
    def quoted(value):
        return "'" + value.replace("'", "''") + "'"
    query = f"""
        ALTER TABLE agent.runtime_generation_auth_sessions
            ADD COLUMN IF NOT EXISTS account_name text,
            ADD COLUMN IF NOT EXISTS account_username text,
            ADD COLUMN IF NOT EXISTS account_customer_id text,
            ADD COLUMN IF NOT EXISTS account_verified_at_utc timestamptz;
        UPDATE agent.runtime_generation_auth_sessions SET
            account_name={quoted(name)},
            account_username={quoted(username)},
            account_customer_id={quoted(customer)},
            account_verified_at_utc=now(),
            diagnostic_code=NULL, updated_at_utc=now()
        WHERE generation='{generation}'
          AND credential_id='{credential}'::uuid
          AND status IN ('Ready','Finishing')
          AND requested_at_utc > now() - interval '30 minutes'
        RETURNING generation;
    """
    result = apply.sql(apply.parse_db(env_file), query, stage="standby_account_identity")
    if result.splitlines()[-1:] != [generation]:
        raise ValueError("stale_or_mismatched_standby_identity")
    print("STANDBY_MAERSK_ACCOUNT_IDENTITY_SAVED")


def reset_identity(env_file, generation, credential):
    if not re.fullmatch(r"prod(?:[2-9]|[1-9][0-9]{1,2})", generation) or (
        int(generation[4:]) > 200
    ) or not re.fullmatch(r"[0-9a-f]{32}", credential):
        raise ValueError("invalid_standby_target")
    result = apply.sql(apply.parse_db(env_file), f"""
        UPDATE agent.runtime_generation_auth_sessions
        SET account_name=NULL,account_username=NULL,
            account_customer_id=NULL,account_verified_at_utc=NULL,
            updated_at_utc=now()
        WHERE generation='{generation}' AND credential_id='{credential}'::uuid
          AND status='Preparing'
        RETURNING generation;
    """, stage="standby_account_reset")
    if result != generation:
        raise ValueError("identity_reset_refused")
    print("STANDBY_MAERSK_ACCOUNT_IDENTITY_RESET")


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--env-file",required=True)
    parser.add_argument("--generation",required=True)
    parser.add_argument("--credential",required=True)
    parser.add_argument("--status",required=False)
    parser.add_argument("--identity-file")
    parser.add_argument("--reset-identity",action="store_true")
    parser.add_argument("--restart-ready",action="store_true")
    args=parser.parse_args()
    if args.restart_ready:
        if args.status != "Ready" or args.identity_file or args.reset_identity:
            raise ValueError("invalid_restart_ready_options")
        restart_ready(args.env_file, args.generation, args.credential)
    elif args.identity_file:
        record_identity(args.env_file,args.generation,args.credential,Path(args.identity_file))
    elif args.reset_identity:
        reset_identity(args.env_file,args.generation,args.credential)
    elif args.status:
        update(args.env_file,args.generation,args.credential,args.status)
        print("STANDBY_AUTH_STATE_RECORDED status="+args.status)
    else:
        raise ValueError("operation_required")

if __name__=="__main__":
    try:
        main()
    except (ValueError,OSError,apply.GateError):
        print("STANDBY_AUTH_STATE_UPDATE_REFUSED",file=sys.stderr)
        sys.exit(2)
