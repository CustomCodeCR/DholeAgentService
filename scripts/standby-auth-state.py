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


# Fixed, non-sensitive state diagnostics. Never print SQL, database errors,
# identity attributes, secrets, or raw Docker output to Actions logs.
SAFE_STATE_FAILURES = {
    "invalid_generation", "invalid_credential", "invalid_status",
    "invalid_standby_target", "invalid_restart_ready_options",
    "restarted_viewer_not_found", "restarted_viewer_not_healthy",
    "restarted_chromium_not_ready", "restarted_standby_state_changed",
    "standby_auth_schema_missing", "standby_auth_session_missing",
    "standby_auth_session_expired", "standby_auth_session_requested",
    "standby_auth_session_finishing", "standby_auth_session_verified",
    "standby_auth_transition_not_allowed",
    "standby_auth_state_changed", "unprotected_identity_file",
    "invalid_hub_proof", "stale_or_mismatched_standby_identity",
    "invalid_inspection_diagnostic",
    "identity_reset_refused", "operation_required",
}


def require_account_columns(db):
    # Schema evolution belongs to migrations, not an operator's read/write
    # authentication workflow (the production DB role may not own the table).
    present = apply.sql(db, """
        SELECT count(DISTINCT column_name)
        FROM information_schema.columns
        WHERE table_schema = 'agent'
          AND table_name = 'runtime_generation_auth_sessions'
          AND column_name IN (
            'account_name', 'account_username', 'account_customer_id',
            'account_verified_at_utc');
    """, stage="standby_auth_schema_preflight")
    if present.strip() != "4":
        raise ValueError("standby_auth_schema_missing")


def get_session_status(db, generation, credential):
    current = apply.sql(db, f"""
        SELECT COALESCE((
            SELECT CASE
                WHEN status IN ('Requested','Preparing','Ready','Failed')
                     AND requested_at_utc <= now() - interval '90 minutes'
                THEN 'Expired'
                ELSE status::text
            END
            FROM agent.runtime_generation_auth_sessions
            WHERE generation='{generation}' AND credential_id='{credential}'::uuid
            ORDER BY requested_at_utc DESC
            LIMIT 1
        ), 'Missing');
    """, stage="standby_auth_session_preflight").strip()
    if current == "Missing":
        raise ValueError("standby_auth_session_missing")
    return current


def refuse_disallowed_status(current):
    # Only fixed known states leave the runner, never an arbitrary DB value.
    codes = {
        "Expired": "standby_auth_session_expired",
        "Requested": "standby_auth_session_requested",
        "Finishing": "standby_auth_session_finishing",
        "Verified": "standby_auth_session_verified",
    }
    raise ValueError(codes.get(current, "standby_auth_transition_not_allowed"))


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
        # Differentiate a missing API-authorized request from a disallowed
        # transition (e.g. Failed -> Preparing, or Verified -> Ready).
        current = get_session_status(apply.parse_db(env_file), generation, credential)
        refuse_disallowed_status(current)


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
    # Failed may be retried only after a healthy, explicitly authorized
    # viewer reconciliation. A previous Verified proof cannot be overridden.
    db = apply.parse_db(env_file)
    require_account_columns(db)
    current = get_session_status(db, generation, credential)
    if current not in ("Requested", "Preparing", "Ready", "Failed"):
        refuse_disallowed_status(current)
    # Invalidate any previous identity proof; a new inspect must re-attest.
    query = f"""
      UPDATE agent.runtime_generation_auth_sessions
      SET status='Ready', diagnostic_code=NULL,
          account_name=NULL, account_username=NULL, account_customer_id=NULL,
          account_verified_at_utc=NULL,
          updated_at_utc=now()
      WHERE generation='{generation}'
        AND credential_id='{credential}'::uuid
        AND status IN ('Requested','Preparing','Ready','Failed')
        AND requested_at_utc > now() - interval '90 minutes'
      RETURNING generation;
    """
    result = apply.sql(db, query, stage="standby_auth_restart_ready")
    if result.splitlines()[-1:] != [generation]:
        raise ValueError("restarted_standby_state_changed")
    print("STANDBY_AUTH_STATE_RECORDED status=Ready")


