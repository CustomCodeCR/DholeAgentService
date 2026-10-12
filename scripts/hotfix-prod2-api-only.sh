#!/usr/bin/env bash
# Bring the already-provisioned prod2 API to the tested live production API image.
# Never start a standby Worker, alter provider auth, update the database, or copy browser profiles.
set -Eeuo pipefail
set +x
system="${1:?DholeSystem checkout required}"
runtime="${2:?protected production env required}"
backup_root="${3:?durable backup root required}"
[[ "${GITHUB_REF:-}" == refs/heads/master && "${GITHUB_EVENT_NAME:-}" == push ]] || exit 2
[[ -f "$system/deploy/docker-compose.agent-generation.yml" && -r "$runtime" && "$backup_root" == /* ]] || exit 2
export AGENT_GENERATION=prod2 DHOLE_ENV_FILE="$runtime" DHOLE_NETWORK=dhole DHOLE_IMAGE_TAG=latest
project=dhole-agent-prod2
compose=(docker compose --env-file "$runtime" -f "$system/deploy/docker-compose.agent-generation.yml" -p "$project")
die(){ echo "PROD2_API_HOTFIX_REFUSED: $*" >&2; exit 1; }
id(){
  local all
  all="$(docker ps -aq --filter "label=com.docker.compose.project=$1" --filter "label=com.docker.compose.service=$2")"
  [[ "$(printf '%s\n' "$all" | sed '/^$/d' | wc -l)" -eq 1 ]] || die "missing/ambiguous service $1/$2"
  printf '%s' "$all"
}
state(){ docker inspect -f '{{.State.Status}}' "$1"; }
digest(){ docker inspect -f '{{.Image}}' "$1"; }
mount(){ docker inspect -f "{{range .Mounts}}{{if eq .Destination \"$2\"}}{{.Name}}{{end}}{{end}}" "$1"; }
prod1="$(id dhole-agent-prod1 dhole-agent-workers-prod1)"
# Missing legacy prod container is safe; a running legacy prod Worker is not.
legacy_running="$(docker ps -q --filter 'label=com.docker.compose.project=dhole' --filter 'label=com.docker.compose.service=dhole-agent-workers')"
python3 - <<'PY'
import importlib.util
spec=importlib.util.spec_from_file_location("owner","scripts/production-generation-executor.py")
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
assert m.find_running_generation()[0]=="prod1", "prod1 must be the sole active Agent Worker"
PY
main_api="$(id dhole dhole-agent-api)"
prod1_api="$(id dhole-agent-prod1 dhole-agent-api-prod1)"
old_api="$(id "$project" dhole-agent-api)"
[[ "$(state "$prod1")" == running && -z "$legacy_running" ]] || die "prod1 sole-worker requirement failed"
[[ "$(state "$old_api")" == running ]] || die "prod2 existing API not running"
other_workers="$(docker ps -q --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=dhole-agent-workers")"
[[ -z "$other_workers" ]] || die "prod2 Worker must not be active"
fixed="$(docker image inspect -f '{{.Id}}' dhole/agent-api:latest)"
[[ "$(digest "$main_api")" == "$fixed" && "$(digest "$prod1_api")" == "$fixed" ]] || die "production and prod1 API images differ from approved release"
if [[ "$(digest "$old_api")" == "$fixed" ]]; then
  echo "PROD2_API_HOTFIX_ALREADY_CURRENT"; exit 0
fi
old_keys="$(mount "$old_api" /data/agent-keys)"
[[ -n "$old_keys" && "$old_keys" == dhole_agent_prod2_keys ]] || die "unexpected prod2 keyring mount"
[[ "$(df -Pk / | awk 'NR==2{print $4}')" -ge 2097152 ]] || die "root disk headroom low"
mkdir -p "$backup_root"; chmod 700 "$backup_root"
[[ "$(df -Pk "$backup_root" | awk 'NR==2{print $4}')" -ge 4194304 ]] || die "backup capacity insufficient"
"${compose[@]}" config -q
python3 - "$old_api" "${compose[@]}" <<'PY'
import sys,subprocess,json
api=json.loads(subprocess.check_output(["docker","inspect",sys.argv[1]],text=True))[0]
current=dict(item.split("=",1) for item in api["Config"]["Env"] if "=" in item)
args=sys.argv[2:]+["config","--format","json"]
manifest=json.loads(subprocess.check_output(args,text=True))
planned=manifest["services"]["dhole-agent-api"]["environment"]
keys=("Postgres__ConnectionString","Redis__ConnectionString",
      "Auth__Authority","Auth__Audience","Auth__Jwt__SecretKey",
      "Auth__Jwt__Issuer","Auth__Jwt__Audience",
      "CredentialProtection__KeyRingPath","Hermes__BaseUrl","Hermes__ApiKey")
for name in keys:
    if name not in current or name not in planned or current[name] != planned[name]:
        raise SystemExit("PROD2_API_CONFIG_MISMATCH key="+name)
print("PROD2_API_CONFIGURATION_COMPATIBLE keys="+str(len(keys)))
PY
snapshot="$backup_root/prod2/$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$backup_root/prod2"
mkdir -m 700 "$snapshot"
docker run --rm -v "$old_keys:/source:ro" -v "$snapshot:/backup" alpine:3.20 tar -C /source -czf /backup/agent-keys.tar.gz .
tar -tzf "$snapshot/agent-keys.tar.gz" >/dev/null || die "keyring archive invalid"
# Back up shared Agent PostgreSQL using the already-protected production connection.
connection="$(sed -n 's/^AGENT_POSTGRES_CONNECTION_STRING=//p' "$runtime" | head -n1 | tr -d '\r')"
[[ -n "$connection" ]] || die "missing protected Agent PostgreSQL connection"
value(){ printf '%s' "$connection" | tr ';' '\n' | sed -n "s/^$1=//p" | head -n1; }
dbhost="$(value Host)"; dbport="$(value Port)"; dbname="$(value Database)"; dbuser="$(value Username)"; dbpassword="$(value Password)"
[[ "$dbname" =~ ^[A-Za-z0-9_]+$ && -n "$dbuser" && -n "$dbpassword" ]] || die "invalid database config"
export PGPASSWORD="$dbpassword"
docker run --rm --network dhole -e PGPASSWORD -v "$snapshot:/backup" postgres:17-alpine \
  pg_dump -h "${dbhost:-postgres}" -p "${dbport:-5432}" -U "$dbuser" -d "$dbname" -Fc -f /backup/agent.dump
unset PGPASSWORD dbpassword connection
docker run --rm -v "$snapshot:/backup:ro" postgres:17-alpine pg_restore -l /backup/agent.dump >/dev/null || die "database backup invalid"
(cd "$snapshot" && sha256sum agent.dump agent-keys.tar.gz > SHA256SUMS && sha256sum -c --status SHA256SUMS) || die "snapshot invalid"
echo "PROD2_API_PROTECTED_BACKUP_VERIFIED path=$snapshot"
suffix="${GITHUB_RUN_ID:-manual}"
old_image="$(digest "$old_api")"
# Docker image GC can discard the source image metadata while a container
# is still running. Preserve its stateless filesystem as a local rollback image.
# docker commit excludes mounted secrets, databases and browser volumes.
if docker image inspect "$old_image" >/dev/null 2>&1; then
  docker image tag "$old_image" "dhole/agent-api:rollback-prod2-api-$suffix"
else
  docker commit --pause=false "$old_api" "dhole/agent-api:rollback-prod2-api-$suffix" >/dev/null ||
    die "unable to preserve old API for rollback"
fi
docker image inspect "dhole/agent-api:rollback-prod2-api-$suffix" >/dev/null ||
  die "old API rollback image not verified"
docker image tag "$fixed" "dhole/agent-api:prod2-api-hotfix-$suffix"
changed=0
completed=0
rollback(){
  local code=$?
  trap - EXIT
  if [[ "$changed" == 1 && "$completed" == 0 ]]; then
    echo "PROD2_API_ROLLBACK_ATTEMPTED"
    export DHOLE_IMAGE_TAG="rollback-prod2-api-$suffix"
    "${compose[@]}" up -d --no-deps --no-build --pull never --force-recreate dhole-agent-api || true
  fi
  exit "$code"
}
trap rollback EXIT
export DHOLE_IMAGE_TAG="prod2-api-hotfix-$suffix"
changed=1
"${compose[@]}" up -d --no-deps --no-build --pull never --force-recreate dhole-agent-api
new_api="$(id "$project" dhole-agent-api)"
[[ "$(state "$new_api")" == running && "$(digest "$new_api")" == "$fixed" ]] || die "new prod2 API image mismatch"
[[ "$(mount "$new_api" /data/agent-keys)" == "$old_keys" ]] || die "key volume changed"
legacy_running="$(docker ps -q --filter 'label=com.docker.compose.project=dhole' --filter 'label=com.docker.compose.service=dhole-agent-workers')"
[[ "$(state "$prod1")" == running && -z "$legacy_running" ]] || die "Worker ownership changed"
[[ -z "$(docker ps -q --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=dhole-agent-workers")" ]] || die "standby Worker started"
ok=0
for attempt in $(seq 1 25); do
  if docker run --rm --pull never --network dhole curlimages/curl:8.12.1 \
     -fsS --max-time 4 http://agent-api-prod2:8080/health >/dev/null 2>&1; then ok=1; break; fi
  sleep 2
done
[[ "$ok" == 1 ]] || die "prod2 API health failed"
completed=1
echo "PROD2_API_HOTFIX_DEPLOYED fixed_image=$fixed active_worker=prod1 prod2_worker=stopped"
echo "PROD2_API_PERSISTENT_KEYRING_UNCHANGED"
echo "PROD2_API_SNAPSHOT=$snapshot"
