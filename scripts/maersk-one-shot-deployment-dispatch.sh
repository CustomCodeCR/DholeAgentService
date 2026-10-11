#!/usr/bin/env bash
# Dispatch an existing PROTECTED workflow and verify its real conclusion.
# Requires GH_TOKEN with actions:write. Does not edit deployment workflows.
set -Eeuo pipefail
set +x
target="${1:-}"
backup_dir="${2:-}"
stage_run="${3:-}"
die() { echo "MAERSK_GUARDED_DEPLOY_FAILED: $*" >&2; exit 1; }
[[ -n "${GH_TOKEN:-}" ]] || die 'GitHub Actions write token is unavailable'
command -v gh >/dev/null || die 'GitHub CLI is required'
command -v python3 >/dev/null || die 'Python 3 is required'
[[ "$backup_dir" == /* && -n "$backup_dir" ]] || die 'Absolute verified backup path is required'
case "$target" in
  staging) workflow=deploy-staging.yml; branch=develop; confirmation=DEPLOY_AGENT_STAGING ;;
  production) workflow=deploy-production.yml; branch=master; confirmation=DEPLOY_AGENT_PRODUCTION
              [[ "$stage_run" =~ ^[0-9]+$ ]] || die 'Successful staging run ID required' ;;
  *) die 'Usage: <staging|production> <backup-dir> [successful-staging-run-id]' ;;
esac
repo=CustomCodeCR/DholeAgentService
expected="$(gh api "repos/$repo/branches/$branch" --jq .commit.sha)" ||
  die 'Cannot resolve target commit'
[[ "$expected" =~ ^[0-9a-f]{40}$ ]] || die 'Invalid target SHA'
start="$(date -u +%s)"
if [[ "$target" == staging ]]; then
  gh api --method POST "repos/$repo/actions/workflows/$workflow/dispatches" \
    -f ref="$branch" \
    -f 'inputs[confirmation]'="$confirmation" \
    -f 'inputs[backup_dir]'="$backup_dir" >/dev/null ||
      die 'GitHub refused staging workflow_dispatch'
else
  gh api --method POST "repos/$repo/actions/workflows/$workflow/dispatches" \
    -f ref="$branch" \
    -f 'inputs[confirmation]'="$confirmation" \
    -f 'inputs[backup_dir]'="$backup_dir" \
    -f 'inputs[staging_run_id]'="$stage_run" >/dev/null ||
      die 'GitHub refused production workflow_dispatch'
fi
echo "MAERSK_GUARDED_DEPLOY_DISPATCHED target=$target sha=$expected"
export MAERSK_EXPECTED_SHA="$expected" MAERSK_STARTED="$start"
run_id=""
for attempt in $(seq 1 45); do
  payload="$(gh api "repos/$repo/actions/workflows/$workflow/runs?event=workflow_dispatch&per_page=25")" ||
    die 'Cannot list dispatched workflow runs'
  run_id="$(printf '%s' "$payload" | python3 -c '
import datetime,json,os,sys
payload=json.load(sys.stdin)
expected=os.environ["MAERSK_EXPECTED_SHA"]
since=int(os.environ["MAERSK_STARTED"])-15
valid=[]
for r in payload.get("workflow_runs", []):
    if r.get("event")!="workflow_dispatch" or r.get("head_sha")!=expected:
        continue
    created=r.get("created_at") or ""
    if not created:
        continue
    ts=datetime.datetime.fromisoformat(created.replace("Z","+00:00")).timestamp()
    if ts >= since:
        valid.append(int(r["id"]))
print(max(valid) if valid else "")
')" || die 'Cannot identify workflow run'
  [[ -n "$run_id" ]] && break
  sleep 5
done
[[ "$run_id" =~ ^[0-9]+$ ]] || die 'No matching workflow run appeared'
echo "MAERSK_GUARDED_DEPLOY_RUN=$run_id"
echo "MAERSK_GUARDED_DEPLOY_URL=https://github.com/$repo/actions/runs/$run_id"
for attempt in $(seq 1 180); do
  info="$(gh api "repos/$repo/actions/runs/$run_id" \
    --jq '[.status,(.conclusion // "pending"),.head_sha] | join(" ")')" ||
      die "Cannot verify workflow $run_id"
  read -r status conclusion actual_sha <<< "$info"
  [[ "$actual_sha" == "$expected" ]] ||
    die 'The observed run no longer matches the expected commit'
  if [[ "$status" == completed ]]; then
    [[ "$conclusion" == success ]] ||
      die "Protected deploy $run_id concluded $conclusion"
    [[ -z "${GITHUB_OUTPUT:-}" ]] ||
      printf 'run_id=%s\n' "$run_id" >> "$GITHUB_OUTPUT"
    echo "MAERSK_GUARDED_DEPLOY_VERIFIED_SUCCESS target=$target run_id=$run_id"
    exit 0
  fi
  sleep 20
done
die "Deployment $run_id has not succeeded within the bounded verification window"
