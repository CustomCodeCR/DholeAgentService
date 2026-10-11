#!/usr/bin/env bash
# Guarded manual provisioning of *one* independently persisted production generation.
# Never delete, reset, or copy browser profiles/sessions; never start a standby Worker.
set -euo pipefail
set +x
generation="${1:?Usage: provision-production-generation.sh prodN check|apply <DholeSystem-dir> <prepared-env>}"
mode="${2:-check}"
system="${3:?Specify DholeSystem checkout directory}"
environment="${4:?Specify prepared production environment file}"
[[ "$generation" =~ ^prod[1-9][0-9]*$ && "${#generation}" -le 64 ]] || {
  echo "GENERATION_INVALID_NAME" >&2; exit 2;
}
[[ "$mode" == check || "$mode" == apply ]] || { echo "GENERATION_INVALID_MODE" >&2; exit 2; }
[[ -f "$system/deploy/docker-compose.agent-generation.yml" && -r "$environment" ]] || {
  echo "GENERATION_MANIFEST_OR_ENV_MISSING" >&2; exit 2;
}
command -v docker >/dev/null
project="dhole-agent-$generation"
export AGENT_GENERATION="$generation"
export DHOLE_NETWORK=dhole
export DHOLE_IMAGE_TAG="${DHOLE_IMAGE_TAG:-latest}"
export DHOLE_ENV_FILE="$environment"
compose=(docker compose --env-file "$environment" -f "$system/deploy/docker-compose.agent-generation.yml" -p "$project" --profile activated)
# A compose-generated project may use only a unique per-generation keyring.
original_keys=dhole_agent-data-protection-keys
keys="dhole_agent_${generation}_keys"
docker network inspect dhole >/dev/null
docker volume inspect "$original_keys" >/dev/null
for image in dhole/agent-api dhole/agent-workers dhole/hermes-agent; do
  docker image inspect "$image:$DHOLE_IMAGE_TAG" >/dev/null
done
"${compose[@]}" config -q
root_available="$(df -Pk / | awk 'NR == 2 { print $4 }')"
[[ "$root_available" =~ ^[0-9]+$ && "$root_available" -ge 5242880 ]] || {
  echo "GENERATION_INSUFFICIENT_ROOT_SPACE" >&2; exit 2;
}
# No reprovision if any old version exists: preserves original volumes and
# prevents Compose from quietly changing an existing browser/runtime identity.
old="$(docker ps -aq --filter "label=com.docker.compose.project=$project")"
if [[ -n "$old" ]]; then
  # Existing generations are immutable; verify the *actual* services before
  # considering the infrastructure provisioned. A partial/legacy environment
  # must never be marked ready based on its project label alone.
  for service in hermes-agent dhole-agent-api dhole-agent-workers; do
    ids="$(docker ps -aq --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=$service")"
    [[ "$(printf '%s\n' "$ids" | sed '/^$/d' | wc -l)" -eq 1 ]] || {
      echo "GENERATION_EXISTING_INCOMPLETE: $service" >&2
      exit 1
    }
    status="$(docker inspect -f '{{.State.Status}}' "$ids")"
    if [[ "$service" == dhole-agent-workers ]]; then
      [[ "$status" == created || "$status" == exited ]] || {
        echo "GENERATION_EXISTING_WORKER_MUST_BE_STOPPED" >&2
        exit 1
      }
    else
      [[ "$status" == running ]] || {
        echo "GENERATION_EXISTING_SERVICE_UNHEALTHY: $service" >&2
        exit 1
      }
    fi
  done
  echo "GENERATION_EXISTING_VERIFIED: $generation; sessions and volumes untouched"
  exit 0
fi
if [[ "$mode" == check ]]; then
  echo "GENERATION_PREPROVISION_CHECK_OK: $generation; no changes made"
  exit 0
fi
# The one-time copy is only encryption-key material for *shared encrypted DB
# credentials*, not Chromium cookies, profile, login or Hermes session state.
docker volume create "$keys" >/dev/null
docker run --rm --entrypoint /bin/sh \
  -v "$original_keys:/source:ro" -v "$keys:/destination" alpine:3.20 -ec '
    if [ -z "$(ls -A /destination)" ]; then
      cp -a /source/. /destination/
    fi
    test -n "$(ls -A /destination)"
    chmod 700 /destination
  '
# Herm es and API are isolated and dormant Worker is only CREATED, never started.
"${compose[@]}" up -d --no-build --pull never \
  hermes-config-init hermes-agent dhole-agent-api
"${compose[@]}" create --no-build --pull never dhole-agent-workers
worker="$("${compose[@]}" ps -aq dhole-agent-workers)"
[[ -n "$worker" ]] || { echo "GENERATION_WORKER_MISSING" >&2; exit 1; }
state="$(docker inspect -f '{{.State.Status}}' "$worker")"
[[ "$state" == created || "$state" == exited ]] || { echo "GENERATION_WORKER_UNEXPECTEDLY_ACTIVE" >&2; exit 1; }
for attempt in $(seq 1 30); do
  if docker run --rm --network dhole curlimages/curl:8.12.1 --fail --silent --show-error \
      --max-time 4 "http://agent-api-$generation:8080/health" >/dev/null 2>&1 \
    && docker run --rm --network dhole curlimages/curl:8.12.1 --fail --silent --show-error \
      --max-time 4 "http://hermes-agent-$generation:8642/health" >/dev/null 2>&1; then
    echo "GENERATION_INFRASTRUCTURE_READY: $generation; standby Worker inactive"
    echo "GENERATION_PROVIDER_AUTHORIZATION_NOT_VERIFIED"
    exit 0
  fi
  sleep 4
done
echo "GENERATION_INFRASTRUCTURE_HEALTH_FAILED: $generation" >&2
exit 1
