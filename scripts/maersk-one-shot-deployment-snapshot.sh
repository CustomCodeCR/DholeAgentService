#!/usr/bin/env bash
# Used only by the explicitly requested, one-shot guarded deployment workflow.
# Stops an idle Agent Worker, captures consistent DB/profile/key snapshots, and
# checks the backup via restore-drill before allowing deployment dispatch.
set -Eeuo pipefail
set +x
target="${1:-}"
infra_dir="${2:-}"
backup_root="${3:-}"
die() { echo "MAERSK_DEPLOY_SNAPSHOT_BLOCKED: $*" >&2; exit 1; }
case "$target" in
  staging) project=dhole-staging; network=dhole-staging; postfix=staging ;;
  production) project=dhole; network=dhole; postfix=production ;;
  *) die "Usage: <staging|production> <DholeSystem checkout> <durable backup root>" ;;
esac
[[ -r "$infra_dir/deploy/prepare-environments.sh" ]] || die 'DholeSystem checkout missing'
[[ "$backup_root" = /* && "$backup_root" != /tmp/* && "$backup_root" != /var/tmp/* ]] ||
  die 'A durable absolute backup path is required'
command -v docker >/dev/null || die 'docker unavailable'
command -v flock >/dev/null || die 'flock unavailable'
[[ -n "${GITHUB_OUTPUT:-}" ]] || die 'GitHub Actions output path missing'
mkdir -p "$backup_root"; chmod 700 "$backup_root"
free="$(df -Pk "$backup_root" | awk 'NR==2 {print $4}')"
[[ "$free" =~ ^[0-9]+$ ]] || die 'Cannot inspect backup disk space'
((free >= 4 * 1024 * 1024)) || die 'Less than 4 GiB free on backup filesystem'
work="$(mktemp -d)"
chmod 700 "$work"
stopped=0
worker_id=""
cleanup() {
  rc="$?"
  if ((rc != 0 && stopped == 1)) && [[ -n "$worker_id" ]]; then
    echo 'Snapshot failed; attempting to resume original Agent Worker' >&2
    docker start "$worker_id" >/dev/null ||
      echo 'WARNING: Agent Worker requires manual restart' >&2
  fi
  rm -rf "$work"
  exit "$rc"
}
trap cleanup EXIT
bash "$infra_dir/deploy/prepare-environments.sh" /opt/dhole/.env "$work" >/dev/null
export DHOLE_ENV_FILE="$work/.env.$postfix"
[[ -r "$DHOLE_ENV_FILE" ]] || die 'Prepared Agent configuration missing'
worker_id="$(docker ps -aq --filter "label=com.docker.compose.project=$project" \
  --filter label=com.docker.compose.service=dhole-agent-workers)"
[[ "$(printf '%s\n' "$worker_id" | sed '/^$/d' | wc -l)" -eq 1 ]] ||
  die 'Expected precisely one Agent Worker; refuse ambiguous maintenance'
connection="$(sed -n 's/^AGENT_POSTGRES_CONNECTION_STRING=//p' "$DHOLE_ENV_FILE" | head -n1 | tr -d '\r')"
[[ -n "$connection" ]] || die 'Missing Agent connection'
value() { printf '%s' "$connection" | tr ';' '\n' | sed -n "s/^$1=//p" | head -n1; }
host="$(value Host)"; host="${host:-postgres}"
port="$(value Port)"; port="${port:-5432}"
database="$(value Database)"
username="$(value Username)"
password="$(value Password)"
[[ "$database" =~ ^[A-Za-z0-9_]+$ && "$port" =~ ^[0-9]{1,5}$ &&
   -n "$username" && -n "$password" ]] || die 'Invalid Agent database'
running="$(docker run --rm --network "$network" \
  -e PGPASSWORD="$password" -e PGOPTIONS='-c default_transaction_read_only=on' \
  postgres:17-alpine psql -X -v ON_ERROR_STOP=1 -At \
  -h "$host" -p "$port" -U "$username" -d "$database" \
  -c 'SELECT COUNT(*) FROM agent."AgentExecutions" WHERE status='\''Running'\'';')" ||
    die 'Cannot inspect active jobs'
[[ "$running" == 0 ]] || die "$running Agent executions are Running; will not interrupt"

# Do not terminate live jobs. Stop the selected environment's sole idle Worker.
docker stop --time 60 "$worker_id" >/dev/null || die 'Could not stop idle Worker'
stopped=1
# Prevent an in-flight transition race: the original backup script checks SQL
# again and refuses any Running execution before reading Chromium volumes.
output_file="$work/backup-output"
GITHUB_OUTPUT="$output_file" bash scripts/maersk-phase8-backup.sh "$target" "$backup_root"
backup_dir="$(sed -n 's/^backup_dir=//p' "$output_file" | tail -n1)"
[[ "$backup_dir" = /* && -d "$backup_dir" ]] || die 'Backup snapshot location was not produced'
MAERSK_PHASE2_ACTIVE_REDEPLOY=CONFIRMED \
  bash scripts/maersk-phase8-preflight.sh "$target" "$backup_dir" "$DHOLE_ENV_FILE" ||
    die 'Snapshot preflight failed'
bash scripts/maersk-phase8-restore-drill.sh "$backup_dir" ||
  die 'Isolated PostgreSQL restoration failed'
printf 'backup_dir=%s\n' "$backup_dir" >> "$GITHUB_OUTPUT"
echo "GUARDED_AGENT_SNAPSHOT_READY environment=$target checksum=PASS restore=PASS"
echo "The stopped Agent Worker will be replaced by the protected deployment workflow."
