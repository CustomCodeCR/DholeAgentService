#!/usr/bin/env python3
"""Report a trusted standby-auth workflow result to the shared PostgreSQL DB."""
import argparse
import importlib.util
from pathlib import Path
import re
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
    expected = {"Preparing": "Requested", "Ready": "Preparing",
                "Verified": "Finishing", "Failed": None}[status]
    where = ("AND status='" + expected + "'" if expected else
             "AND status IN ('Requested','Preparing','Ready','Finishing')")
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

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--env-file",required=True)
    parser.add_argument("--generation",required=True)
    parser.add_argument("--credential",required=True)
    parser.add_argument("--status",required=True)
    args=parser.parse_args()
    update(args.env_file,args.generation,args.credential,args.status)
    print("STANDBY_AUTH_STATE_RECORDED status="+args.status)

if __name__=="__main__":
    try:
        main()
    except (ValueError,OSError,apply.GateError):
        print("STANDBY_AUTH_STATE_UPDATE_REFUSED",file=sys.stderr)
        sys.exit(2)
