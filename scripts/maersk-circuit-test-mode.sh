#!/usr/bin/env bash
# Operator-only temporary flag override; no secrets printed, no persistent data removed.
set -Eeuo pipefail
set +x
environment="${1:-}"; action="${2:-}"; confirmation="${3:-}"; source_env="${4:-}"
system_dir="${5:-}"
[[ -r "$source_env" && -r "$system_dir/deploy/docker-compose.services.yml" ]] || {
  echo "MAERSK_TEST_REFUSED: missing source env or DholeSystem infrastructure checkout" >&2; exit 2;
}
command -v docker >/dev/null && command -v flock >/dev/null
case "$environment:$action:${GITHUB_REF:-}" in
  production:disable:refs/heads/master|production:restore:refs/heads/master)
    network=dhole; tag=latest ;;
  staging:disable:refs/heads/develop|staging:restore:refs/heads/develop)
    network=dhole-staging; tag=staging ;;
  *) echo "MAERSK_TEST_REFUSED: invalid branch/environment/action" >&2; exit 2 ;;
esac
if [[ "$action" == disable ]]; then
  [[ "$confirmation" == DISABLE_MAERSK_CIRCUIT_SCHEDULES_PAUSED ]] || exit 2
  old=true; new=false
else
  [[ "$confirmation" == RESTORE_MAERSK_CIRCUIT ]] || exit 2
  old=false; new=true
fi
state_dir="${RUNNER_TEMP:-$HOME/.local/state}/maersk-circuit-test"
mkdir -p "$state_dir"; chmod 700 "$state_dir"
exec 9>"$state_dir/$environment.lock"
flock -n 9 || { echo "MAERSK_TEST_REFUSED: conflicting toggle" >&2; exit 2; }

if [[ "$environment" == production ]]; then
  targets=(dhole:dhole-agent-api dhole-agent-prod1:dhole-agent-api-prod1 dhole-agent-prod1:dhole-agent-workers-prod1)
  [[ -z "$(docker ps -q --filter label=com.docker.compose.project=dhole --filter label=com.docker.compose.service=dhole-agent-workers)" ]] || {
    echo "MAERSK_TEST_REFUSED: primary worker unexpectedly active" >&2; exit 2;
  }
else
  targets=(dhole-staging:dhole-agent-api dhole-staging:dhole-agent-workers)
fi
# Compose definitions come from a known DholeSystem checkout, not optional
# Docker labels. Existing production containers may lack Compose provenance.
if [[ "$environment" == production ]]; then
  [[ -r "$system_dir/deploy/docker-compose.agent-prod1.yml" ]] || {
    echo "MAERSK_TEST_REFUSED: missing prod1 Compose manifest" >&2; exit 2;
  }
else
  [[ -r "$system_dir/deploy/docker-compose.staging.yml" ]] || {
    echo "MAERSK_TEST_REFUSED: missing staging Compose manifest" >&2; exit 2;
  }
fi
projects=(); services=(); ids_before=(); images_before=(); mounts_before=()
for target in "${targets[@]}"; do
  project="${target%%:*}"; service="${target#*:}"
  mapfile -t ids < <(docker ps -aq --filter "label=com.docker.compose.project=$project" --filter "label=com.docker.compose.service=$service")
  [[ "${#ids[@]}" == 1 && "$(docker inspect -f '{{.State.Status}}' "${ids[0]}")" == running ]] || {
    echo "MAERSK_TEST_REFUSED: missing, duplicate, or stopped $target" >&2; exit 2;
  }
  actual="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "${ids[0]}" | grep '^MaerskCircuit__Enabled=' || true)"
  [[ "$actual" == "MaerskCircuit__Enabled=$old" ]] || {
    echo "MAERSK_TEST_REFUSED: unexpected circuit state on $target" >&2; exit 2;
  }
  case "$service" in
    *workers*) expected_image="dhole/agent-workers:$tag" ;;
    *api*) expected_image="dhole/agent-api:$tag" ;;
    *) echo "MAERSK_TEST_REFUSED: unknown service $target" >&2; exit 2 ;;
  esac
  expected_digest="$(docker image inspect -f '{{.Id}}' "$expected_image")" || {
    echo "MAERSK_TEST_REFUSED: image missing for $target" >&2; exit 2;
  }
  old_digest="$(docker inspect -f '{{.Image}}' "${ids[0]}")"
  [[ "$old_digest" == "$expected_digest" ]] || {
    echo "MAERSK_TEST_REFUSED: image tag does not match running $target" >&2; exit 2;
  }
  projects+=("$project"); services+=("$service")
  ids_before+=("${ids[0]}"); images_before+=("$old_digest")
  mounts_before+=("$(docker inspect -f '{{range .Mounts}}{{if or (eq .Destination "/data/browser-profiles") (eq .Destination "/data/agent-keys")}}{{.Destination}}:{{.Type}}:{{.Name}}:{{.Source}};{{end}}{{end}}' "${ids[0]}")")
