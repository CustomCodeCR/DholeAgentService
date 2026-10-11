#!/usr/bin/env bash
# Read-only Docker observation; updates ONLY the active generation summary in DB.
# Never starts, stops, rebuilds or modifies browsers, cookies, or credentials.
set -euo pipefail
set +x
file="${1:?Usage: <prepared-production-env-file>}"
[[ -r "$file" ]] || { echo "GENERATION_STATUS_CONFIG_UNAVAILABLE"; exit 1; }
command -v docker >/dev/null
connection="$(sed -n 's/^AGENT_POSTGRES_CONNECTION_STRING=//p' "$file" | head -n1 | tr -d '\r')"
[[ -n "$connection" ]] || { echo "GENERATION_STATUS_DB_UNAVAILABLE"; exit 1; }
value() { printf '%s' "$connection" | tr ';' '\n' | sed -n "s/^$1=//p" | head -n1; }
db="$(value Database)"; username="$(value Username)"; password="$(value Password)"
host="$(value Host)"; port="$(value Port)"
host="${host:-postgres}";port="${port:-5432}"
[[ "$db" =~ ^[A-Za-z0-9_]+$ && -n "$username" && -n "$password" ]] || exit 1

ids_primary="$(docker ps -q --filter 'label=com.docker.compose.project=dhole' --filter 'label=com.docker.compose.service=dhole-agent-workers')"
ids_alternative="$(docker ps -q --filter 'label=com.docker.compose.project=dhole-agent-prod1' --filter 'label=com.docker.compose.service=dhole-agent-workers-prod1')"
count() { printf '%s\n' "$1" | sed '/^$/d' | wc -l; }
primary="$(count "$ids_primary")"; alternative="$(count "$ids_alternative")"
if (( primary + alternative != 1 )); then
  echo "GENERATION_STATUS_AMBIGUOUS: no authoritative single active Worker" >&2
  exit 1
fi
if (( primary == 1 )); then generation=prod; else generation=prod1; fi
query="UPDATE agent.runtime_generation_state
  SET active_generation='$generation', updated_at_utc=NOW()
  WHERE singleton=true;"
docker run --rm --network dhole \
  -e PGPASSWORD="$password" postgres:17-alpine \
  psql -X -q -v ON_ERROR_STOP=1 \
  -h "$host" -p "$port" -U "$username" -d "$db" -c "$query" >/dev/null
echo "GENERATION_STATUS_VERIFIED: active=$generation from a single running Docker Worker"
