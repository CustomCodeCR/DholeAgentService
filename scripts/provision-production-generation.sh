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
# A previous failed attempt may have left containers and persistent
# volumes. Never discard those; only the *stateless standby API* may be
# recreated using the reviewed generic compose definition.
existing="$(docker ps -aq --filter "label=com.docker.compose.project=$project")"
if [[ -n "$existing" ]]; then
  for service in hermes-config-init hermes-agent dhole-agent-api dhole-agent-workers; do
    ids="$(docker ps -aq --filter "label=com.docker.compose.project=$project" \
      --filter "label=com.docker.compose.service=$service")"
    [[ "$(printf '%s\\n' "$ids" | sed '/^$/d' | wc -l)" -eq 1 ]] || {
      printf 'GENERATION_EXISTING_INCOMPLETE: service=%s\\n' "$service" >&2
      exit 1
    }
    state="$(docker inspect -f '{{.State.Status}}' "$ids")"
    if [[ "$service" == dhole-agent-workers ]]; then
      [[ "$state" == created || "$state" == exited ]] || {
        echo 'GENERATION_ACTIVE_WORKER_PROTECTED: refusing modifications' >&2
        exit 1
      }
    elif [[ "$service" == hermes-agent ]]; then
      [[ "$state" == running ]] || {
        echo 'GENERATION_HERMES_NOT_RUNNING: inspect existing container before recovery' >&2
        exit 1
      }
    fi
  done
  if [[ "$mode" == apply ]]; then
    # The prior prodN API lacked Auth__Authority/Auth__Audience and could
    # restart indefinitely. Replace only this *stateless* container; Compose
    # retains the existing keyring mount, Chromium profiles and Hermes data.
    echo "GENERATION_EXISTING_API_REPAIR: $generation (API only)"
    "${compose[@]}" up -d --no-deps --no-build --pull never \
      --force-recreate dhole-agent-api
  fi
else
  if [[ "$mode" == check ]]; then
    echo "GENERATION_PREPROVISION_CHECK_OK: $generation; no changes made"
    exit 0
  fi
  # One-time copy of *encryption key material*, never browser sessions.
  docker volume create "$keys" >/dev/null
  docker run --rm --entrypoint /bin/sh \
    -v "$original_keys:/source:ro" -v "$keys:/destination" alpine:3.20 -ec '
      if [ -z "$(ls -A /destination)" ]; then
        cp -a /source/. /destination/
      fi
      test -n "$(ls -A /destination)"
      chmod 700 /destination
    '
  "${compose[@]}" up -d --no-build --pull never \
    hermes-config-init hermes-agent dhole-agent-api
  "${compose[@]}" create --no-build --pull never dhole-agent-workers
fi
worker="$("${compose[@]}" ps -aq dhole-agent-workers)"
[[ -n "$worker" ]] || { echo 'GENERATION_WORKER_MISSING' >&2; exit 1; }
state="$(docker inspect -f '{{.State.Status}}' "$worker")"
[[ "$state" == created || "$state" == exited ]] || {
  echo 'GENERATION_WORKER_UNEXPECTEDLY_ACTIVE' >&2
  exit 1
}
probe() {
  # Silence response body/error text; report only numeric status and curl exit.
  docker run --rm --network dhole curlimages/curl:8.12.1 \
    --silent --output /dev/null --connect-timeout 3 --max-time 5 \
    --write-out '%{http_code}|%{exitcode}' "$1" 2>/dev/null || true
}
api_result='not-tested'
hermes_result='not-tested'
for attempt in $(seq 1 30); do
  api_result="$(probe "http://agent-api-$generation:8080/health")"
  hermes_result="$(probe "http://hermes-agent-$generation:8642/health")"
  if [[ "$api_result" == '200|0' && "$hermes_result" == '200|0' ]]; then
    api_id="$(docker ps -aq --filter "label=com.docker.compose.project=$project" \
      --filter 'label=com.docker.compose.service=dhole-agent-api')"
    api_state="$(docker inspect -f '{{.State.Status}}' "$api_id")"
    if [[ "$api_state" == running ]]; then
      echo "GENERATION_INFRASTRUCTURE_READY: $generation; standby Worker inactive"
      echo 'GENERATION_PROVIDER_AUTHORIZATION_NOT_VERIFIED'
      exit 0
    fi
  fi
  sleep 4
done
# No log bodies or environment variables: safe operational diagnostics only.
for service in hermes-agent dhole-agent-api dhole-agent-workers; do
  id="$(docker ps -aq --filter "label=com.docker.compose.project=$project" \
    --filter "label=com.docker.compose.service=$service" | head -n1)"
  if [[ -n "$id" ]]; then
    status="$(docker inspect -f '{{.State.Status}}' "$id")"
    health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}not-defined{{end}}' "$id")"
    restarts="$(docker inspect -f '{{.RestartCount}}' "$id")"
    printf 'GENERATION_HEALTH_DETAIL service=%s state=%s health=%s restarts=%s\\n' \
      "$service" "$status" "$health" "$restarts"
  fi
done
printf 'GENERATION_HEALTH_HTTP api=%s hermes=%s\\n' "$api_result" "$hermes_result"
echo "GENERATION_INFRASTRUCTURE_HEALTH_FAILED: $generation" >&2
exit 1
