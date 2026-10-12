#!/usr/bin/env bash
# Read-only generation health diagnosis. No application logs, env vars, auth tokens,
# browser profiles, DB data, or container mutations are read or printed.
set -euo pipefail
generation="${1:-prod2}"
[[ "$generation" =~ ^prod[1-9][0-9]*$ && "${#generation}" -le 64 ]] || {
  echo "GENERATION_DIAG_INVALID_NAME"; exit 2;
}
project="dhole-agent-$generation"
printf 'GENERATION_DIAG_START generation=%s project=%s\n' "$generation" "$project"
docker network inspect dhole >/dev/null || {
  echo 'GENERATION_DIAG_NETWORK_MISSING'; exit 1;
}
for service in hermes-config-init hermes-agent dhole-agent-api dhole-agent-workers; do
  ids="$(docker ps -aq --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=$service")"
  count="$(printf '%s\n' "$ids" | sed '/^$/d' | wc -l)"
  if [[ "$count" -ne 1 ]]; then
    printf 'GENERATION_DIAG_SERVICE_COUNT service=%s count=%s\n' "$service" "$count"
    continue
  fi
  state="$(docker inspect -f '{{.State.Status}}' "$ids")"
  health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}not-defined{{end}}' "$ids")"
  restarts="$(docker inspect -f '{{.RestartCount}}' "$ids")"
  printf 'GENERATION_DIAG_CONTAINER service=%s state=%s health=%s restarts=%s\n' \
    "$service" "$state" "$health" "$restarts"
done
for service in agent-api hermes-agent; do
  case "$service" in
    agent-api) address="http://agent-api-$generation:8080/health" ;;
    hermes-agent) address="http://hermes-agent-$generation:8642/health" ;;
  esac
  # Curl writes only HTTP code and exit code: never response bodies or secrets.
  result="$(docker run --rm --network dhole curlimages/curl:8.12.1 \
    --silent --output /dev/null --connect-timeout 3 --max-time 7 \
    --write-out '%{http_code}|%{exitcode}' "$address" 2>/dev/null)" || true
  if [[ "$result" =~ ^[0-9]{3}\|[0-9]+$ ]]; then
    printf 'GENERATION_DIAG_HTTP service=%s response_and_curl_exit=%s\n' "$service" "$result"
  else
    printf 'GENERATION_DIAG_HTTP service=%s probe_unavailable\n' "$service"
  fi
done
# Identify startup failure types without publishing logs, PII or secrets.
api_id="$(docker ps -aq --filter "label=com.docker.compose.project=$project" \
  --filter 'label=com.docker.compose.service=dhole-agent-api' | head -n1)"
prod_id="$(docker ps -aq --filter 'label=com.docker.compose.project=dhole' \
  --filter 'label=com.docker.compose.service=dhole-agent-api' | head -n1)"
if [[ -n "$api_id" ]]; then
  for key in Auth__Authority Auth__Audience Postgres__ConnectionString \
    Redis__ConnectionString ASPNETCORE_ENVIRONMENT CredentialProtection__KeyRingPath; do
    in_alt="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$api_id" \
      | cut -d= -f1 | grep -Fx -c "$key" || true)"
    in_primary=unknown
    if [[ -n "$prod_id" ]]; then
      in_primary="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$prod_id" \
        | cut -d= -f1 | grep -Fx -c "$key" || true)"
    fi
    printf 'GENERATION_DIAG_CONFIG key=%s primary_present=%s standby_present=%s\n' \
      "$key" "$in_primary" "$in_alt"
  done
  if [[ -n "$prod_id" ]]; then
    # Print only missing configuration KEY NAMES and value equality; never
    # print credentials, URLs, identifiers, cookies, tokens, or raw log lines.
    python3 - "$prod_id" "$api_id" <<'PY'
import subprocess,sys,json,re
def keys(container_id):
    raw=subprocess.check_output(
        ["docker","inspect","--format","{{json .Config.Env}}",container_id],
        text=True,timeout=10)
    return dict(value.split("=",1) for value in json.loads(raw) if "=" in value)
a,b=keys(sys.argv[1]),keys(sys.argv[2])
prefixes=("Auth__","Jwt__","Postgres__","Redis__","ConnectionStrings__",
          "CredentialProtection__","DataProtection__","Security__","ASPNETCORE_")
for key in sorted(a.keys()-b.keys()):
    if key.startswith(prefixes):
        print("GENERATION_DIAG_MISSING_STANDBY_KEY="+key)
for key in ("Auth__Authority","Auth__Audience","Auth__Jwt__SecretKey",
            "Postgres__ConnectionString","Redis__ConnectionString",
            "CredentialProtection__KeyRingPath"):
    if key in a and key in b:
        print("GENERATION_DIAG_VALUE_MATCH key="+key+
              " matches_primary="+str(a[key]==b[key]).lower())
PY
  fi
  docker logs --tail 200 "$api_id" 2>&1 | python3 -c '
import re,sys
types=set()
frames=set()
for line in sys.stdin:
    m=re.search(r"(?<![\w.])((?:System|Npgsql|Microsoft|Dhole|CustomCodeFramework)[\w.]*Exception)",line)
    if m: types.add(m.group(1))
    f=re.search(r"\bat ([A-Za-z_][\w.]*)\(",line)
    if f and re.match(r"^(?:Dhole|Microsoft|Npgsql|CustomCodeFramework)[.]",f.group(1)):
        frames.add(f.group(1))
for value in sorted(types)[:8]: print("GENERATION_DIAG_EXCEPTION_TYPE="+value)
for value in sorted(frames)[:8]: print("GENERATION_DIAG_STACK_FRAME="+value)
' || true
fi
echo 'GENERATION_DIAG_FINISHED (no containers or volumes modified)'
