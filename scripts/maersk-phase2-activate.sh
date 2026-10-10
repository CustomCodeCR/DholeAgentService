#!/usr/bin/env bash
# Phase 2: operator-authorized post-deployment feature activation.
# Never runs on CAPTCHA and never changes the database or a browser profile.
set -Eeuo pipefail
set +x
die() { echo "PHASE2_ACTIVATION_BLOCKED: $*" >&2; exit 1; }
[ "$#" -eq 5 ] || die 'Usage: script <staging|production> <backup-dir> <40-digit SHA> <DholeSystem checkout> <confirmation>'
env_name="$1"
backup_dir="$2"
expected_sha="$3"
system_dir="$4"
confirmed="$5"
case "$env_name" in
  staging) project=dhole-staging; network=dhole-staging; tag=staging; source_env=/opt/dhole/.env.staging; required=ACTIVATE_MAERSK_CIRCUIT_STAGING ;;
  production) project=dhole; network=dhole; tag=latest; source_env=/opt/dhole/.env; required=ACTIVATE_MAERSK_CIRCUIT_PRODUCTION ;;
  *) die 'Unrecognized environment' ;;
esac
[ "$confirmed" = "$required" ] || die 'Confirmation did not match the selected environment'
[[ "$expected_sha" =~ ^[a-f0-9]{40}$ ]] || die 'Expected SHA must be 40 lowercase hex digits'
[[ "$backup_dir" = /* && -d "$backup_dir" ]] || die 'Absolute verified backup path required'
[ -r "$source_env" ] || die 'Source env missing'
[ -r "$system_dir/deploy/prepare-environments.sh" ] || die 'DholeSystem checkout missing'
[ -r "$system_dir/deploy/docker-compose.services.yml" ] || die 'DholeSystem Compose missing'
work="$(mktemp -d)"
chmod 700 "$work"
wrote=0
activated=0
backup_basename=""
cleanup() {
  rc="$?"
  trap - EXIT
  if [ "$rc" -ne 0 ] && [ "$wrote" -eq 1 ] && [ "$activated" -eq 0 ]; then
    echo 'Activation failed; stopping only the Agent worker (fail-closed).' >&2
    docker ps -q --filter "label=com.docker.compose.project=$project" --filter label=com.docker.compose.service=dhole-agent-workers | xargs -r docker stop >/dev/null || true
    docker run --rm --network none --user 0:0 \
      --mount type=bind,src=/opt/dhole,dst=/dhole \
      -e BACKUP_NAME="$backup_basename" -e ENVIRONMENT="$env_name" \
      --entrypoint /bin/sh postgres:16-alpine -ec '
        if [ "$ENVIRONMENT" = production ]; then target=/dhole/.env; else target=/dhole/.env.staging; fi
        [ -f "/dhole/$BACKUP_NAME" ] && cp -p "/dhole/$BACKUP_NAME" "$target"
      ' || echo 'WARNING: source environment rollback needs manual inspection' >&2
    echo 'Worker remains stopped to avoid unprotected requests. No database rows changed.' >&2
  fi
  rm -rf "$work"
  exit "$rc"
}
trap cleanup EXIT
export DHOLE_ENV_FILE="$work/.env.$env_name"
bash "$system_dir/deploy/prepare-environments.sh" /opt/dhole/.env "$work" >/dev/null
# Backup and restore-drill gates of phase 8 remain mandatory.
bash "$(dirname "$0")/maersk-phase8-preflight.sh" "$env_name" "$backup_dir" "$DHOLE_ENV_FILE" >/dev/null
bash "$(dirname "$0")/maersk-phase8-restore-drill.sh" "$backup_dir" >/dev/null
bash "$(dirname "$0")/maersk-phase8-schema-check.sh" "$env_name" >/dev/null
short="$(printf '%s' "$expected_sha" | cut -c 1-12)"
for service in dhole-agent-api dhole-agent-workers; do
  base=api
  [ "$service" = dhole-agent-workers ] && base=workers
  expected_image="$(docker image inspect --format '{{.Id}}' "dhole/agent-$base:$tag-$short" 2>/dev/null)" ||
    die 'Expected tagged release image unavailable; complete guarded deployment first'
  cid="$(docker ps -q --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=$service")"
  [ "$(printf '%s\n' "$cid" | sed '/^$/d' | wc -l)" -eq 1 ] || die "Missing/multiple $service containers"
  actual_image="$(docker inspect --format '{{.Image}}' "$cid")"
  [ "$actual_image" = "$expected_image" ] || die "$service is not on the expected guarded deploy SHA"
done
conn="$(grep -m1 '^AGENT_POSTGRES_CONNECTION_STRING=' "$DHOLE_ENV_FILE" | cut -d= -f2- | tr -d '\r')"
value() { printf '%s' "$conn" | tr ';' '\n' | sed -n "s/^$1=//p" | head -n1; }
dbhost="$(value Host)"; dbport="$(value Port)"
dbname="$(value Database)"; dbuser="$(value Username)"; dbpass="$(value Password)"
[[ "$dbname" =~ ^[A-Za-z0-9_]+$ && -n "$dbuser" && -n "$dbpass" ]] ||
  die 'Invalid Agent database settings'
running="$(docker run --rm --network "$network" -e PGPASSWORD="$dbpass" \
  -e PGOPTIONS='-c default_transaction_read_only=on' postgres:16-alpine \
  psql -X -v ON_ERROR_STOP=1 -At -h "$dbhost" -p "$dbport" -U "$dbuser" -d "$dbname" \
  -c "SELECT COUNT(*) FROM agent.\"AgentExecutions\" WHERE status='Running';")" ||
  die 'Cannot inspect running executions'
[ "$running" = 0 ] || die 'Active executions must finish before deployment'
# Refuse activation against a schema missing phase-2 idempotency protections.
phase2_schema="$(docker run --rm --network "$network" -e PGPASSWORD="$dbpass" \
  -e PGOPTIONS='-c default_transaction_read_only=on' postgres:16-alpine \
  psql -X -v ON_ERROR_STOP=1 -At -h "$dbhost" -p "$dbport" -U "$dbuser" -d "$dbname" \
  -c "SELECT CASE WHEN EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema='agent' AND table_name='maersk_circuit_events'
      AND column_name='execution_id'
  ) AND EXISTS (
    SELECT 1 FROM pg_indexes
    WHERE schemaname='agent' AND indexname='ix_maersk_circuit_events_failure_reason'
  ) THEN 1 ELSE 0 END;")" || die 'Cannot verify phase-2 migration'
[ "$phase2_schema" = 1 ] || die 'Phase-2 idempotency migration not applied to this database'
# Refuse first activation when old provider challenges are still pending.
# Phase 3 reconciliation must bring these incidents into a durable Open circuit
# BEFORE workers are allowed to schedule Maersk again.
pending="$(docker run --rm --network "$network" -e PGPASSWORD="$dbpass" \
  -e PGOPTIONS='-c default_transaction_read_only=on' postgres:16-alpine \
  psql -X -v ON_ERROR_STOP=1 -At -h "$dbhost" -p "$dbport" -U "$dbuser" -d "$dbname" \
  -c "SELECT count(*) FROM agent.\"AgentExecutions\" WHERE status='WaitingForAuthentication' AND error_code IN ('maersk_hcaptcha_required','maersk_authentication_forbidden','maersk_authentication_rate_limited');")" ||
  die 'Cannot verify existing CAPTCHA backlog'
[ "$pending" = 0 ] ||
  die "There are $pending earlier provider-verification incidents. Complete safe reconciliation before enabling any new Maersk work"

compose="$system_dir/deploy/docker-compose.services.yml"
compose_apply() {
  if [ "$env_name" = staging ]; then
    DHOLE_NETWORK="$network" DHOLE_IMAGE_TAG="$tag" docker compose --env-file "$DHOLE_ENV_FILE" \
      -f "$compose" -f "$system_dir/deploy/docker-compose.staging.yml" -p "$project" "$@"
  else
    DHOLE_NETWORK="$network" DHOLE_IMAGE_TAG="$tag" docker compose --env-file "$DHOLE_ENV_FILE" \
      -f "$compose" -p "$project" "$@"
  fi
}
compose_apply config -q || die 'Compose configuration invalid'

# Move the permanent source file atomically, preserving a private rollback copy.
backup_basename=".maersk-phase2-source-$env_name-$(date -u +%Y%m%dT%H%M%SZ)"
docker run --rm --network none --user 0:0 \
  --mount type=bind,src=/opt/dhole,dst=/dhole \
  -e BACKUP_NAME="$backup_basename" -e ENVIRONMENT="$env_name" \
  --entrypoint /bin/sh postgres:16-alpine -ec '
    set -eu
    if [ "$ENVIRONMENT" = production ]; then env=/dhole/.env; else env=/dhole/.env.staging; fi
    [ -f "$env" ] && [ ! -L "$env" ] || exit 1
    backup="/dhole/$BACKUP_NAME"
    [ ! -e "$backup" ] || exit 1
    cp -p "$env" "$backup"
    tmp="$(mktemp /dhole/.maersk-phase2.XXXXXX)"
    grep -Ev "^(MaerskCircuit__Enabled|MaerskMonitoring__Enabled)=" "$env" > "$tmp" || true
    printf "MaerskCircuit__Enabled=true\nMaerskMonitoring__Enabled=true\n" >> "$tmp"
    chmod --reference="$env" "$tmp"
    chown --reference="$env" "$tmp"
    mv "$tmp" "$env"
    echo "PRIVATE_ACTIVATION_BACKUP_CREATED=$BACKUP_NAME"
  '
wrote=1
bash "$system_dir/deploy/prepare-environments.sh" /opt/dhole/.env "$work" >/dev/null
grep -Fxq MaerskCircuit__Enabled=true "$DHOLE_ENV_FILE" || die 'Flag not present in prepared env'
grep -Fxq MaerskMonitoring__Enabled=true "$DHOLE_ENV_FILE" || die 'Monitoring flag missing in prepared env'
# Only affected API/Workers are recreated; volumes and PostgreSQL untouched.
compose_apply up -d --no-deps --force-recreate --pull never dhole-agent-api dhole-agent-workers ||
  die 'Compose activation failure'
for service in dhole-agent-api dhole-agent-workers; do
  cid="$(docker ps -q --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=$service")"
  [ "$(printf '%s\n' "$cid" | sed '/^$/d' | wc -l)" -eq 1 ] || die "Missing $service after activation"
  [ "$(docker inspect --format '{{.State.Status}}' "$cid")" = running ] ||
    die "$service is not running after activation"
  for key in MaerskCircuit__Enabled MaerskMonitoring__Enabled; do
    found="$(docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$cid" | grep -Fxc "$key=true" || true)"
    [ "$found" = 1 ] || die "Effective flag $key differs in $service"
  done
done
activated=1
echo "PHASE2_ACTIVATED=$env_name flags_verified=true; durable_database_untouched=true"
echo 'No circuit reset, queue replay, session rotation or CAPTCHA request has been executed.'
