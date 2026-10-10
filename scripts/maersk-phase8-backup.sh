#!/usr/bin/env bash
# Offline, operator-invoked snapshot for a stopped Dhole Agent worker.
# No credentials, cookies, or database rows are uploaded to GitHub.
set -euo pipefail

environment="${1:?Usage: maersk-phase8-backup.sh <staging|production> <durable-backup-root>}"
backup_root="${2:?Durable backup root required}"
die() { printf 'Phase 8 backup failed: %s\n' "$*" >&2; exit 1; }

case "$environment" in
  staging) project="dhole-staging"; network="dhole-staging" ;;
  production) project="dhole"; network="dhole" ;;
  *) die "Invalid environment" ;;
esac

[[ "$backup_root" = /* && "$backup_root" != /tmp/* && "$backup_root" != /var/tmp/* ]] || die "Use an absolute durable backup location"
[[ -d "$backup_root" && -w "$backup_root" ]] || die "Pre-create a writable durable backup root"
[[ -r "${DHOLE_ENV_FILE:-}" ]] || die "DHOLE_ENV_FILE must point to the corresponding environment file"
command -v docker >/dev/null || die "Docker is required"

# Do not copy a live Chromium profile, and never interrupt a running execution.
# Operators must drain the job queue and stop just this worker beforehand.
worker="$(docker ps -q --filter "label=com.docker.compose.project=$project" \
  --filter "label=com.docker.compose.service=dhole-agent-workers" | head -n 1)"
[[ -z "$worker" ]] || die "Agent worker is running; drain jobs and stop it before taking a consistent profile snapshot"

connection="$(sed -n 's/^AGENT_POSTGRES_CONNECTION_STRING=//p' "$DHOLE_ENV_FILE" | head -n 1 | tr -d '\r')"
[[ -n "$connection" ]] || die "Agent PostgreSQL connection is missing"
connection_value() {
  printf '%s' "$connection" | tr ';' '\n' | sed -n "s/^$1=//p" | head -n 1
}
host="$(connection_value Host)"
port="$(connection_value Port)"
database="$(connection_value Database)"
username="$(connection_value Username)"
password="$(connection_value Password)"
host="${host:-postgres}"
port="${port:-5432}"
[[ "$database" =~ ^[A-Za-z0-9_]+$ && -n "$username" && -n "$password" ]] || die "Invalid database configuration"

profile_volume="${project}_agent-browser-profiles"
keys_volume="${project}_agent-data-protection-keys"
docker volume inspect "$profile_volume" >/dev/null || die "Browser-profile volume does not exist: $profile_volume"
docker volume inspect "$keys_volume" >/dev/null || die "Data-protection volume does not exist: $keys_volume"

# Do not label a backup as consistent if an execution is still Running in SQL.
running="$(docker run --rm --network "$network" -e PGPASSWORD="$password" postgres:16-alpine \
  psql -At -h "$host" -p "$port" -U "$username" -d "$database" \
  -c 'SELECT COUNT(*) FROM agent."AgentExecutions" WHERE status = '\''Running'\'';')"
[[ "$running" == 0 ]] || die "There are $running Running executions; investigate before backup"

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
output="$backup_root/$environment/$stamp"
mkdir -p "$backup_root/$environment"
mkdir -m 700 "$output" || die "Could not create unique backup location"

docker run --rm --network "$network" -e PGPASSWORD="$password" \
  -v "$output:/backup" postgres:16-alpine \
  pg_dump -h "$host" -p "$port" -U "$username" -Fc -f /backup/agent.dump "$database"

docker run --rm -v "$profile_volume:/source:ro" -v "$output:/backup" alpine:3.20 \
  tar -C /source -czf /backup/browser-profiles.tar.gz .
docker run --rm -v "$keys_volume:/source:ro" -v "$output:/backup" alpine:3.20 \
  tar -C /source -czf /backup/agent-keys.tar.gz .

printf 'environment=%s\ncompose_project=%s\ncreated_epoch=%s\n' \
  "$environment" "$project" "$(date -u +%s)" > "$output/metadata.txt"
(cd "$output" && sha256sum agent.dump browser-profiles.tar.gz agent-keys.tar.gz metadata.txt > SHA256SUMS)
# Bind-mounted snapshots are created as container root. Return file ownership
# to the self-hosted runner while keeping archives private.
docker run --rm -v "$output:/backup" alpine:3.20 sh -c \
  "chown $(id -u):$(id -g) /backup/* && chmod 600 /backup/*"
echo "Snapshot created. Run preflight against: $output"
