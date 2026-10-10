#!/usr/bin/env bash
# Read-only phase-8 database schema gate, executed AFTER Agent API migrates and
# BEFORE updated workers start. Never changes production data or schema.
set -euo pipefail

environment="${1:?Usage: maersk-phase8-schema-check.sh <staging|production>}"
die() { echo "Phase 8 schema gate: $*" >&2; exit 1; }
case "$environment" in
  staging) network="dhole-staging" ;;
  production) network="dhole" ;;
  *) die "Unsupported environment" ;;
esac

[[ -r "${DHOLE_ENV_FILE:-}" ]] || die "DHOLE_ENV_FILE must identify the selected environment"
command -v docker >/dev/null || die "Docker is required"
connection="$(sed -n 's/^AGENT_POSTGRES_CONNECTION_STRING=//p' "$DHOLE_ENV_FILE" | head -n1 | tr -d '\r')"
[[ -n "$connection" ]] || die "Agent PostgreSQL connection is missing"
value() { printf '%s' "$connection" | tr ';' '\n' | sed -n "s/^$1=//p" | head -n1; }
host="$(value Host)"; host="${host:-postgres}"
port="$(value Port)"; port="${port:-5432}"
database="$(value Database)"
username="$(value Username)"
password="$(value Password)"
[[ "$database" =~ ^[A-Za-z0-9_]+$ && -n "$username" && -n "$password" ]] || die "Invalid connection configuration"

# AgentDataSeeder runs EF MigrateAsync during API startup. Health alone is not
# proof these three migrations were actually applied to the selected database.
query="SELECT CASE WHEN
  to_regclass('agent.execution_leases') IS NOT NULL
  AND to_regclass('agent.maersk_circuits') IS NOT NULL
  AND to_regclass('agent.maersk_circuit_events') IS NOT NULL
  AND to_regclass('agent.maersk_health_alerts') IS NOT NULL
  -- Phase-2 idempotency requires the latest migration. The earlier 3-column
  -- unique index cannot support a timeout followed by CAPTCHA in one job.
  AND EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'agent' AND table_name = 'maersk_circuit_events'
      AND column_name = 'execution_id'
  )
  AND EXISTS (
    SELECT 1 FROM pg_indexes
    WHERE schemaname = 'agent'
      AND indexname = 'ix_maersk_circuit_events_failure_reason'
      AND indexdef ILIKE '%(provider_id, execution_id, event_type, reason_code)%'
  )
  AND EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'agent' AND table_name = 'AgentExecutions'
      AND column_name = 'next_attempt_at_utc'
  )
THEN 'PHASE8_SCHEMA_READY' ELSE 'PHASE8_SCHEMA_MISSING' END;"
result="$(docker run --rm --network "$network" -e PGPASSWORD="$password" postgres:16-alpine \
  psql -X -v ON_ERROR_STOP=1 -At -h "$host" -p "$port" -U "$username" -d "$database" -c "$query")" \
  || die "Could not query Agent database"
[[ "$result" == PHASE8_SCHEMA_READY ]] || die "Required lease/circuit/monitoring tables or execution column are absent"
echo "PHASE8_SCHEMA_READY: circuit, queue, monitoring and phase-2 dedup schema verified in $environment (read-only verification)."
