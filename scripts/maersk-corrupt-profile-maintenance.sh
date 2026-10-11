#!/usr/bin/env bash
# Explicit maintenance of technically corrupt local Chromium data.
# This must never run in response to provider CAPTCHA / 401 / 403 / 429.
set -Eeuo pipefail
set +x
[ "$#" -ge 5 ] || { echo "Usage: <staging|production> <--check|--apply> <credential-uuid> <verified-backup-dir> <env-file> [confirmation]" >&2; exit 2; }
environment="$1"; mode="$2"; credential_id="$3"; backup_dir="$4"; env_file="$5"; confirmation="${6:-}"
die() { echo "CORRUPT_PROFILE_PURGE_BLOCKED: $*" >&2; exit 1; }
case "$environment" in
  production) project=dhole; network=dhole; expected=PURGE_CORRUPT_MAERSK_PROFILE_PRODUCTION ;;
  staging) project=dhole-staging; network=dhole-staging; expected=PURGE_CORRUPT_MAERSK_PROFILE_STAGING ;;
  *) die "Unsupported environment" ;;
esac
[[ "$mode" == --check || "$mode" == --apply ]] || die 'Use --check or --apply'
[[ "$credential_id" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] ||
  die 'Credential UUID required; arbitrary paths are forbidden'
credential_id="$(printf '%s' "$credential_id" | tr '[:upper:]' '[:lower:]')"
flat_id="$(printf '%s' "$credential_id" | tr -d '-')"
[[ "$mode" != --apply || "$confirmation" == "$expected" ]] || die 'Explicit environment-specific confirmation required'
[[ "$backup_dir" == /* && "$backup_dir" != /tmp/* && -r "$env_file" ]] ||
  die 'Durable backup and environment configuration required'
for artifact in metadata.txt SHA256SUMS agent.dump browser-profiles.tar.gz agent-keys.tar.gz; do
  [[ -s "$backup_dir/$artifact" ]] || die "Missing backup: $artifact"
done
grep -Fxq "environment=$environment" "$backup_dir/metadata.txt" || die 'Backup environment mismatch'
grep -Fxq "compose_project=$project" "$backup_dir/metadata.txt" || die 'Backup project mismatch'
grep -Fxq "profile_volume=$project"'_agent-browser-profiles' "$backup_dir/metadata.txt" ||
  die 'Backup profile volume mismatch'
created="$(sed -n 's/^created_epoch=//p' "$backup_dir/metadata.txt" | head -n1)"
now="$(date -u +%s)"
[[ "$created" =~ ^[0-9]+$ ]] || die 'Backup creation timestamp is invalid'
(( created <= now && now-created <= 86400 )) || die 'Backup must be less than 24 hours old'
(cd "$backup_dir" && sha256sum -c --status SHA256SUMS) || die 'Invalid backup checksums'
tar -tzf "$backup_dir/browser-profiles.tar.gz" >/dev/null || die 'Invalid profile backup'
tar -tzf "$backup_dir/agent-keys.tar.gz" >/dev/null || die 'Invalid key backup'
command -v docker >/dev/null || die 'Docker required'
command -v flock >/dev/null || die 'flock required'
state_dir="$HOME/.local/state/dhole-maersk-runtime-recovery"
mkdir -p "$state_dir"; chmod 700 "$state_dir"
exec 9>"$state_dir/$environment.lock"
flock -n 9 || die 'Another recovery/maintenance holds the environment lock'
worker="$(docker ps -q --filter "label=com.docker.compose.project=$project" \
  --filter label=com.docker.compose.service=dhole-agent-workers)"
[[ -z "$worker" ]] || die 'Worker must be stopped after all jobs have drained'

conn="$(sed -n 's/^AGENT_POSTGRES_CONNECTION_STRING=//p' "$env_file" | head -n1 | tr -d '\r')"
[[ -n "$conn" ]] || die 'Missing PostgreSQL connection'
value() { printf '%s' "$conn" | tr ';' '\n' | sed -n "s/^$1=//p" | head -n1; }
db_host="$(value Host)"; db_host="$(printf '%s' "$db_host" | sed 's/^$/postgres/')"
db_port="$(value Port)"; db_port="$(printf '%s' "$db_port" | sed 's/^$/5432/')"
db_name="$(value Database)"; db_user="$(value Username)"; db_pass="$(value Password)"
[[ "$db_name" =~ ^[A-Za-z0-9_]+$ && "$db_port" =~ ^[0-9]{1,5}$ &&
   -n "$db_user" && -n "$db_pass" ]] || die 'Invalid database configuration'
grep -Fxq "database_name=$db_name" "$backup_dir/metadata.txt" || die 'Backup database mismatch'
# Only if a recent structured error proves LOCAL profile corruption.
# Provider circuit restrictions or recent CAPTCHA blocks all profile purges.
sql="$(cat <<SQL
SELECT CASE WHEN
  (SELECT COUNT(*) FROM agent."AgentExecutions" WHERE status='Running')=0
  AND EXISTS (
    SELECT 1 FROM agent."AgentExecutions" e
    JOIN agent."AgentProviders" p ON p.id=e.provider_id
    WHERE e.credential_id='$credential_id'::uuid
      AND UPPER(p.code)='MAERSK'
      AND e.error_code='maersk_browser_profile_corrupt'
      AND e.created_at_utc >= NOW()-INTERVAL '24 hours'
  )
  AND NOT EXISTS (
    SELECT 1 FROM agent.maersk_circuits c
    JOIN agent."AgentProviders" p ON p.id=c.provider_id
    WHERE UPPER(p.code)='MAERSK' AND (c.requires_operator OR c.state<>'Closed')
  )
  AND NOT EXISTS (
    SELECT 1 FROM agent."AgentExecutions" e
    JOIN agent."AgentProviders" p ON p.id=e.provider_id
    WHERE UPPER(p.code)='MAERSK' AND e.created_at_utc>=NOW()-INTERVAL '24 hours'
      AND e.error_code IN (
        'maersk_hcaptcha_required','maersk_authentication_edge_denied',
        'maersk_authentication_forbidden','maersk_authentication_rate_limited',
        'maersk_authentication_verification_required','maersk_browser_profile_blocked')
  )
THEN 'CORRUPTION_ONLY' ELSE 'RESTRICTED' END;
SQL
)"
result="$(docker run --rm --network "$network" -e PGPASSWORD="$db_pass" \
  -e PGOPTIONS='-c default_transaction_read_only=on' postgres:16-alpine \
  psql -X -v ON_ERROR_STOP=1 -At -h "$db_host" -p "$db_port" -U "$db_user" -d "$db_name" -c "$sql")" ||
  die 'Cannot verify technical profile corruption'
[[ "$result" == CORRUPTION_ONLY ]] ||
  die 'No recent technical corruption evidence or a provider restriction is active'
volume="$project"'_agent-browser-profiles'
docker volume inspect "$volume" >/dev/null || die 'Profile volume not found'
if [[ "$mode" == --check ]]; then
  echo "MAERSK_CORRUPT_PROFILE_PURGE_ELIGIBLE: $environment $credential_id"
  exit 0
fi
# Mutates only this credential's Chromium directory. Verified backups remain off-volume.
docker run --rm --network none --mount "type=volume,source=$volume,target=/profiles" \
  -e PROFILE_ID="$flat_id" --entrypoint /bin/sh alpine:3.20 -eu -c '
  target="/profiles/MAERSK/$PROFILE_ID"
  [ ! -L /profiles/MAERSK ] && [ ! -L "$target" ] || exit 3
  if [ -d "$target" ]; then rm -rf -- "$target"; fi
  [ ! -e "$target" ] || exit 4
' || die 'Could not remove the exact profile directory'
echo "MAERSK_CORRUPT_PROFILE_PURGED: $environment $credential_id"
echo 'A fresh authorized login is required before future Maersk jobs.'
