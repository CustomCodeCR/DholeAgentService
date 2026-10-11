#!/usr/bin/env bash
# One-time repair: restore exactly one missing staging provider parent from
# production. Preserve all credential rows, IDs, and production data.
set -Eeuo pipefail
set +x
[[ "$MAERSK_REPAIR_APPROVAL" == RESTORE_STAGING_PROVIDER_FROM_PRODUCTION ]] || exit 2
staging_env="$1"
production_env="$2"
[[ -r "$staging_env" && -r "$production_env" ]] || exit 2
command -v docker >/dev/null
command -v python3 >/dev/null

worker="$(docker ps -q --filter label=com.docker.compose.project=dhole-staging \
  --filter label=com.docker.compose.service=dhole-agent-workers)"
[[ -z "$worker" ]] || { echo 'PROVIDER_REPAIR_BLOCKED: stop staging worker after backup' >&2; exit 1; }

db_query() {
  local target="$1" sql="$2" envfile conn host port name username password network
  if [[ "$target" == staging ]]; then
    envfile="$staging_env"; network=dhole-staging
  else
    envfile="$production_env"; network=dhole
  fi
  conn="$(sed -n 's/^AGENT_POSTGRES_CONNECTION_STRING=//p' "$envfile" | head -n1 | tr -d '\r')"
  [[ -n "$conn" ]] || return 3
  setting() { printf '%s' "$conn" | tr ';' '\n' | sed -n "s/^$1=//p" | head -n1; }
  host="$(setting Host)"; [[ -n "$host" ]] || host=postgres
  port="$(setting Port)"; [[ -n "$port" ]] || port=5432
  name="$(setting Database)"; username="$(setting Username)"; password="$(setting Password)"
  [[ "$name" =~ ^[A-Za-z0-9_]+$ && "$port" =~ ^[0-9]+$ &&
     -n "$username" && -n "$password" ]] || return 4
  docker run --rm --network "$network" \
    -e PGPASSWORD="$password" -e PGOPTIONS='-c statement_timeout=30000' \
    postgres:17-alpine psql -X -v ON_ERROR_STOP=1 -At \
    -h "$host" -p "$port" -U "$username" -d "$name" -c "$sql"
}

running="$(db_query staging 'SELECT COUNT(*) FROM agent."AgentExecutions" WHERE status='\''Running'\'';')"
[[ "$running" == 0 ]] || { echo 'PROVIDER_REPAIR_BLOCKED: running executions' >&2; exit 1; }
orphans="$(db_query staging 'SELECT DISTINCT c.provider_id FROM agent."AgentCredentials" c LEFT JOIN agent."AgentProviders" p ON p.id=c.provider_id WHERE p.id IS NULL;')"
count="$(printf '%s\n' "$orphans" | sed '/^$/d' | wc -l)"
[[ "$count" == 1 ]] || { echo "PROVIDER_REPAIR_BLOCKED: missing parent count $count != 1" >&2; exit 1; }
provider_id="$(printf '%s\n' "$orphans" | head -n1)"
[[ "$provider_id" =~ ^[0-9a-fA-F-]{36}$ ]] || exit 1
json="$(db_query production "SELECT to_jsonb(p)::text FROM agent.\"AgentProviders\" p WHERE p.id='$provider_id'::uuid;")"
[[ -n "$json" ]] || { echo 'PROVIDER_REPAIR_BLOCKED: provider missing in production' >&2; exit 1; }
printf '%s' "$json" | python3 -c '
import json,sys
v=json.load(sys.stdin)
assert v.get("id","").lower()==sys.argv[1].lower()
assert v.get("code") and v.get("name")' "$provider_id" >/dev/null || exit 1
payload="$(printf '%s' "$json" | base64 -w0)"
[[ "$payload" =~ ^[A-Za-z0-9+/=]+$ ]] || exit 1
# Read-only production. INSERT only the single missing staging parent.
db_query staging "BEGIN;
SET LOCAL lock_timeout = '10s';
LOCK TABLE agent.\"AgentProviders\" IN SHARE ROW EXCLUSIVE MODE;
INSERT INTO agent.\"AgentProviders\"
  SELECT p.* FROM jsonb_populate_record(NULL::agent.\"AgentProviders\",
    convert_from(decode('$payload','base64'),'UTF8')::jsonb) p
  WHERE p.id='$provider_id'::uuid
    AND NOT EXISTS (SELECT 1 FROM agent.\"AgentProviders\" q WHERE q.id=p.id);
COMMIT;" >/dev/null
remaining="$(db_query staging 'SELECT COUNT(DISTINCT c.provider_id) FROM agent."AgentCredentials" c LEFT JOIN agent."AgentProviders" p ON p.id=c.provider_id WHERE p.id IS NULL;')"
[[ "$remaining" == 0 ]] || { echo 'PROVIDER_REPAIR_FAILED: orphan remains, stop' >&2; exit 1; }
echo 'STAGING_PROVIDER_PARENT_RESTORED: one provider copied; credentials preserved, production unchanged.'
