#!/usr/bin/env bash
# Phase-2 staging-only, read-only preflight for orphan provider credentials.
# DO NOT edit /opt/dhole, browser profiles, source database or production.
set -euo pipefail
set +x

env_file=/opt/dhole/.env.staging
backup_dir=/home/dhole-agent-phase2-backups/staging/20261010T154831Z
test -r "$env_file" || { echo 'STAGING_ENV_MISSING'; exit 1; }
test -s "$backup_dir/agent.dump" || { echo 'PRECHANGE_BACKUP_MISSING'; exit 1; }
test -s "$backup_dir/SHA256SUMS" || { echo 'PRECHANGE_BACKUP_CHECKSUMS_MISSING'; exit 1; }

# Backup preflight checks metadata, sha256 sums and archived profile/keyrings.
# It does NOT write to PostgreSQL or try to bypass restore FK constraints.
bash scripts/maersk-phase8-preflight.sh staging "$backup_dir" "$env_file"
echo 'PRECHANGE_STAGING_BACKUP_ARCHIVES_VALID=true'

connection="$(sed -n 's/^AGENT_POSTGRES_CONNECTION_STRING=//p' "$env_file" | tail -n 1 | tr -d '\r')"
if [[ -z "$connection" ]]; then
  pg_user="$(sed -n 's/^POSTGRES_USER=//p' "$env_file" | tail -n1 | tr -d '\r')"
  pg_pass="$(sed -n 's/^POSTGRES_PASSWORD=//p' "$env_file" | tail -n1 | tr -d '\r')"
  [[ -n "$pg_user" && -n "$pg_pass" ]] || { echo 'STAGING_DATABASE_CREDENTIALS_UNAVAILABLE'; exit 1; }
  connection="Host=postgres;Port=5432;Database=dhole_agent;Username=$pg_user;Password=$pg_pass"
fi
value() { printf '%s' "$connection" | tr ';' '\n' | sed -n "s/^$1=//p" | head -n 1; }
host="$(value Host)"
port="$(value Port)"
dbname="$(value Database)"
dbuser="$(value Username)"
dbpass="$(value Password)"
# Fail closed: the staging postgres service lives on the staging Docker network.
[[ "$host" == "postgres" && "$dbname" == "dhole_agent" && "$port" == "5432" ]] ||
  { echo 'STAGING_DATABASE_TARGET_MISMATCH'; exit 1; }
[[ -n "$dbuser" && -n "$dbpass" ]] || { echo 'STAGING_DATABASE_AUTH_MISSING'; exit 1; }
test "$(docker network inspect dhole-staging -f '{{.Name}}')" = dhole-staging

docker run --rm -i --network dhole-staging -e PGPASSWORD="$dbpass" \
  -e PGOPTIONS='-c default_transaction_read_only=on' \
  postgres:17-alpine psql -X -v ON_ERROR_STOP=1 -At \
  -h "$host" -p "$port" -U "$dbuser" -d "$dbname" <<'SQL'
BEGIN READ ONLY;
SELECT 'ORPHAN_PROVIDER_CREDENTIAL_COUNT=' || COUNT(*)
  FROM agent."AgentCredentials" c
  LEFT JOIN agent."AgentProviders" p ON p.id=c.provider_id WHERE p.id IS NULL;
SELECT 'ACTIVE_EXECUTIONS=' || COUNT(*) FROM agent."AgentExecutions" WHERE status='Running';
DO $$
DECLARE
  candidate_id uuid;
  orphan_count bigint;
  usage_count bigint;
  r record;
BEGIN
  SELECT count(*) INTO orphan_count FROM agent."AgentCredentials" c
   LEFT JOIN agent."AgentProviders" p ON p.id=c.provider_id WHERE p.id IS NULL;
  IF orphan_count <> 1 THEN
    RAISE EXCEPTION 'Expected exactly one orphaned credential, got %', orphan_count;
  END IF;
  SELECT c.id INTO candidate_id FROM agent."AgentCredentials" c
   LEFT JOIN agent."AgentProviders" p ON p.id=c.provider_id WHERE p.id IS NULL;
  IF candidate_id = '70ccf773-c95a-4ed8-9905-8df77cd1c126'::uuid THEN
    RAISE EXCEPTION 'Current Maersk credential is marked orphaned: do not delete';
  END IF;
  FOR r IN SELECT table_schema, table_name, column_name
    FROM information_schema.columns
    WHERE table_schema='agent' AND udt_name='uuid'
      AND table_name NOT IN ('AgentCredentials','AgentProviders')
  LOOP
    EXECUTE format('SELECT COUNT(*) FROM %I.%I WHERE %I = $1',
      r.table_schema, r.table_name, r.column_name)
      INTO usage_count USING candidate_id;
    IF usage_count > 0 THEN
      RAISE EXCEPTION 'Orphan credential is referenced by %.%: refuse deletion',
        r.table_name, r.column_name;
    END IF;
  END LOOP;
  RAISE NOTICE 'STAGING_ORPHAN_CREDENTIAL_REFERENCES=0';
END
$$;
COMMIT;
SQL

echo 'STAGING_ORPHAN_AUDIT_COMPLETE_READ_ONLY=true'
