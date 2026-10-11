#!/usr/bin/env bash
# Host-level, bounded recovery of failed Agent/Hermes runtime containers.
# Never rotates provider identity, Chromium cookies/profiles, IP or environment.
# Intentionally DOES NOT recover from CAPTCHA, MFA, 401/403 or 429.
set -Eeuo pipefail
set +x

environment="${1:-}"
mode="${2:---check}"
system_dir="${3:-}"
env_file="${4:-}"
die() { echo "AGENT_RUNTIME_RECOVERY_BLOCKED: $*" >&2; exit 1; }

case "$environment" in
  production) project=dhole; infra=dhole-infrastructure; tag=latest; network=dhole
              infrastructure_compose=docker-compose.infrastructure.yml; hermes_service=hermes-agent ;;
  staging) project=dhole-staging; infra=dhole-staging-infra; tag=staging; network=dhole-staging
           infrastructure_compose=docker-compose.staging-infra.yml; hermes_service=hermes-agent-staging ;;
  *) die 'Usage: <production|staging> <--check|--apply> <DholeSystem checkout> <prepared env file>' ;;
esac
[[ "$mode" == --check || "$mode" == --apply ]] || die 'Mode must be --check or --apply'
[[ -r "$system_dir/deploy/docker-compose.services.yml" ]] || die 'Missing runtime Compose manifest'
[[ -r "$system_dir/deploy/$infrastructure_compose" ]] || die 'Missing Hermes Compose manifest'
[[ -r "$env_file" ]] || die 'Prepared env is missing'
command -v docker >/dev/null || die 'Docker is unavailable'
command -v flock >/dev/null || die 'flock is unavailable'

# Lock per environment. Concurrency with deploys is handled by Actions too.
state_dir="${AGENT_RUNTIME_RECOVERY_STATE_DIR:-/opt/dhole/.maersk-runtime-recovery}"
mkdir -p "$state_dir"
chmod 700 "$state_dir"
exec 9>"$state_dir/$environment.lock"
flock -n 9 || { echo "AGENT_RUNTIME_RECOVERY_SKIPPED: another recovery is running"; exit 0; }

container_id() {
  docker ps -aq --filter "label=com.docker.compose.project=$1" \
    --filter "label=com.docker.compose.service=$2" | head -n 2
}
state_of() {
  docker inspect --format '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$1"
}
get_flag() {
  docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$1" \
    | grep -Fxc "$2=true" || true
}

api_id="$(container_id "$project" dhole-agent-api)"
worker_id="$(container_id "$project" dhole-agent-workers)"
# A missing or duplicate API/Worker prevents proof that the breaker is enabled.
[[ "$(printf '%s\n' "$api_id" | sed '/^$/d' | wc -l)" -eq 1 ]] ||
  die 'Expected exactly one Agent API container'
[[ "$(printf '%s\n' "$worker_id" | sed '/^$/d' | wc -l)" -eq 1 ]] ||
  die 'Expected exactly one Agent Worker container'
for id in "$api_id" "$worker_id"; do
  if [[ "$(get_flag "$id" MaerskCircuit__Enabled)" != 1 ]]; then
    echo 'AGENT_RUNTIME_RECOVERY_INACTIVE: circuit disabled; no automatic rebuild'
    exit 0
  fi
done

services=(dhole-agent-api dhole-agent-workers "$hermes_service")
projects=("$project" "$project" "$infra")
candidates=()
for index in 0 1 2; do
  service="${services[$index]}"
  target_project="${projects[$index]}"
  id="$(container_id "$target_project" "$service")"
  [[ "$(printf '%s\n' "$id" | sed '/^$/d' | wc -l)" -eq 1 ]] ||
    die "Missing or duplicate container for $service; manual diagnosis required"
  runtime_state="$(state_of "$id")"
  status="${runtime_state%%|*}"
  health="${runtime_state#*|}"
  if [[ "$status" == restarting || "$status" == exited ||
        "$status" == dead || "$health" == unhealthy ]]; then
    candidates+=("$service")
    echo "AGENT_RUNTIME_RECOVERY_CANDIDATE: $environment/$service state=$status health=$health"
  else
    echo "AGENT_RUNTIME_RECOVERY_HEALTHY: $environment/$service state=$status health=$health"
  fi
done

(( ${#candidates[@]} )) || { echo 'AGENT_RUNTIME_RECOVERY_NO_ACTION'; exit 0; }
[[ "$mode" == --apply ]] || { echo 'AGENT_RUNTIME_RECOVERY_CHECK_ONLY'; exit 0; }

# Cooldown prevents repeated restarts of an irreparably broken Worker.
last_file="$state_dir/$environment.last"
now="$(date -u +%s)"
if [[ -f "$last_file" ]]; then
  last="$(cat "$last_file")"
  if [[ "$last" =~ ^[0-9]+$ ]] && (( now - last < 1800 )); then
    echo 'AGENT_RUNTIME_RECOVERY_COOLDOWN: next check can retry after 30 minutes'
    exit 0
  fi
fi
available_kb="$(df -Pk / | awk 'NR==2 {print $4}')"
[[ "$available_kb" =~ ^[0-9]+$ ]] || die 'Could not read disk space'
(( available_kb >= 2 * 1024 * 1024 )) ||
  die 'Less than 2 GiB disk free; no recreate to protect persistent volumes'

# Commit cooldown BEFORE making Docker changes, to bound repeated failures.
printf '%s\n' "$now" > "$last_file"
chmod 600 "$last_file"
for service in "${candidates[@]}"; do
  if [[ "$service" == "$hermes_service" ]]; then
    compose="$system_dir/deploy/$infrastructure_compose"
    target_project="$infra"
  else
    compose="$system_dir/deploy/docker-compose.services.yml"
    target_project="$project"
  fi

  args=(--env-file "$env_file" -f "$compose")
  if [[ "$environment" == staging && "$service" != "$hermes_service" ]]; then
    args+=(-f "$system_dir/deploy/docker-compose.staging.yml")
  fi
  # Do not pull/build anything, prune volumes, copy cookies or touch PostgreSQL.
  DHOLE_NETWORK="$network" DHOLE_IMAGE_TAG="$tag" DHOLE_ENV_FILE="$env_file" \
    docker compose "${args[@]}" -p "$target_project" config -q ||
    die "Compose config is invalid for $service"
  echo "AGENT_RUNTIME_RECOVERY_RECREATING: $environment/$service"
  DHOLE_NETWORK="$network" DHOLE_IMAGE_TAG="$tag" DHOLE_ENV_FILE="$env_file" \
    docker compose "${args[@]}" -p "$target_project" \
    up -d --no-deps --force-recreate --pull never "$service" ||
    die "Docker recreation failed for $service; cooldown remains active"
done
echo 'AGENT_RUNTIME_RECOVERY_FINISHED: persistent data and provider sessions preserved'
