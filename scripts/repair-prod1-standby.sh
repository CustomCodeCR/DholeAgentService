#!/usr/bin/env bash
# Recover only legacy prod1 Agent API and its dormant Worker.
# Never rotate profiles/sessions or remove any container volume.
set -euo pipefail
set +x
system="${1:?DholeSystem checkout required}"
environment="${2:?Protected runtime .env required}"
[[ -r "$environment" && -f "$system/deploy/docker-compose.agent-prod1.yml" ]] || {
  echo "PROD1_MANIFEST_OR_ENV_MISSING"; exit 2;
}
export DHOLE_ENV_FILE="$environment"
export DHOLE_NETWORK=dhole
export DHOLE_IMAGE_TAG=latest
project=dhole-agent-prod1
compose=(docker compose --env-file "$environment" -f "$system/deploy/docker-compose.agent-prod1.yml" -p "$project" --profile activated)
for key in Auth__Jwt__SecretKey Auth__Jwt__Issuer Auth__Jwt__Audience Auth__Jwt__ExpirationMinutes Auth__Jwt__RequireHttpsMetadata ConnectionStrings__Postgres; do
  grep -q "^$key=." "$environment" || {
    echo "PROD1_REQUIRED_RUNTIME_KEY_MISSING: $key"; exit 2;
  }
done
"${compose[@]}" config -q
docker network inspect dhole >/dev/null
hermes="$(docker ps -aq --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=hermes-agent-prod1')"
api="$(docker ps -aq --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=dhole-agent-api-prod1')"
[[ "$(printf '%s\n' "$hermes" | sed '/^$/d' | wc -l)" -eq 1 ]] || { echo "PROD1_HERMES_ID_INVALID"; exit 1; }
[[ "$(printf '%s\n' "$api" | sed '/^$/d' | wc -l)" -eq 1 ]] || { echo "PROD1_API_ID_INVALID"; exit 1; }
[[ "$(docker inspect -f '{{.State.Status}}' "$hermes")" == running ]] || { echo "PROD1_HERMES_NOT_RUNNING"; exit 1; }
worker="$(docker ps -aq --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=dhole-agent-workers-prod1')"
[[ "$(printf '%s\n' "$worker" | sed '/^$/d' | wc -l)" -le 1 ]] || { echo "PROD1_MULTIPLE_WORKERS_REFUSED"; exit 1; }
if [[ -n "$worker" ]]; then
  state="$(docker inspect -f '{{.State.Status}}' "$worker")"
  [[ "$state" == created || "$state" == exited ]] || {
    echo "PROD1_WORKER_ACTIVE_REFUSED"; exit 1;
  }
fi
# Maintain compatibility with the previous API keyring. The legacy container
# currently mounts the production keyring. Do not wipe it or replace the volume.
original_keys=dhole_agent-data-protection-keys
target_keys=dhole_agent_prod1_keys
docker volume inspect "$original_keys" >/dev/null
docker volume create "$target_keys" >/dev/null
docker run --rm --entrypoint /bin/sh \
  -v "$original_keys:/source:ro" -v "$target_keys:/destination" alpine:3.20 -ec '
  if [ -z "$(ls -A /destination)" ]; then cp -a /source/. /destination/; fi
  test -n "$(ls -A /destination)"
  chmod 700 /destination
'
# Recreate ONLY stateless API. Hermes and browser volume remain untouched.
"${compose[@]}" up -d --no-deps --no-build --pull never --force-recreate dhole-agent-api-prod1
if [[ -z "$worker" ]]; then
  "${compose[@]}" create --no-build --pull never dhole-agent-workers-prod1
fi
worker="$(docker ps -aq --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=dhole-agent-workers-prod1')"
[[ -n "$worker" ]] || { echo "PROD1_WORKER_MISSING"; exit 1; }
state="$(docker inspect -f '{{.State.Status}}' "$worker")"
[[ "$state" == created || "$state" == exited ]] || { echo "PROD1_WORKER_ACTIVE_REFUSED"; exit 1; }
api="$(docker ps -aq --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=dhole-agent-api-prod1')"
mounted="$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/data/agent-keys"}}{{.Name}}{{end}}{{end}}' "$api")"
[[ "$mounted" == "$target_keys" ]] || { echo "PROD1_KEYRING_MOUNT_MISMATCH"; exit 1; }
probe() {
  docker run --rm --network dhole curlimages/curl:8.12.1 \
    --silent --output /dev/null --connect-timeout 3 --max-time 5 \
    --write-out '%{http_code}' "$1" 2>/dev/null || true
}
for attempt in $(seq 1 30); do
  if [[ "$(probe http://dhole-agent-api-prod1:8080/health)" == 200 && "$(probe http://hermes-agent-prod1:8642/health)" == 200 ]]; then
    echo "PROD1_INFRASTRUCTURE_READY: API/Hermes healthy; Worker stopped; persistent volumes untouched"
    exit 0
  fi
  sleep 4
done
echo "PROD1_INFRASTRUCTURE_HEALTH_FAILED"; exit 1
