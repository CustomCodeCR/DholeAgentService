#!/usr/bin/env bash
# Single-owner production hotfix for already ACTIVE prod1. Never starts prod.
set -Eeuo pipefail
set +x
sha="${1:?Approved staging SHA required}"
system="${2:?DholeSystem checkout required}"
runtime="${3:?Protected production env required}"
backup_root="${4:?Durable backup root required}"
[[ "$sha" =~ ^[0-9a-f]{40}$ && "$backup_root" == /* ]] || exit 2
[[ "${GITHUB_REF:-}" == refs/heads/master && "${GITHUB_EVENT_NAME:-}" == push ]] || exit 2
[[ -r "$runtime" && -f "$system/deploy/docker-compose.agent-prod1.yml" ]] || exit 2
export DHOLE_ENV_FILE="$runtime" DHOLE_NETWORK=dhole DHOLE_IMAGE_TAG=latest AGENT_GENERATION=prod1
primary=(docker compose --env-file "$runtime" -f "$system/deploy/docker-compose.services.yml" -p dhole)
active=(docker compose --env-file "$runtime" -f "$system/deploy/docker-compose.agent-prod1.yml" -p dhole-agent-prod1 --profile activated)
"${primary[@]}" config -q
"${active[@]}" config -q
fail(){ echo "PROD1_HOTFIX_REFUSED: $*" >&2; exit 1; }
id(){
  local ids
  ids="$(docker ps -aq --filter "label=com.docker.compose.project=$1" --filter "label=com.docker.compose.service=$2")"
  [[ "$(printf '%s\n' "$ids" | sed '/^$/d' | wc -l)" == 1 ]] || fail 'ambiguous container ownership'
  printf '%s' "$ids"
}
status(){ docker inspect -f '{{.State.Status}}' "$1"; }
digest(){ docker inspect -f '{{.Image}}' "$1"; }
mount(){ docker inspect -f "{{range .Mounts}}{{if eq .Destination \"$2\"}}{{.Name}}{{end}}{{end}}" "$1"; }
main_worker="$(id dhole dhole-agent-workers)"
main_api="$(id dhole dhole-agent-api)"
worker="$(id dhole-agent-prod1 dhole-agent-workers-prod1)"
api="$(id dhole-agent-prod1 dhole-agent-api-prod1)"
hermes="$(id dhole-agent-prod1 hermes-agent-prod1)"
[[ "$(status "$main_worker")" != running && "$(status "$worker")" == running ]] ||
  fail 'single active prod1 Worker is required'
[[ "$(status "$api")" == running && "$(status "$hermes")" == running ]] ||
  fail 'healthy existing prod1 infrastructure required'
python3 - <<'PY'
import importlib.util
from pathlib import Path
spec=importlib.util.spec_from_file_location("owner",Path("scripts/production-generation-executor.py"))
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
assert m.find_running_generation()[0]=="prod1"
PY
short="${sha:0:12}"
for n in agent-api agent-workers; do
  docker image inspect "dhole/$n:staging-$short" >/dev/null || fail "missing validated stage artifact $n"
done
[[ "$(df -Pk / | awk 'NR==2{print $4}')" -ge 2097152 ]] || fail 'root filesystem too full'
mkdir -p "$backup_root"; chmod 700 "$backup_root"
[[ "$(df -Pk "$backup_root" | awk 'NR==2{print $4}')" -ge 4194304 ]] ||
  fail 'not enough durable backup capacity'
old_worker="$(digest "$worker")"
old_api="$(digest "$api")"
old_main_api="$(digest "$main_api")"
old_browser="$(mount "$worker" /data/browser-profiles)"
old_keys="$(mount "$worker" /data/agent-keys)"
old_api_keys="$(mount "$api" /data/agent-keys)"
[[ -n "$old_browser" && -n "$old_keys" && "$old_keys" == "$old_api_keys" ]] ||
  fail 'persistent mounts mismatch'
[[ "$old_browser" != "$(mount "$main_worker" /data/browser-profiles)" ]] ||
  fail 'browser profile isolation failed'
suffix="${GITHUB_RUN_ID:-hotfix}"
docker image tag "$old_worker" "dhole/agent-workers:rollback-prod1-$suffix"
docker image tag "$old_api" "dhole/agent-api:rollback-prod1-$suffix"
docker image tag "$old_main_api" "dhole/agent-api:rollback-prod-$suffix"
stopped=0
changed=0
completed=0
rollback(){
  rc="$?"
  trap - EXIT
  if [[ "$completed" == 1 ]]; then exit "$rc"; fi
  if [[ "$stopped" == 1 ]]; then
    echo 'PROD1_HOTFIX_ROLLBACK_ATTEMPTED: preserving profiles and generation' >&2
    set +e
    if [[ "$changed" == 1 ]]; then
      docker image tag "dhole/agent-workers:rollback-prod1-$suffix" dhole/agent-workers:latest
      docker image tag "dhole/agent-api:rollback-prod1-$suffix" dhole/agent-api:latest
      "${active[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-api-prod1
      "${active[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-workers-prod1
      docker image tag "dhole/agent-api:rollback-prod-$suffix" dhole/agent-api:latest
      "${primary[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-api
    else
      docker start "$worker"
    fi
    set -e
  fi
  echo 'PROD1_HOTFIX_FAILED: confirm rollback and worker ownership manually' >&2
  exit "$rc"
}
trap rollback EXIT
running(){
  python3 - "$runtime" <<'PY'
import importlib.util,sys
from pathlib import Path
spec=importlib.util.spec_from_file_location("apply",Path("scripts/production-generation-apply-request.py"))
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
print(m.sql(m.parse_db(sys.argv[1]),'''SELECT count(*) FROM agent."AgentExecutions" WHERE status='Running';''',stage="prod1_hotfix_drain"))
PY
}
deadline=$(( $(date -u +%s) + 570 ))
while :; do
  count="$(running)"
  [[ "$count" =~ ^[0-9]+$ ]] || fail 'cannot verify active execution count'
  [[ "$count" == 0 ]] && break
  echo "PROD1_HOTFIX_DRAIN: $count running executions; not interrupting jobs"
  (( $(date -u +%s) < deadline )) || fail 'active jobs have not drained'
  sleep 15
done
docker stop --time 90 "$worker" >/dev/null || fail 'failed to stop active Worker gracefully'
stopped=1
[[ "$(running)" == 0 && "$(status "$main_worker")" != running ]] ||
  fail 'jobs active or duplicate Worker started during drain'
output="$(mktemp)"
chmod 600 "$output"
GITHUB_OUTPUT="$output" bash scripts/maersk-phase8-backup.sh prod1 "$backup_root"
backup_dir="$(sed -n 's/^backup_dir=//p' "$output" | tail -n1)"
rm -f "$output"
[[ "$backup_dir" == /* && -d "$backup_dir" ]] || fail 'prod1 database/profile backup missing'
(cd "$backup_dir" && sha256sum -c --status SHA256SUMS) || fail 'backup integrity mismatch'
docker run --rm -v "$backup_dir:/backup:ro" postgres:17-alpine \
  pg_restore -l /backup/agent.dump >/dev/null || fail 'PostgreSQL backup is invalid'
tar -tzf "$backup_dir/browser-profiles.tar.gz" >/dev/null || fail 'browser archive invalid'
tar -tzf "$backup_dir/agent-keys.tar.gz" >/dev/null || fail 'keyring archive invalid'
echo 'PROD1_HOTFIX_PROTECTED_BACKUPS_VERIFIED'
changed=1
docker image tag "dhole/agent-api:staging-$short" dhole/agent-api:latest
docker image tag "dhole/agent-workers:staging-$short" dhole/agent-workers:latest
"${primary[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-api
"${active[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-api-prod1
"${active[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-workers-prod1
new_worker="$(id dhole-agent-prod1 dhole-agent-workers-prod1)"
new_api="$(id dhole-agent-prod1 dhole-agent-api-prod1)"
new_primary_api="$(id dhole dhole-agent-api)"
[[ "$(status "$new_worker")" == running && "$(status "$main_worker")" != running ]] ||
  fail 'single-owner Worker check failed'
[[ "$(digest "$new_worker")" == "$(docker image inspect -f '{{.Id}}' dhole/agent-workers:latest)" ]] ||
  fail 'active Worker image revision differs from release'
[[ "$(digest "$new_api")" == "$(docker image inspect -f '{{.Id}}' dhole/agent-api:latest)" &&
   "$(digest "$new_primary_api")" == "$(docker image inspect -f '{{.Id}}' dhole/agent-api:latest)" ]] ||
  fail 'Agent API image revisions differ from release'
[[ "$(mount "$new_worker" /data/browser-profiles)" == "$old_browser" &&
   "$(mount "$new_worker" /data/agent-keys)" == "$old_keys" &&
   "$(mount "$new_api" /data/agent-keys)" == "$old_api_keys" &&
   "$(id dhole-agent-prod1 hermes-agent-prod1)" == "$hermes" ]] ||
  fail 'persistent mounts or Hermes container were changed'
for host in dhole-agent-api dhole-agent-api-prod1; do
  ok=0
  for i in $(seq 1 30); do
    if docker run --rm --pull never --network dhole curlimages/curl:8.12.1 \
      -fsS --max-time 4 "http://$host:8080/health" >/dev/null 2>&1; then ok=1;break;fi
    sleep 2
  done
  [[ "$ok" == 1 ]] || fail "API health failed: $host"
done
pump=0
for i in $(seq 1 35); do
  if docker logs --since 4m "$new_worker" 2>&1 |
     grep -Eq 'AGENT_QUEUE_(PUMP|CONCURRENT)_STARTED'; then pump=1;break;fi
  sleep 2
done
[[ "$pump" == 1 ]] || fail 'prod1 queue pump did not start'
python3 - <<'PY'
import importlib.util
from pathlib import Path
spec=importlib.util.spec_from_file_location("owner",Path("scripts/production-generation-executor.py"))
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
assert m.find_running_generation()[0]=="prod1"
PY
completed=1
echo "PROD1_NPGSQL_HOTFIX_DEPLOYED staging_sha=$sha active_worker=prod1"
echo "PROD1_HOTFIX_BACKUP_LOCATION=$backup_dir"
echo 'PROD1_HOTFIX_SINGLE_OWNER_AND_VOLUMES_VERIFIED'
