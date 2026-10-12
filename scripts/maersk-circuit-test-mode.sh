#!/usr/bin/env bash
# Operator-only temporary flag override; no secrets printed, no persistent data removed.
set -Eeuo pipefail
set +x
environment="${1:-}"; action="${2:-}"; confirmation="${3:-}"; source_env="${4:-}"
[[ -r "$source_env" ]] || { echo "MAERSK_TEST_REFUSED: missing env" >&2; exit 2; }
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
projects=(); services=(); dirs=(); manifests=()
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
  dir="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.working_dir"}}' "${ids[0]}")"
  files="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.config_files"}}' "${ids[0]}")"
  [[ "$dir" == /* && -d "$dir" && "$files" == /* ]] || {
    echo "MAERSK_TEST_REFUSED: missing Compose provenance for $target" >&2; exit 2;
  }
  IFS=',' read -ra paths <<< "$files"
  for file in "${paths[@]}"; do
    [[ "$file" == /* && -r "$file" ]] || { echo "MAERSK_TEST_REFUSED: missing deployed manifest" >&2; exit 2; }
  done
  projects+=("$project"); services+=("$service"); dirs+=("$dir"); manifests+=("$files")
done

work="$(mktemp -d "$state_dir/env.XXXXXX")"
chmod 700 "$work"
trap 'rm -rf "$work"' EXIT
cp "$source_env" "$work/runtime.env"
chmod 600 "$work/runtime.env"
if grep -q '^MaerskCircuit__Enabled=' "$work/runtime.env"; then
  sed -i "s/^MaerskCircuit__Enabled=.*/MaerskCircuit__Enabled=$new/" "$work/runtime.env"
else
  printf '\nMaerskCircuit__Enabled=%s\n' "$new" >> "$work/runtime.env"
fi
compose() {
  local index="$1" env_file="$2"; shift 2
  local -a flags=() paths=()
  IFS=',' read -ra paths <<< "${manifests[$index]}"
  for path in "${paths[@]}"; do flags+=(-f "$path"); done
  DHOLE_ENV_FILE="$env_file" DHOLE_NETWORK="$network" DHOLE_IMAGE_TAG="$tag" AGENT_GENERATION=prod1 \
    docker compose --project-directory "${dirs[$index]}" --env-file "$env_file" \
    "${flags[@]}" -p "${projects[$index]}" "$@"
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
      compose "$i" "$source_env" up -d --no-deps --force-recreate --pull never "${services[$i]}" >&2 || true
    done
  fi
  rm -rf "$work"
  exit "$rc"
}
trap rollback EXIT
for i in "${!targets[@]}"; do
  changed+=("$i")
  compose "$i" "$work/runtime.env" up -d --no-deps --force-recreate --pull never "${services[$i]}" >/dev/null
done
for i in "${!targets[@]}"; do
  mapfile -t ids < <(docker ps -aq \
    --filter "label=com.docker.compose.project=${projects[$i]}" \
    --filter "label=com.docker.compose.service=${services[$i]}")
  [[ "${#ids[@]}" == 1 && "$(docker inspect -f '{{.State.Status}}' "${ids[0]}")" == running ]] || exit 1
  actual="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "${ids[0]}" | grep '^MaerskCircuit__Enabled=' || true)"
  [[ "$actual" == "MaerskCircuit__Enabled=$new" ]] || exit 1
  echo "MAERSK_TEST_VERIFIED: ${targets[$i]} circuit=$new"
done
changed=()
echo "MAERSK_TEST_DONE: $environment circuit=$new"
echo "Provider login remains required. Temporary override may reset at next deployment."
