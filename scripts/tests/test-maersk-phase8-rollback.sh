#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fixture="$(mktemp -d "$HOME/maersk-phase8-rollback-test.XXXXXX")"
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/bin"
touch "$fixture/compose.yml" "$fixture/staging.yml" "$fixture/.env"
cat > "$fixture/bin/docker" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$DOCKER_MOCK_LOG"
if [[ "$1" == compose && "$*" == *" ps -q dhole-agent-api" ]]; then
  echo "previous-api-container"
elif [[ "$1" == compose && "$*" == *" ps -q dhole-agent-workers" ]]; then
  echo "previous-worker-container"
elif [[ "$1" == inspect && "${*: -1}" == "previous-api-container" ]]; then
  echo sha256:existing-api
elif [[ "$1" == inspect && "${*: -1}" == "previous-worker-container" ]]; then
  echo sha256:existing-worker
elif [[ "$1" == container && "$2" == inspect ]]; then
  [[ "${MOCK_RESTORE_EXISTS:-true}" == true ]] || exit 1
elif [[ "$1" == exec && "$*" == *" psql -At "* ]]; then
  echo 3
fi
MOCK
chmod +x "$fixture/bin/docker"

export DOCKER_MOCK_LOG="$fixture/docker.log"
export PATH="$fixture/bin:$PATH"
export RUNNER_TEMP="$fixture"
export GITHUB_RUN_ID="9876543"
export DHOLE_ENV_FILE="$fixture/.env"
export COMPOSE_FILE="$fixture/compose.yml"
export COMPOSE_OVERRIDE="$fixture/staging.yml"

rollback="$repo/scripts/maersk-phase8-image-rollback.sh"
bash -n "$rollback"

bash "$rollback" capture staging
manifest="$fixture/maersk-phase8-9876543-staging.manifest"
[[ -s "$manifest" ]] || { echo "Capture manifest missing" >&2; exit 1; }
grep -Fq "api_tag=dhole/agent-api:phase8-prev-staging-9876543" "$manifest"
grep -Fq "worker_tag=dhole/agent-workers:phase8-prev-staging-9876543" "$manifest"
bash "$rollback" restore staging
# A failed build must restore original tags without recreating either service.
grep -Fq 'image tag dhole/agent-api:phase8-prev-staging-9876543 dhole/agent-api:staging' "$DOCKER_MOCK_LOG"
grep -Fq 'image tag dhole/agent-workers:phase8-prev-staging-9876543 dhole/agent-workers:staging' "$DOCKER_MOCK_LOG"
if grep -Fq ' up -d ' "$DOCKER_MOCK_LOG"; then
  echo "Rollback must not touch services before deployment starts" >&2
  exit 1
fi
bash "$rollback" mark-deploy staging
bash "$rollback" restore staging
grep -Fq 'image tag dhole/agent-api:phase8-prev-staging-9876543 dhole/agent-api:staging' "$DOCKER_MOCK_LOG"
grep -Fq 'image tag dhole/agent-workers:phase8-prev-staging-9876543 dhole/agent-workers:staging' "$DOCKER_MOCK_LOG"
grep -Fq 'up -d --no-deps --force-recreate --pull never dhole-agent-api dhole-agent-workers' "$DOCKER_MOCK_LOG"

# Verify previous tags remain protected and both deployments can revert on failure.
for workflow in "$repo/.github/workflows/deploy-staging.yml" "$repo/.github/workflows/deploy-production.yml"; do
  grep -Fq 'Phase 8 isolated PostgreSQL restoration drill' "$workflow"
  grep -Fq 'maersk-phase8-restore-drill.sh' "$workflow"
  grep -Fq 'Protect previous Agent images for rollback' "$workflow"
  grep -Fq 'Mark phase 8 deployment started' "$workflow"
  grep -Fq 'Restore previous Agent images on failed deployment' "$workflow"
  grep -Fq "failure() && steps.capture_images.outcome == 'success'" "$workflow"
  if grep -Fq 'docker image prune -af' "$workflow"; then
    echo "Workflow deletes protected rollback tags: $workflow" >&2
    exit 1
  fi
done

# A second/overlapping restore run must never delete a container it does
# not own. Conversely, a newly created drill container must be removed.
touch "$fixture/agent.dump"
export MOCK_RESTORE_EXISTS=true
if bash "$repo/scripts/maersk-phase8-restore-drill.sh" "$fixture" >/dev/null 2>&1; then
  echo "Restore drill unexpectedly reused an existing container" >&2
  exit 1
fi
if grep -Fq 'rm -f -v dhole-phase8-restore-9876543' "$DOCKER_MOCK_LOG"; then
  echo "Restore drill removed a container owned by another run" >&2
  exit 1
fi
export MOCK_RESTORE_EXISTS=false
bash "$repo/scripts/maersk-phase8-restore-drill.sh" "$fixture"
grep -Fq 'rm -f -v dhole-phase8-restore-9876543' "$DOCKER_MOCK_LOG"

echo "Phase 8 image capture, rollback and guarded workflow regressions passed."
