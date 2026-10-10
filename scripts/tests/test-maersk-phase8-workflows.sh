#!/usr/bin/env bash
# Offline release-policy regression test; no runner, database, or provider access.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
staging="$root/.github/workflows/deploy-staging.yml"
production="$root/.github/workflows/deploy-production.yml"

for workflow in "$staging" "$production"; do
  [[ -s "$workflow" ]] || { echo "Missing workflow: $workflow" >&2; exit 1; }
  grep -Fq 'workflow_dispatch:' "$workflow"
  grep -Fq 'confirmation:' "$workflow"
  grep -Fq 'backup_dir:' "$workflow"
  grep -Fq 'maersk-phase8-preflight.sh' "$workflow"
  grep -Fq 'cancel-in-progress: false' "$workflow"
  grep -Fq 'AGENT_QUEUE_PUMP_STARTED' "$workflow"
  grep -Fq 'Verify Agent worker queue pump' "$workflow"
  if grep -Eq '^[[:space:]]+push:|^[[:space:]]+schedule:' "$workflow"; then
    echo "Phase 8 release must not deploy on a push or cron event: $workflow" >&2
    exit 1
  fi
  if grep -Fq 'Release blocked Maersk queue once before builds' "$workflow"; then
    echo "Destructive queue release is forbidden: $workflow" >&2
    exit 1
  fi
done

grep -Fq 'staging_run_id:' "$production"
grep -Fq 'Require successful recent staging acceptance' "$production"
grep -Fq 'DEPLOY_AGENT_PRODUCTION' "$production"
grep -Fq 'DEPLOY_AGENT_STAGING' "$staging"
echo "Phase 8 workflow safety checks passed."
