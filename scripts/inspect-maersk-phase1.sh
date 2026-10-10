#!/usr/bin/env bash
# Read-only phase 1 inventory; does not reveal secret values.
set -euo pipefail
set +x
case "$1" in
  staging) project=dhole-staging; network=dhole-staging ;;
  production) project=dhole; network=dhole ;;
  *) echo "Usage: $0 <staging|production> <prepared-env-file>" >&2; exit 2 ;;
esac
environment="$1"; env_file="$2"
test -r "$env_file" || { echo 'Missing runtime env file' >&2; exit 2; }
command -v docker >/dev/null || exit 2
read_key() {
  grep -E "^$1=" "$2" | tail -n1 | cut -d= -f2- | tr -d '\r' || true
}
runtime_key() {
  docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$1" |
    grep -E "^$2=" | tail -n1 | cut -d= -f2- | tr -d '\r' || true
}
sanitize() {
  if [ "$1" = AgentQueue__MaxConcurrentMaersk ]; then
    if [[ "$2" =~ ^[0-9]+$ ]]; then echo "$2"; else echo UNKNOWN; fi
  else
    case "$2" in true|false|0|1) echo "$2" ;; *) echo UNKNOWN ;; esac
  fi
}
flags='MaerskCircuit__Enabled MaerskMonitoring__Enabled AgentQueue__ConcurrentDispatcherEnabled AgentQueue__MaxConcurrentMaersk'
printf 'phase1|environment|%s\nphase1|project|%s\nphase1|utc|%s\n' \
  "$environment" "$project" "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
conn="$(read_key AGENT_POSTGRES_CONNECTION_STRING "$env_file")"
test -n "$conn" || { echo 'Database connection missing' >&2; exit 1; }
incomplete=0
for flag in $flags; do
  printf 'source|%s|%s\n' "$flag" "$(sanitize "$flag" "$(read_key "$flag" "$env_file")")"
done
for service in dhole-agent-api dhole-agent-workers; do
  ids="$(docker ps -q --filter "label=com.docker.compose.project=$project" \
    --filter "label=com.docker.compose.service=$service")"
  if [ "$(printf '%s\n' "$ids" | sed '/^$/d' | wc -l)" -ne 1 ]; then
    printf 'runtime|%s|MISSING_OR_MULTIPLE\n' "$service"
    incomplete=1
    continue
  fi
  image="$(docker inspect --format '{{.Image}}' "$ids")"
  revision="$(docker inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$ids" 2>/dev/null || true)"
  [[ "$revision" =~ ^[a-f0-9]{40}$ ]] || revision=UNKNOWN
  printf 'runtime|%s|image_id|%s\nruntime|%s|revision|%s\n' \
    "$service" "$image" "$service" "$revision"
  if [ "$(runtime_key "$ids" Postgres__ConnectionString)" = "$conn" ]; then
    printf 'runtime|%s|db|MATCHES_ENV\n' "$service"
  else
    printf 'runtime|%s|db|MISMATCH\n' "$service"
    incomplete=1
  fi
  for flag in $flags; do
    raw="$(runtime_key "$ids" "$flag")"
    value="$(sanitize "$flag" "$raw")"
    printf 'runtime|%s|%s|%s\n' "$service" "$flag" "$value"
    [ "$value" != UNKNOWN ] || incomplete=1
    source="$(read_key "$flag" "$env_file")"
    if [ -n "$source" ] && [ "$source" != "$raw" ]; then
      printf 'runtime|%s|%s|SOURCE_MISMATCH\n' "$service" "$flag"
      incomplete=1
    fi
  done
done
schema_ok=0
if DHOLE_ENV_FILE="$env_file" bash "$(dirname "$0")/maersk-phase8-schema-check.sh" "$environment" >/dev/null; then
  echo 'database|schema|READY'
  schema_ok=1
else
  echo 'database|schema|MISSING_OR_UNVERIFIED'
  incomplete=1
fi
if [ "$schema_ok" -eq 1 ]; then
  host="$(echo "$conn" | tr ';' '\n' | sed -n 's/^Host=//p' | head -n1)"
  port="$(echo "$conn" | tr ';' '\n' | sed -n 's/^Port=//p' | head -n1)"
  db="$(echo "$conn" | tr ';' '\n' | sed -n 's/^Database=//p' | head -n1)"
  dbuser="$(echo "$conn" | tr ';' '\n' | sed -n 's/^Username=//p' | head -n1)"
  dbpass="$(echo "$conn" | tr ';' '\n' | sed -n 's/^Password=//p' | head -n1)"
  [[ "$db" =~ ^[A-Za-z0-9_]+$ && -n "$dbuser" && -n "$dbpass" ]] || exit 1
  docker run --rm -i --network "$network" -e PGPASSWORD="$dbpass" \
    -e PGOPTIONS='-c default_transaction_read_only=on' postgres:16-alpine \
    psql -X -v ON_ERROR_STOP=1 -At -h "$host" -p "$port" -U "$dbuser" -d "$db" <<'SQL'
SELECT 'profiles|total=' || count(*) || '|blocked=' || count(*) FILTER (WHERE status='Blocked')
FROM agent."BrowserProfiles"
WHERE provider_id='2155f49f-ef15-43a5-9cc1-55203598af59' AND NOT is_deleted;
SELECT 'circuit|' || COALESCE((SELECT state || '|operator=' || requires_operator::text ||
  '|reason=' || COALESCE(reason_code,'NONE') FROM agent.maersk_circuits
  WHERE provider_id='2155f49f-ef15-43a5-9cc1-55203598af59'), 'NO_ROW');
SELECT 'executions|' || status || '|' || count(*) FROM agent."AgentExecutions"
WHERE provider_id='2155f49f-ef15-43a5-9cc1-55203598af59' GROUP BY status ORDER BY status;
SELECT 'incident|' || id || '|' || status || '|' || COALESCE(error_code,'NONE')
FROM agent."AgentExecutions"
WHERE provider_id='2155f49f-ef15-43a5-9cc1-55203598af59'
AND id IN (
'34410256-cb49-446e-9c3b-497072637428',
'87904432-169b-4626-b9e5-a0dae2e3bd8d',
'26948205-b888-4745-bd23-4b3163524ba1',
'37cc9b0c-e01e-4d61-bdb3-8ab62f5cf69d',
'170cdb2d-4c52-455e-8034-8415d221ce90',
'ca4ee54c-43b1-4430-a636-19852b6a0c21',
'8c40340d-ca99-4cab-90f2-b42fdf9dfc') ORDER BY id;
SQL
fi
if [ "$incomplete" -eq 0 ]; then echo 'phase1|status|INVENTORY_CAPTURED'
else echo 'phase1|status|INCOMPLETE_DO_NOT_ACTIVATE'; exit 1; fi