def record_identity(env_file, generation, credential, identity_file):
    """Persist authenticated Hub evidence; no personal account fields needed."""
    if not re.fullmatch(r"prod(?:[2-9]|[1-9][0-9]{1,2})", generation) or (
            int(generation[4:]) > 200) or not re.fullmatch(r"[0-9a-f]{32}", credential):
        raise ValueError("invalid_standby_target")
    if identity_file.is_symlink() or not identity_file.is_file() or (
            identity_file.stat().st_mode & 0o077):
        raise ValueError("unprotected_identity_file")
    proof = json.loads(identity_file.read_text(encoding="utf-8"))
    # Never treat a URL-only result, identity strings or arbitrary JSON as a
    # trusted provider session. Inspector must attest live private Hub widgets.
    if not isinstance(proof, dict) or proof != {"HubAuthenticated": True}:
        raise ValueError("invalid_hub_proof")
    db = apply.parse_db(env_file)
    require_account_columns(db)
    current = get_session_status(db, generation, credential)
    if current not in ("Ready", "Finishing"):
        refuse_disallowed_status(current)
    query = f"""
        UPDATE agent.runtime_generation_auth_sessions SET
            account_name=NULL,
            account_username=NULL,
            account_customer_id=NULL,
            account_verified_at_utc=now(),
            diagnostic_code=NULL, updated_at_utc=now()
        WHERE generation='{generation}'
          AND credential_id='{credential}'::uuid
          AND status IN ('Ready','Finishing')
          AND requested_at_utc > now() - interval '90 minutes'
        RETURNING generation;
    """
    result = apply.sql(db, query, stage="standby_hub_proof")
    if result.splitlines()[-1:] != [generation]:
        raise ValueError("stale_or_mismatched_standby_identity")
    print("STANDBY_MAERSK_HUB_PROOF_SAVED")


def record_inspection_failure(env_file, generation, credential, code):
    # The operator's manual inspection may fail while the viewer remains
    # usable. Preserve Ready (so it can be retried) and record no identity.
    # No arbitrary text from a provider or subprocess can enter this column.
    if code != "standby_hub_not_authenticated":
        raise ValueError("invalid_inspection_diagnostic")
    if not re.fullmatch(r"prod(?:[2-9]|[1-9][0-9]{1,2})", generation) or (
            int(generation[4:]) > 200) or not re.fullmatch(r"[0-9a-f]{32}", credential):
        raise ValueError("invalid_standby_target")
    db = apply.parse_db(env_file)
    result = apply.sql(db, f"""
        UPDATE agent.runtime_generation_auth_sessions
        SET diagnostic_code='{code}', updated_at_utc=now()
        WHERE generation='{generation}'
          AND credential_id='{credential}'::uuid
          AND status='Ready'
          AND requested_at_utc > now() - interval '90 minutes'
          AND account_verified_at_utc IS NULL
        RETURNING generation;
    """, stage="standby_account_inspection_failure")
    if result.strip() != generation:
        # Do not overwrite a Verified, Expired, Failed or replaced request.
        refuse_disallowed_status(get_session_status(db, generation, credential))
    print("STANDBY_MAERSK_HUB_INSPECTION_RETRY_AVAILABLE")


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
    parser.add_argument("--inspection-diagnostic")
    parser.add_argument("--reset-identity",action="store_true")
    parser.add_argument("--restart-ready",action="store_true")
    args=parser.parse_args()
    if args.restart_ready:
        if args.status != "Ready" or args.identity_file or args.reset_identity:
            raise ValueError("invalid_restart_ready_options")
        restart_ready(args.env_file, args.generation, args.credential)
    elif args.inspection_diagnostic:
        if args.identity_file or args.reset_identity or args.status:
            raise ValueError("invalid_inspection_diagnostic")
        record_inspection_failure(
            args.env_file, args.generation, args.credential,
            args.inspection_diagnostic)
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
    except (ValueError, OSError, apply.GateError) as error:
        # Report only allowlisted reason codes; never expose SQL, private
        # account fields, database credentials or raw subprocess stderr.
        reason = str(error) if isinstance(error, ValueError) and str(error) in SAFE_STATE_FAILURES else (
            "postgres_operation_failed" if isinstance(error, apply.GateError) else
            "unexpected_state_update_failure"
        )
        print("STANDBY_AUTH_STATE_UPDATE_REFUSED reason=" + reason, file=sys.stderr)
        sys.exit(2)
