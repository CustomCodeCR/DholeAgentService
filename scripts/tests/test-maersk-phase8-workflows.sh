#!/usr/bin/env bash
# Offline release-policy regression test; no runner, database, or provider access.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
staging="$root/.github/workflows/deploy-staging.yml"
production="$root/.github/workflows/deploy-production.yml"
backup="$root/.github/workflows/maersk-phase8-backup.yml"

for workflow in "$staging" "$production"; do
  [[ -s "$workflow" ]] || { echo "Missing workflow: $workflow" >&2; exit 1; }
  grep -Fq 'workflow_dispatch:' "$workflow"
  grep -Fq 'confirmation:' "$workflow"
  grep -Fq 'backup_dir:' "$workflow"
  grep -Fq 'maersk-phase8-preflight.sh' "$workflow"
  grep -Fq 'maersk-phase8-restore-drill.sh' "$workflow"
  grep -Fq 'cancel-in-progress: false' "$workflow"
  grep -Fq 'AGENT_QUEUE_PUMP_STARTED' "$workflow"
  grep -Fq 'Verify Agent worker queue pump' "$workflow"
  grep -Fq 'maersk-phase8-schema-check.sh' "$workflow"
  start="$(grep -n -m1 'Phase 8 verify migrated schema before workers' "$workflow" | cut -d: -f1)"
  workers="$(grep -n -m1 'name: Deploy Agent workers' "$workflow" | cut -d: -f1)"
  [[ "$start" =~ ^[0-9]+$ && "$workers" =~ ^[0-9]+$ && "$start" -lt "$workers" ]] || {
    echo "Schema check must precede launching the worker: $workflow" >&2
    exit 1
  }
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
grep -Fq 'run.get("event") == "workflow_dispatch"' "$production"
grep -Fq 'run.get("head_sha") == os.environ["CURRENT_DEVELOP_SHA"]' "$production"
grep -Fq 'DEPLOY_AGENT_PRODUCTION' "$production"
grep -Fq 'DEPLOY_AGENT_STAGING' "$staging"
[[ -s "$backup" ]] || { echo "Missing guarded manual backup workflow" >&2; exit 1; }
for expected in 'workflow_dispatch:' 'BACKUP_AGENT_STAGING' 'BACKUP_AGENT_PRODUCTION' \
  'maersk-phase8-backup.sh' 'maersk-phase8-preflight.sh' \
  'maersk-phase8-restore-drill.sh' 'cancel-in-progress: false' \
  'agent-' 'DHOLE_ENV_FILE'; do
  grep -Fq "$expected" "$backup" || { echo "Backup workflow missing: $expected" >&2; exit 1; }
done
if grep -Eq '^[[:space:]]+push:|^[[:space:]]+schedule:' "$backup"; then
  echo "Backup workflow must never run automatically" >&2
  exit 1
fi
if grep -Eq 'upload-artifact|docker volume prune|docker compose.*stop' "$backup"; then
  echo "Backup workflow must not export data, prune volumes or stop workers" >&2
  exit 1
fi

echo "Phase 8 workflow safety checks passed."
