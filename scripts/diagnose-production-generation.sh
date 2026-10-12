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
echo 'GENERATION_DIAG_FINISHED (no containers or volumes modified)'
