#!/usr/bin/env bash
# Read-only worker/DB/standby diagnosis for the generation control plane.
# Never print credentials, user data, provider error messages or browser state.
set -euo pipefail
set +x
env_file="${1:?Usage: diagnose-generation-dispatch.sh <prepared-env>}"
[[ -r "$env_file" ]] || exit 2
conn="$(sed -n 's/^AGENT_POSTGRES_CONNECTION_STRING=//p' "$env_file" | head -n1 | tr -d '\r')"
[[ -n "$conn" ]] || { echo "GENERATION_DIAG_DB_CONFIG_MISSING"; exit 2; }
part() { printf '%s' "$conn" | tr ';' '\n' | sed -n "s/^$1=//p" | head -n1; }
user="$(part Username)"
password="$(part Password)"
db="$(part Database)"
host="$(part Host)"
port="$(part Port)"
[[ "$db" =~ ^[A-Za-z0-9_]+$ && -n "$password" && -n "$user" ]] || exit 2
host="${host:-postgres}"
port="${port:-5432}"
primary="$(docker ps -q --filter label=com.docker.compose.project=dhole-agent-prod1 --filter label=com.docker.compose.service=dhole-agent-workers-prod1)"
main_api="$(docker ps -q --filter label=com.docker.compose.project=dhole --filter label=com.docker.compose.service=dhole-agent-api)"
prod1_api="$(docker ps -q --filter label=com.docker.compose.project=dhole-agent-prod1 --filter label=com.docker.compose.service=dhole-agent-api-prod1)"
for role in primary_api prod1_api prod1_worker; do
  case "$role" in
    primary_api) target="$main_api" ;;
    prod1_api) target="$prod1_api" ;;
    prod1_worker) target="$primary" ;;
  esac
  if [[ -z "$target" || "$(printf '%s\n' "$target" | sed '/^$/d' | wc -l)" -ne 1 ]]; then
    echo "GENERATION_DIAG_RUNTIME role=$role state=missing_or_ambiguous"
    continue
  fi
  envlines="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$target")"
  flag="$(printf '%s\n' "$envlines" | sed -n 's/^GenerationOrchestrator__Enabled=//p' | head -n1)"
  actionref="$(printf '%s\n' "$envlines" | sed -n 's/^GenerationOrchestrator__ActionsRef=//p' | head -n1)"
  tokenstate=missing
  if printf '%s\n' "$envlines" | grep -q '^GenerationOrchestrator__GitHubToken=.'; then
    tokenstate=present
  fi
  printf 'GENERATION_DIAG_RUNTIME role=%s enabled=%s token=%s ref=%s\n' "$role" "${flag:-unset}" "$tokenstate" "${actionref:-unset}"
done
if [[ "$(printf '%s\n' "$primary" | sed '/^$/d' | wc -l)" -eq 1 ]]; then
  running=yes
  settings="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$primary")"
  enabled="$(printf '%s\n' "$settings" | sed -n 's/^GenerationOrchestrator__Enabled=//p' | head -n1)"
  token_present=no
  if printf '%s\n' "$settings" | grep -q '^GenerationOrchestrator__GitHubToken=.'; then
    token_present=yes
  fi
  printf 'GENERATION_DIAG_WORKER running=%s dispatch_enabled=%s credential_present=%s\n' \
    "$running" "${enabled:-unset}" "$token_present"
  # Only emit fixed uppercase diagnostic marker *counts*, never raw worker logs.
  docker logs --since 30m "$primary" 2>&1 |
    grep -oE 'GENERATION_(DISPATCHER_DISABLED|DISPATCHER_RUNNING|DISPATCH_CYCLE_FAILED|RULE_EVALUATION_FAILED|GITHUB_DISPATCHED|GITHUB_DISPATCH_FAILED|GITHUB_TOKEN_MISSING)' |
    sort | uniq -c || true
else
  echo "GENERATION_DIAG_PRIMARY_WORKER_COUNT_INVALID"
fi
sql="SELECT target_generation || '|' || status || '|' ||
       coalesce(diagnostic_code, 'none') || '|' ||
       (extract(epoch from now() - created_at_utc)::integer / 60)::text
     FROM agent.runtime_generation_requests
     ORDER BY created_at_utc DESC LIMIT 5;"
echo "GENERATION_DIAG_RECENT_REQUESTS target|status|diagnostic|age_min:"
docker run --rm --network dhole -e PGPASSWORD="$password" postgres:17-alpine \
  psql -X -q -At -v ON_ERROR_STOP=1 -h "$host" -p "$port" -U "$user" -d "$db" -c "$sql"
echo "GENERATION_DIAG_PROVISION_STATUS:"
docker run --rm --network dhole -e PGPASSWORD="$password" postgres:17-alpine \
  psql -X -q -At -v ON_ERROR_STOP=1 -h "$host" -p "$port" -U "$user" -d "$db" \
  -c "SELECT generation || '|' || status || '|' || coalesce(diagnostic_code, 'none') FROM agent.runtime_generation_provisions ORDER BY generation LIMIT 20;"
echo "GENERATION_DIAG_READINESS_STATUS:"
docker run --rm --network dhole -e PGPASSWORD="$password" postgres:17-alpine \
  psql -X -q -At -v ON_ERROR_STOP=1 -h "$host" -p "$port" -U "$user" -d "$db" \
  -c "SELECT generation || '|ready=' || ready::text || '|authorized=' || provider_authorized::text || '|age_min=' || (extract(epoch from now()-verified_at_utc)::integer / 60)::text FROM agent.runtime_generation_readiness ORDER BY generation LIMIT 20;"
for generation in prod1 prod2; do
  project="dhole-agent-$generation"
  if [[ "$generation" == prod1 ]]; then api_service=dhole-agent-api-prod1; worker_service=dhole-agent-workers-prod1;
  else api_service=dhole-agent-api; worker_service=dhole-agent-workers; fi
  for service in "$api_service" "$worker_service"; do
    id="$(docker ps -aq --filter "label=com.docker.compose.project=$project" \
      --filter "label=com.docker.compose.service=$service" | head -n1)"
    if [[ -z "$id" ]]; then
      printf 'GENERATION_DIAG_STANDBY generation=%s service=%s state=missing\n' "$generation" "$service"
    else
      state="$(docker inspect -f '{{.State.Status}}' "$id")"
      health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}not-defined{{end}}' "$id")"
      printf 'GENERATION_DIAG_STANDBY generation=%s service=%s state=%s health=%s\n' \
        "$generation" "$service" "$state" "$health"
    fi
  done
done
echo 'GENERATION_DIAG_READ_ONLY_COMPLETE'
