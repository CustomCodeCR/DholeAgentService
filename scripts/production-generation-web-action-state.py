#!/usr/bin/env python3
"""Update durable web-origin generation action status from protected Actions runner.

A running claim validates the exact UUID, operation, target and recent creation
before any host operation. This script never performs the host operation itself.
"""
import argparse
import importlib.util
from pathlib import Path
import re
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]
OPERATIONS = {"inspect", "reconcile", "provision-check", "provision",
              "attest", "preflight", "switch", "technical-recovery"}
GENERATION = re.compile(r"^prod(?:[1-9][0-9]*)?$")


class InvalidWebAction(ValueError):
    pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--id", required=True)
    parser.add_argument("--operation", choices=sorted(OPERATIONS), required=True)
    parser.add_argument("--generation", required=True)
    parser.add_argument("--status", choices=("Running", "Completed", "Failed"), required=True)
    parser.add_argument("--env-file", required=True)
    args = parser.parse_args()
    if str(uuid.UUID(args.id)) != args.id:
        raise InvalidWebAction("noncanonical_request")
    if not GENERATION.fullmatch(args.generation) or len(args.generation) > 64:
        raise InvalidWebAction("invalid_target")
    if args.status == "Running":
        sql = f"""
            UPDATE agent.runtime_generation_web_actions
            SET status='Running', diagnostic_code='github_workflow_running',
                updated_at_utc=now()
            WHERE id='{args.id}'::uuid
              AND generation='{args.generation}' AND operation='{args.operation}'
              AND status IN ('Pending','Dispatched')
              AND created_at_utc > now()-interval '15 minutes'
            RETURNING id::text;
        """
    else:
        error_code = ("github_workflow_confirmed" if args.status == "Completed"
                      else "github_workflow_failed")
        sql = f"""
            UPDATE agent.runtime_generation_web_actions
            SET status='{args.status}',diagnostic_code='{error_code}',
                updated_at_utc=now(),completed_at_utc=now()
            WHERE id='{args.id}'::uuid
              AND generation='{args.generation}' AND operation='{args.operation}'
              AND status='Running'
            RETURNING id::text;
        """
    spec = importlib.util.spec_from_file_location(
        "generation_db", ROOT / "scripts/production-generation-apply-request.py")
    db = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(db)
    config = db.parse_db(args.env_file)
    result = db.sql(config, sql, stage="web_action_" + args.status.lower())
    if result.strip() != args.id:
        raise InvalidWebAction("request_already_processed_or_not_authorized")
    print(f"GENERATION_WEB_ACTION_{args.status.upper()} id={args.id} "
          f"operation={args.operation} generation={args.generation}")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, TypeError) as exc:
        print("GENERATION_WEB_ACTION_REFUSED: " +
              (str(exc) if isinstance(exc, InvalidWebAction) else type(exc).__name__),
              file=sys.stderr)
        sys.exit(1)
