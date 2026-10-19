#!/usr/bin/env bash
# Single-owner production hotfix for already ACTIVE prod2. Never starts prod.
set -Eeuo pipefail
set +x
sha="${1:?Approved master SHA required}"
system="${2:?DholeSystem checkout required}"
runtime="${3:?Protected production env required}"
backup_root="${4:?Durable backup root required}"
[[ "$sha" =~ ^[0-9a-f]{40}$ && "$backup_root" == /* ]] || exit 2
# Never deploy an unrelated SHA, even if a previous Docker image carries its tag.
[[ "${GITHUB_SHA:-}" == "$sha" ]] || { echo 'PROD2_HOTFIX_REFUSED: checkout SHA differs from approved master commit' >&2; exit 2; }
[[ "${GITHUB_REF:-}" == refs/heads/master && ( "${GITHUB_EVENT_NAME:-}" == push || "${GITHUB_EVENT_NAME:-}" == workflow_dispatch ) ]] || exit 2
[[ -r "$runtime" && -f "$system/deploy/docker-compose.agent-generation.yml" ]] || exit 2
# Prepared deploy config must carry the exact effective JWT options of the
# running primary API; the host's /opt/dhole/.env does not declare those keys.
# Update only the protected temporary env file, before any container mutation.
python3 scripts/prepare-prod2-jwt-env.py "$runtime"
export DHOLE_ENV_FILE="$runtime" DHOLE_NETWORK=dhole DHOLE_IMAGE_TAG=latest AGENT_GENERATION=prod2
primary=(docker compose --env-file "$runtime" -f "$system/deploy/docker-compose.services.yml" -p dhole)
active=(docker compose --env-file "$runtime" -f "$system/deploy/docker-compose.agent-generation.yml" -p dhole-agent-prod2 --profile activated)
"${primary[@]}" config -q
"${active[@]}" config -q
fail(){ echo "PROD2_HOTFIX_REFUSED: $*" >&2; exit 1; }
id(){
  local ids
  ids="$(docker ps -aq --filter "label=com.docker.compose.project=$1" --filter "label=com.docker.compose.service=$2")"
  [[ "$(printf '%s\n' "$ids" | sed '/^$/d' | wc -l)" == 1 ]] || fail 'ambiguous container ownership'
  printf '%s' "$ids"
}
status(){ docker inspect -f '{{.State.Status}}' "$1"; }
digest(){ docker inspect -f '{{.Image}}' "$1"; }
mount(){ docker inspect -f "{{range .Mounts}}{{if eq .Destination \"$2\"}}{{.Name}}{{end}}{{end}}" "$1"; }
# Legacy prod Worker container may have been removed. Check for a running
# duplicate, not the existence of an obsolete stopped container.
main_worker="$(docker ps -aq --filter 'label=com.docker.compose.project=dhole' --filter 'label=com.docker.compose.service=dhole-agent-workers')"
[[ "$(printf '%s\n' "$main_worker" | sed '/^$/d' | wc -l)" -le 1 ]] ||
  fail 'ambiguous legacy worker ownership'
legacy_running(){ [[ -n "$main_worker" ]] && [[ "$(status "$main_worker")" == running ]]; }
main_api="$(id dhole dhole-agent-api)"
worker="$(id dhole-agent-prod2 dhole-agent-workers)"
api="$(id dhole-agent-prod2 dhole-agent-api)"
hermes="$(id dhole-agent-prod2 hermes-agent)"
[[ "$(status "$worker")" == running ]] && ! legacy_running ||
  fail 'single active prod2 Worker is required'
[[ "$(status "$api")" == running && "$(status "$hermes")" == running ]] ||
  fail 'healthy existing prod2 infrastructure required'
python3 - <<'PY'
import importlib.util
from pathlib import Path
spec=importlib.util.spec_from_file_location("owner",Path("scripts/production-generation-executor.py"))
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
assert m.find_running_generation()[0]=="prod2"
PY
short="${sha:0:12}"
for n in agent-api agent-workers; do
  docker image inspect "dhole/$n:master-$short" >/dev/null || fail "missing approved master artifact $n"
done
# The health probe is itself a Docker image. Containerd GC can remove it
# between releases: validate and run it BEFORE stopping prod2 or backing up.
probe_image=curlimages/curl:8.12.1
if ! docker image inspect "$probe_image" >/dev/null 2>&1; then
  docker pull "$probe_image" >/dev/null || fail 'health probe image cannot be downloaded'
fi
docker image inspect "$probe_image" >/dev/null || fail 'health probe image unavailable'
docker run --rm --pull never --network dhole "$probe_image" \
  -fsS --max-time 8 http://dhole-dhole-agent-api-1:8080/health >/dev/null ||
  fail 'primary API baseline health failed before deployment'
docker run --rm --pull never --network dhole "$probe_image" \
  -fsS --max-time 8 http://dhole-dhole-agent-api-1:8080/health >/dev/null ||
  fail 'prod2 API baseline health failed before deployment'
echo 'PROD2_HOTFIX_HEALTH_PROBE_BASELINE_VERIFIED'
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
[[ -z "$main_worker" || "$old_browser" != "$(mount "$main_worker" /data/browser-profiles)" ]] ||
  fail 'browser profile isolation failed'
suffix="${GITHUB_RUN_ID:-hotfix}"
docker image tag "$old_worker" "dhole/agent-workers:rollback-prod2-$suffix"
docker image tag "$old_api" "dhole/agent-api:rollback-prod2-$suffix"
docker image tag "$old_main_api" "dhole/agent-api:rollback-prod-$suffix"
stopped=0
changed=0
completed=0
rollback(){
  rc="$?"
  trap - EXIT
  if [[ "$completed" == 1 ]]; then exit "$rc"; fi
  if [[ "$stopped" == 1 ]]; then
    echo 'PROD2_HOTFIX_ROLLBACK_ATTEMPTED: preserving profiles and generation' >&2
    set +e
    if [[ "$changed" == 1 ]]; then
      docker image tag "dhole/agent-workers:rollback-prod2-$suffix" dhole/agent-workers:latest
      docker image tag "dhole/agent-api:rollback-prod2-$suffix" dhole/agent-api:latest
      "${active[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-api
      "${active[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-workers
      docker image tag "dhole/agent-api:rollback-prod-$suffix" dhole/agent-api:latest
      "${primary[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-api
    else
      docker start "$worker"
    fi
    set -e
  fi
  echo 'PROD2_HOTFIX_FAILED: confirm rollback and worker ownership manually' >&2
  exit "$rc"
}
trap rollback EXIT
running(){
  python3 - "$runtime" <<'PY'
import importlib.util,sys
from pathlib import Path
spec=importlib.util.spec_from_file_location("apply",Path("scripts/production-generation-apply-request.py"))
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
print(m.sql(m.parse_db(sys.argv[1]),'''SELECT count(*) FROM agent."AgentExecutions" WHERE status='Running';''',stage="prod2_hotfix_drain"))
PY
}
deadline=$(( $(date -u +%s) + 570 ))
while :; do
  count="$(running)"
  [[ "$count" =~ ^[0-9]+$ ]] || fail 'cannot verify active execution count'
  [[ "$count" == 0 ]] && break
  echo "PROD2_HOTFIX_DRAIN: $count running executions; not interrupting jobs"
  (( $(date -u +%s) < deadline )) || fail 'active jobs have not drained'
  sleep 15
done
docker stop --time 90 "$worker" >/dev/null || fail 'failed to stop active Worker gracefully'
stopped=1
[[ "$(running)" == 0 ]] && ! legacy_running ||
  fail 'jobs active or duplicate Worker started during drain'
output="$(mktemp)"
chmod 600 "$output"
GITHUB_OUTPUT="$output" bash scripts/maersk-phase8-backup.sh prod2 "$backup_root"
backup_dir="$(sed -n 's/^backup_dir=//p' "$output" | tail -n1)"
rm -f "$output"
[[ "$backup_dir" == /* && -d "$backup_dir" ]] || fail 'prod2 database/profile backup missing'
(cd "$backup_dir" && sha256sum -c --status SHA256SUMS) || fail 'backup integrity mismatch'
docker run --rm -v "$backup_dir:/backup:ro" postgres:17-alpine \
  pg_restore -l /backup/agent.dump >/dev/null || fail 'PostgreSQL backup is invalid'
tar -tzf "$backup_dir/browser-profiles.tar.gz" >/dev/null || fail 'browser archive invalid'
tar -tzf "$backup_dir/agent-keys.tar.gz" >/dev/null || fail 'keyring archive invalid'
echo 'PROD2_HOTFIX_PROTECTED_BACKUPS_VERIFIED'
changed=1
docker image tag "dhole/agent-api:master-$short" dhole/agent-api:latest
docker image tag "dhole/agent-workers:master-$short" dhole/agent-workers:latest
"${primary[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-api
"${active[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-api
"${active[@]}" up -d --no-deps --force-recreate --pull never dhole-agent-workers
new_worker="$(id dhole-agent-prod2 dhole-agent-workers)"
new_api="$(id dhole-agent-prod2 dhole-agent-api)"
new_primary_api="$(id dhole dhole-agent-api)"
[[ "$(status "$new_worker")" == running ]] && ! legacy_running ||
  fail 'single-owner Worker check failed'
[[ "$(digest "$new_worker")" == "$(docker image inspect -f '{{.Id}}' dhole/agent-workers:latest)" ]] ||
  fail 'active Worker image revision differs from release'
[[ "$(digest "$new_api")" == "$(docker image inspect -f '{{.Id}}' dhole/agent-api:latest)" &&
   "$(digest "$new_primary_api")" == "$(docker image inspect -f '{{.Id}}' dhole/agent-api:latest)" ]] ||
  fail 'Agent API image revisions differ from release'
[[ "$(mount "$new_worker" /data/browser-profiles)" == "$old_browser" &&
   "$(mount "$new_worker" /data/agent-keys)" == "$old_keys" &&
   "$(mount "$new_api" /data/agent-keys)" == "$old_api_keys" &&
   "$(id dhole-agent-prod2 hermes-agent)" == "$hermes" ]] ||
  fail 'persistent mounts or Hermes container were changed'
for host in dhole-dhole-agent-api-1 agent-api-prod2; do
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
[[ "$pump" == 1 ]] || fail 'prod2 queue pump did not start'
# Do not report a successful rollout if either API still advertises
# githubDispatcherConfigured=false or the active Worker lacks its credential.
# Docker inspect is captured in Python memory only; no token/value is logged.
python3 - "$new_primary_api" "$new_api" "$new_worker" <<'PY'
import json
import subprocess
import sys

labels = ("primary_api", "prod2_api", "prod2_worker")
assert len(sys.argv) == 4, "three exact prod2 runtime containers required"
for label, container in zip(labels, sys.argv[1:]):
    info = json.loads(subprocess.run(
        ["docker", "inspect", container], capture_output=True, text=True,
        check=True, timeout=20).stdout)[0]
    opts = dict(row.split("=", 1)
                for row in info.get("Config", {}).get("Env", []) if "=" in row)
    assert opts.get("GenerationOrchestrator__Enabled", "").lower() == "true", (
        "generation orchestrator disabled for " + label)
    assert opts.get("GenerationOrchestrator__GitHubToken"), (
        "generation dispatcher credential missing for " + label)
    assert opts.get("GenerationOrchestrator__ActionsRef") == "master", (
        "generation workflow reference not master for " + label)
    print("PROD2_GENERATION_RUNTIME_READY role=" + label)
PY
python3 - <<'PY'
import importlib.util
from pathlib import Path
spec=importlib.util.spec_from_file_location("owner",Path("scripts/production-generation-executor.py"))
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
assert m.find_running_generation()[0]=="prod2"
PY
completed=1
echo "PROD2_NPGSQL_HOTFIX_DEPLOYED master_sha=$sha active_worker=prod2"
echo "PROD2_HOTFIX_BACKUP_LOCATION=$backup_dir"
echo 'PROD2_HOTFIX_SINGLE_OWNER_AND_VOLUMES_VERIFIED'