done

work="$(mktemp -d "$state_dir/env.XXXXXX")"
chmod 700 "$work"
trap 'rm -rf "$work"' EXIT
cp "$source_env" "$work/runtime.env"
cp "$source_env" "$work/previous.env"
chmod 600 "$work/runtime.env" "$work/previous.env"
for entry in "runtime:$new" "previous:$old"; do
  file="$work/${entry%%:*}.env"; value="${entry#*:}"
  if grep -q '^MaerskCircuit__Enabled=' "$file"; then
    sed -i "s/^MaerskCircuit__Enabled=.*/MaerskCircuit__Enabled=$value/" "$file"
  else
    printf '\nMaerskCircuit__Enabled=%s\n' "$value" >> "$file"
  fi
done
if [[ "$environment" == production ]]; then
  # Prod1 Compose has not inherited the Auth:Jwt options found in the main
  # production API. Preserve its effective issuer, audience and secret on
  # every future toggle; never print these values or modify /opt/dhole/.env.
  python3 scripts/prepare-prod1-jwt-env.py "$work/runtime.env"
  python3 scripts/prepare-prod1-jwt-env.py "$work/previous.env"
fi
compose() {
  local index="$1" env_file="$2"; shift 2
  local -a flags=(-f "$system_dir/deploy/docker-compose.services.yml")
  if [[ "${projects[$index]}" == dhole-agent-prod1 ]]; then
    flags=(-f "$system_dir/deploy/docker-compose.agent-prod1.yml" --profile activated)
  elif [[ "$environment" == staging ]]; then
    flags+=(-f "$system_dir/deploy/docker-compose.staging.yml")
  fi
  DHOLE_ENV_FILE="$env_file" DHOLE_NETWORK="$network" DHOLE_IMAGE_TAG="$tag" AGENT_GENERATION=prod1 \
    docker compose --env-file "$env_file" "${flags[@]}" -p "${projects[$index]}" "$@"
}
for i in "${!targets[@]}"; do
  compose "$i" "$work/runtime.env" config -q || exit 2
done
changed=()
rollback() {
  rc=$?
  trap - EXIT
  if (( rc != 0 )); then
    echo "MAERSK_TEST_ROLLBACK: restoring previous containers" >&2
    for i in "${changed[@]}"; do
      compose "$i" "$work/previous.env" up -d --no-deps --no-build --force-recreate --pull never "${services[$i]}" >&2 || true
    done
  fi
  rm -rf "$work"
  exit "$rc"
}
trap rollback EXIT
for i in "${!targets[@]}"; do
  changed+=("$i")
  compose "$i" "$work/runtime.env" up -d --no-deps --no-build --force-recreate --pull never "${services[$i]}" >/dev/null
done
for i in "${!targets[@]}"; do
  mapfile -t ids < <(docker ps -aq \
    --filter "label=com.docker.compose.project=${projects[$i]}" \
    --filter "label=com.docker.compose.service=${services[$i]}")
  [[ "${#ids[@]}" == 1 && "$(docker inspect -f '{{.State.Status}}' "${ids[0]}")" == running ]] || exit 1
  actual="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "${ids[0]}" | grep '^MaerskCircuit__Enabled=' || true)"
  [[ "$actual" == "MaerskCircuit__Enabled=$new" ]] || exit 1
  [[ "$(docker inspect -f '{{.Image}}' "${ids[0]}")" == "${images_before[$i]}" ]] || {
    echo "MAERSK_TEST_FAILED: image changed for ${targets[$i]}" >&2; exit 1;
  }
  after="$(docker inspect -f '{{range .Mounts}}{{if or (eq .Destination "/data/browser-profiles") (eq .Destination "/data/agent-keys")}}{{.Destination}}:{{.Type}}:{{.Name}}:{{.Source}};{{end}}{{end}}' "${ids[0]}")"
  [[ "$after" == "${mounts_before[$i]}" ]] || {
    echo "MAERSK_TEST_FAILED: volumes changed for ${targets[$i]}" >&2; exit 1;
  }
  echo "MAERSK_TEST_VERIFIED: ${targets[$i]} circuit=$new image_and_volumes=preserved"
done
changed=()
echo "MAERSK_TEST_DONE: $environment circuit=$new"
echo "Provider login remains required. Temporary override may reset at next deployment."
