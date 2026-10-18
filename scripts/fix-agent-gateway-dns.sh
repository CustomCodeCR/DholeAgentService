#!/usr/bin/env bash
# Fix DNS ambiguity in gateway only. Keep original prod1 worker and all APIs.
set -Eeuo pipefail
set +x
[[ "$GITHUB_REF" == refs/heads/master && -r "$DHOLE_GATEWAY_ENV" ]] || exit 2
export DHOLE_ENV_FILE="$DHOLE_GATEWAY_ENV" DHOLE_NETWORK=dhole DHOLE_IMAGE_TAG=latest
compose(){ docker compose --env-file "$DHOLE_ENV_FILE" -f .dhole-system/deploy/docker-compose.services.yml -p dhole "$@"; }
compose config -q
python3 - <<'PY'
import json,os,subprocess
def find(project,service):
    ids=subprocess.check_output(["docker","ps","-q","--filter","label=com.docker.compose.project="+project,
        "--filter","label=com.docker.compose.service="+service],text=True).splitlines()
    if len(ids)!=1: raise SystemExit("GATEWAY_FIX_REFUSED: missing or ambiguous "+project+"/"+service)
    return json.loads(subprocess.check_output(["docker","inspect",ids[0]],text=True))[0]
worker=find("dhole-agent-prod1","dhole-agent-workers-prod1")
main=find("dhole","dhole-agent-api")
standby=find("dhole-agent-prod2","dhole-agent-api")
gateway=find("dhole","api-gateway")
assert all(v["State"]["Status"]=="running" for v in [worker,main,standby,gateway])
assert main["Id"] != standby["Id"]
main_network=main["NetworkSettings"]["Networks"].get("dhole",{})
assert "dhole-dhole-agent-api-1" in main_network.get("Aliases",[]), "GATEWAY_FIX_REFUSED: missing unique API name"
env=lambda c: dict(x.split("=",1) for x in c["Config"]["Env"] if "=" in x)
assert env(main).get("GenerationOrchestrator__Enabled","").lower()=="true"
assert len(env(main).get("GenerationOrchestrator__GitHubToken",""))>=20
assert env(standby).get("GenerationOrchestrator__Enabled","").lower()!="true"
actual=env(gateway)
manifest=json.loads(subprocess.check_output(["docker","compose","--env-file",os.environ["DHOLE_ENV_FILE"],
    "-f",".dhole-system/deploy/docker-compose.services.yml","-p","dhole","config","--format","json"],text=True))
proposed=manifest["services"]["api-gateway"]["environment"]
unique="http://dhole-dhole-agent-api-1:8080"
assert proposed.get("AGENT_HTTP_URL")==unique and proposed.get("Gateway__Routes__11__Destination")==unique, "GATEWAY_FIX_REFUSED: unique gateway target absent"
old=actual.get("Gateway__Routes__11__Destination","")
assert old in ("http://dhole-agent-api:8080",unique), "GATEWAY_FIX_REFUSED: unknown previous route"
expected={"AGENT_HTTP_URL","Gateway__Routes__11__Destination"}
critical=lambda k: k.startswith(("Gateway__","Auth__")) or k in (
    "AGENT_HTTP_URL","CONTENT_HTTP_URL","HERMES_HTTP_URL","INTERNAL_SERVICE_KEY")
unexpected=[k for k in set(actual)|set(proposed) if critical(k) and k not in expected
            and actual.get(k)!=proposed.get(k)]
if unexpected: raise SystemExit("GATEWAY_FIX_REFUSED: unrelated gateway config differences="+",".join(sorted(unexpected)))
state=os.environ["RUNNER_TEMP"]+"/agent-gateway-route"
os.makedirs(state,mode=0o700,exist_ok=True)
for name,value in (("gateway-id",gateway["Id"]),("gateway-image",gateway["Image"]),
                   ("worker-id",worker["Id"]),("worker-image",worker["Image"]),
                   ("main-api-id",main["Id"]),("standby-api-id",standby["Id"])):
    with open(state+"/"+name,"w") as out: out.write(value)
with open(state+"/rollback.yml","w") as out:
    out.write("services:\n  api-gateway:\n    environment:\n")
    out.write("      AGENT_HTTP_URL: http://dhole-agent-api:8080\n")
    out.write("      Gateway__Routes__11__Destination: "+old+"\n")
print("GATEWAY_FIX_DNS_COLLISION_CONFIRMED primary_and_prod2_distinct=true")
print("GATEWAY_FIX_UNRELATED_SETTINGS_UNCHANGED")
PY
directory="$RUNNER_TEMP/agent-gateway-route"
old_image="$(cat "$directory/gateway-image")"
docker image inspect "$old_image" >/dev/null || { echo GATEWAY_FIX_REFUSED_OLD_IMAGE_MISSING >&2; exit 2; }
changed=0
completed=0
rollback(){
  rc=$?
  trap - EXIT
  if [[ "$changed" == 1 && "$completed" == 0 ]]; then
    echo GATEWAY_FIX_ROLLBACK_STARTED
    compose -f "$directory/rollback.yml" up -d --no-deps --no-build --pull never --force-recreate api-gateway || true
  fi
  exit "$rc"
}
trap rollback EXIT
changed=1
compose up -d --no-deps --no-build --pull never --force-recreate api-gateway
python3 - <<'PY'
import json,os,subprocess
state=os.environ["RUNNER_TEMP"]+"/agent-gateway-route/"
def inspect(id):
    return json.loads(subprocess.check_output(["docker","inspect",id],text=True))[0]
def one(project,service):
    ids=subprocess.check_output(["docker","ps","-q","--filter","label=com.docker.compose.project="+project,
        "--filter","label=com.docker.compose.service="+service],text=True).splitlines()
    assert len(ids)==1,"GATEWAY_FIX_REFUSED: ambiguous service "+project+"/"+service
    return inspect(ids[0])
gateway=one("dhole","api-gateway")
env=dict(v.split("=",1) for v in gateway["Config"]["Env"] if "=" in v)
unique="http://dhole-dhole-agent-api-1:8080"
assert gateway["State"]["Status"]=="running" and env.get("AGENT_HTTP_URL")==unique
assert env.get("Gateway__Routes__11__Destination")==unique
worker=inspect(open(state+"worker-id").read())
assert worker["State"]["Status"]=="running" and worker["Image"]==open(state+"worker-image").read()
assert one("dhole","dhole-agent-api")["Id"]==open(state+"main-api-id").read()
assert one("dhole-agent-prod2","dhole-agent-api")["Id"]==open(state+"standby-api-id").read()
print("GATEWAY_FIX_PRIMARY_BACKEND_PINNED prod1_worker_unchanged=true prod2_api_unchanged=true")
PY
for host in dhole-dhole-agent-api-1 api-gateway; do
  ok=0
  for i in $(seq 1 25); do
    if docker run --rm --network dhole curlimages/curl:8.12.1 -fsS --max-time 4 "http://$host:8080/health" >/dev/null 2>&1; then ok=1;break;fi
    sleep 2
  done
  [[ "$ok" == 1 ]] || { echo "GATEWAY_FIX_HEALTH_FAILED host=$host" >&2; exit 2; }
done
completed=1
echo "GATEWAY_FIX_COMPLETED: all /api/agents requests pinned to primary Agent API"
