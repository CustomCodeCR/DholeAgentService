#!/usr/bin/env bash
# Restore a phase-8 PostgreSQL dump into a disposable, network-isolated server.
# The live Agent database and browser volumes are NEVER modified.
set -euo pipefail
backup_dir="${1:?Usage: maersk-phase8-restore-drill.sh <verified-backup-directory>}"
die() { echo "Phase 8 isolated restore drill: $*" >&2; exit 1; }

[[ "$backup_dir" = /* && -r "$backup_dir/agent.dump" ]] || die "An absolute directory with agent.dump is required"
pg_major="$(sed -n 's/^postgres_major=//p' "$backup_dir/metadata.txt" 2>/dev/null | head -n 1 || true)"
pg_major="${pg_major:-16}" # Legacy snapshots were taken using PostgreSQL 16.
case "$pg_major" in 16|17) ;; *) die 'Unsupported PostgreSQL dump major version' ;; esac
[[ "${GITHUB_RUN_ID:-}" =~ ^[0-9]+$ ]] || die "A valid GitHub Actions run ID is required"
command -v docker >/dev/null || die "Docker is required"
command -v openssl >/dev/null || die "OpenSSL is required"

container="dhole-phase8-restore-$GITHUB_RUN_ID"
# Host bind mount stays read-only and database is created inside a disposable
# anonymous Docker volume; --network none eliminates external/provider traffic.
# Do not remove an existing container: its ownership belongs to another run.
# Register cleanup only after we have successfully started OUR isolated database.
if docker container inspect "$container" >/dev/null 2>&1; then
  die "Restore container name already exists; refusing to reuse it"
fi
log="$(mktemp)"
chmod 600 "$log"
created=false
cleanup() {
  if [[ "$created" == true ]]; then
    docker rm -f -v "$container" >/dev/null 2>&1 || true
  fi
  rm -f "$log"
}
trap cleanup EXIT

password="$(openssl rand -hex 24)"
docker run -d --name "$container" --network none \
  --mount "type=bind,src=$backup_dir,dst=/backup,readonly" \
  -e POSTGRES_PASSWORD="$password" "postgres:$pg_major-alpine" >/dev/null
created=true

ready=false
for attempt in $(seq 1 40); do
  if docker exec "$container" pg_isready -U postgres -d postgres >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 2
done
[[ "$ready" == true ]] || die "Disposable PostgreSQL 16 server did not become healthy"

if ! docker exec "$container" pg_restore --no-owner --no-acl \
    --exit-on-error --single-transaction -U postgres -d postgres \
    /backup/agent.dump >"$log" 2>&1; then
  if grep -qi 'violates foreign key constraint' "$log"; then
    die 'PHASE8_RESTORE_REFERENCE_INTEGRITY_FAILED: archive contains orphan references. Source DB and backup are unchanged; do not bypass FK validation or delete credentials.'
  fi
  die "Restoring backup into isolated PostgreSQL failed (details kept private)"
fi
table_count="$(docker exec "$container" psql -At -U postgres -d postgres \
  -c "SELECT COUNT(*) FROM pg_catalog.pg_tables WHERE schemaname = 'agent';")"
[[ "$table_count" =~ ^[0-9]+$ && "$table_count" -gt 0 ]] \
  || die "Restored database has no Agent tables"

echo "PHASE8_RESTORE_DRILL_OK: PostgreSQL backup restored with $table_count Agent tables (isolated, no network)."
