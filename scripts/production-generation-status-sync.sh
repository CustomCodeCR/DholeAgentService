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

# Identify exactly one processing owner among arbitrary numbered projects.
# A registered generation without a RUNNING worker is never marked active.
active_count=0
generation=""
while IFS='|' read -r id project service; do
  [[ -n "$id" ]] || continue
  if [[ "$project" == dhole && "$service" == dhole-agent-workers ]]; then
    generation=prod
  elif [[ "$project" =~ ^dhole-agent-(prod[1-9][0-9]*)$ ]]; then
    candidate="${BASH_REMATCH[1]}"
    [[ "${#candidate}" -le 64 ]] || continue
    if [[ "$candidate" == prod1 && "$service" == dhole-agent-workers-prod1 ]] ||
       [[ "$candidate" != prod1 && "$service" == dhole-agent-workers ]]; then
      generation="$candidate"
    else
      continue
    fi
  else
    continue
  fi
  active_count=$((active_count + 1))
done < <(docker ps --format '{{.ID}}|{{.Label "com.docker.compose.project"}}|{{.Label "com.docker.compose.service"}}')
if (( active_count != 1 )); then
  echo "GENERATION_STATUS_AMBIGUOUS: no authoritative single active Worker" >&2
  exit 1
fi
query="UPDATE agent.runtime_generation_state
  SET active_generation='$generation', updated_at_utc=NOW()
  WHERE singleton=true AND active_generation IS DISTINCT FROM '$generation';"
docker run --rm --network dhole \
  -e PGPASSWORD="$password" postgres:17-alpine \
  psql -X -q -v ON_ERROR_STOP=1 \
  -h "$host" -p "$port" -U "$username" -d "$db" -c "$query" >/dev/null
echo "GENERATION_STATUS_VERIFIED: active=$generation from a single running Docker Worker"
