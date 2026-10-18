#!/usr/bin/env bash
set -Eeuo pipefail
set +x
[[ "$GITHUB_REF" == refs/heads/master && -r "$SAFE_AGENT_API_ENV" ]] || exit 2
export DHOLE_ENV_FILE="$SAFE_AGENT_API_ENV" DHOLE_NETWORK=dhole DHOLE_IMAGE_TAG=latest
compose(){ docker compose --env-file "$DHOLE_ENV_FILE" -f .dhole-system/deploy/docker-compose.services.yml -p dhole "$@"; }
compose config -q
python3 - <<'PY'
import importlib.util,json,os,subprocess
spec=importlib.util.spec_from_file_location("owner","scripts/production-generation-executor.py")
owner=importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)
assert owner.find_running_generation()[0]=="prod1","API_ONLY_REFUSED: prod1 not owner"
def item(project,service):
    ids=subprocess.check_output(["docker","ps","-aq","--filter","label=com.docker.compose.project="+project,"--filter","label=com.docker.compose.service="+service],text=True).splitlines()
    assert len(ids)==1,"API_ONLY_REFUSED: ambiguous service"
    return json.loads(subprocess.check_output(["docker","inspect",ids[0]],text=True))[0]
worker=item("dhole-agent-prod1","dhole-agent-workers-prod1")
api=item("dhole","dhole-agent-api")
assert worker["State"]["Status"]=="running" and api["State"]["Status"]=="running"
current=dict(row.split("=",1) for row in api["Config"]["Env"] if "=" in row)
cmd=["docker","compose","--env-file",os.environ["DHOLE_ENV_FILE"],"-f",".dhole-system/deploy/docker-compose.services.yml","-p","dhole","config","--format","json"]
planned=json.loads(subprocess.check_output(cmd,text=True))["services"]["dhole-agent-api"]["environment"]
keys=("Postgres__ConnectionString","Redis__ConnectionString","Auth__Authority","Auth__Audience","CredentialProtection__KeyRingPath","GenerationOrchestrator__Enabled","GenerationOrchestrator__GitHubToken","GenerationOrchestrator__ActionsRef")
for key in keys:
    assert current.get(key) and current[key]==planned.get(key),"API_ONLY_REFUSED: changed config "+key
mounts=[m["Name"] for m in api["Mounts"] if m["Destination"]=="/data/agent-keys" and m.get("Name")]
assert len(mounts)==1
state=os.environ["RUNNER_TEMP"]+"/standby-api-only"
os.makedirs(state,mode=0o700,exist_ok=True)
for key,value in (("worker-id",worker["Id"]),("worker-image",worker["Image"]),("key-volume",mounts[0]),("old-api-image",api["Image"])):
    with open(state+"/"+key,"w") as output: output.write(value)
print("API_ONLY_RUNTIME_AND_WORKER_VERIFIED")
PY
state="$RUNNER_TEMP/standby-api-only"
old_image="$(cat "$state/old-api-image")"
docker image inspect "$old_image" >/dev/null || exit 2
new_tag="standby-auth-api-$GITHUB_RUN_ID"
docker image inspect "dhole/agent-api:$new_tag" >/dev/null || exit 2
rollback_tag="rollback-standby-api-$GITHUB_RUN_ID"
docker image tag "$old_image" "dhole/agent-api:$rollback_tag"
changed=0
complete=0
rollback(){
  result=$?
  trap - EXIT
  if [[ "$changed" == 1 && "$complete" == 0 ]]; then
    echo API_ONLY_ROLLBACK_STARTED
    export DHOLE_IMAGE_TAG="$rollback_tag"
    compose up -d --no-deps --no-build --pull never --force-recreate dhole-agent-api || true
  fi
  exit "$result"
}
trap rollback EXIT
export DHOLE_IMAGE_TAG="$new_tag"
changed=1
compose up -d --no-deps --no-build --pull never --force-recreate dhole-agent-api
python3 - <<'PY'
import json,os,subprocess
state=os.environ["RUNNER_TEMP"]+"/standby-api-only/"
ids=subprocess.check_output(["docker","ps","-q","--filter","label=com.docker.compose.project=dhole","--filter","label=com.docker.compose.service=dhole-agent-api"],text=True).splitlines()
assert len(ids)==1
api=json.loads(subprocess.check_output(["docker","inspect",ids[0]],text=True))[0]
image=subprocess.check_output(["docker","image","inspect","-f","{{.Id}}","dhole/agent-api:standby-auth-api-"+os.environ["GITHUB_RUN_ID"]],text=True).strip()
assert api["State"]["Status"]=="running" and api["Image"]==image
assert next(m["Name"] for m in api["Mounts"] if m["Destination"]=="/data/agent-keys")==open(state+"key-volume").read()
worker=json.loads(subprocess.check_output(["docker","inspect",open(state+"worker-id").read()],text=True))[0]
assert worker["State"]["Status"]=="running" and worker["Image"]==open(state+"worker-image").read()
print("API_ONLY_PROD1_WORKER_AND_KEYS_PRESERVED")
PY
docker image inspect curlimages/curl:8.12.1 >/dev/null || docker pull curlimages/curl:8.12.1 >/dev/null
healthy=0
for attempt in $(seq 1 30); do
  if docker run --rm --pull never --network dhole curlimages/curl:8.12.1 -fsS --max-time 4 http://dhole-agent-api:8080/health >/dev/null 2>&1; then healthy=1;break;fi
  sleep 2
done
[[ "$healthy" == 1 ]] || exit 2
complete=1
echo STANDBY_AUTH_PRIMARY_API_ONLY_DEPLOYED
