#!/usr/bin/env bash
# Phase 8: fail-closed pre-deploy validation. Never creates/deletes data.
set -euo pipefail

environment="${1:?Usage: maersk-phase8-preflight.sh <staging|production> <backup-directory> <env-file>}"
backup_dir="${2:?Backup directory required}"
env_file="${3:?Runtime env file required}"

die() { printf 'Phase 8 preflight failed: %s\n' "$*" >&2; exit 1; }
case "$environment" in staging|production) ;; *) die "Unknown environment";; esac
[[ "$backup_dir" = /* ]] || die "Backup directory must be an absolute path"
[[ "$backup_dir" != /tmp/* && "$backup_dir" != /var/tmp/* ]] || die "Backups must be on durable storage"
[[ -d "$backup_dir" && -r "$env_file" ]] || die "Backup directory or runtime env file missing"
command -v sha256sum >/dev/null || die "sha256sum missing"
command -v tar >/dev/null || die "tar missing"
command -v docker >/dev/null || die "docker missing"

for f in agent.dump browser-profiles.tar.gz agent-keys.tar.gz SHA256SUMS metadata.txt; do
  [[ -s "$backup_dir/$f" ]] || die "Missing/empty backup artifact: $f"
done

project=dhole
if [[ "$environment" == staging ]]; then project=dhole-staging; fi
grep -Fxq "environment=$environment" "$backup_dir/metadata.txt" || die "Backup environment mismatch"
grep -Fxq "compose_project=$project" "$backup_dir/metadata.txt" || die "Backup project mismatch"
expected_database="$(sed -n 's/^AGENT_POSTGRES_CONNECTION_STRING=//p' "$env_file" | head -n 1 | tr -d '\r' | tr ';' '\n' | sed -n 's/^Database=//p' | head -n 1)"
[[ -n "$expected_database" ]] || die "Agent database is unspecified"
grep -Fxq "database_name=$expected_database" "$backup_dir/metadata.txt" || die "Backup database mismatch"
grep -Fxq "profile_volume=${project}_agent-browser-profiles" "$backup_dir/metadata.txt" || die "Profile volume mismatch"
grep -Fxq "keys_volume=${project}_agent-data-protection-keys" "$backup_dir/metadata.txt" || die "Key-ring volume mismatch"
# Fresh evidence is required for every deployment (24h maximum).
now="$(date -u +%s)"
created="$(sed -n 's/^created_epoch=//p' "$backup_dir/metadata.txt" | head -n 1)"
[[ "$created" =~ ^[0-9]+$ ]] || die "Invalid backup timestamp"
(( created <= now && now - created <= 86400 )) || die "Backup evidence older than 24 hours"

# A backup must be complete and tamper-evident; a digest does not replace
# a full restore drill, which is documented separately.
(cd "$backup_dir" && sha256sum -c --status SHA256SUMS) || die "Backup checksum mismatch"
tar -tzf "$backup_dir/browser-profiles.tar.gz" >/dev/null || die "Corrupt browser profile archive"
tar -tzf "$backup_dir/agent-keys.tar.gz" >/dev/null || die "Corrupt data-protection archive"
docker run --rm -v "$backup_dir:/backup:ro" postgres:16-alpine \
  pg_restore --file /dev/null /backup/agent.dump >/dev/null || die "Corrupt PostgreSQL archive"

# First rollout remains opt-in. An already activated installation can
# upgrade only after the same backup/restore gates and an explicit active
# redeploy acknowledgment, with both protection and monitoring still on.
read_flag() {
  sed -n "s/^$1=//p" "$env_file" | tail -n 1 | tr -d '\r' | tr '[:upper:]' '[:lower:]'
}
circuit="$(read_flag MaerskCircuit__Enabled)"
monitor="$(read_flag MaerskMonitoring__Enabled)"
queue="$(read_flag AgentQueue__ConcurrentDispatcherEnabled)"
[[ -z "$queue" || "$queue" == false || "$queue" == 0 ]] ||
  die 'Concurrent dispatcher must remain disabled during circuit rollout'
if [[ "$circuit" == true && "$monitor" == true ]]; then
  [[ "${MAERSK_PHASE2_ACTIVE_REDEPLOY:-}" == CONFIRMED ]] ||
    die 'Releasing with active circuit requires an explicit active-redeploy gate'
  DHOLE_ENV_FILE="$env_file" bash "$(dirname "$0")/maersk-phase8-schema-check.sh" "$environment" >/dev/null ||
    die 'Active redeploy requires the live migrated schema'
  echo 'PHASE2_ALREADY_ACTIVE_REDEPLOY_GATE_PASSED'
else
  for value in "$circuit" "$monitor"; do
    [[ -z "$value" || "$value" == false || "$value" == 0 ]] ||
      die 'Both circuit and monitor must be disabled before first activation'
  done
fi

echo "PHASE8_PREFLIGHT_OK: $environment; PostgreSQL and both persistent volumes backed up and archive-checked."
