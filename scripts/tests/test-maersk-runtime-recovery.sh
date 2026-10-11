#!/usr/bin/env bash
# Offline tests: no real Docker engine, provider session or database is touched.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
script="$root/scripts/maersk-runtime-recovery.sh"
workflow="$root/.github/workflows/maersk-runtime-recovery.yml"
bash -n "$script"
test -r "$workflow"
grep -Fq "cron: '*/15 * * * *'" "$workflow"
grep -Fq 'group: dhole-agent-production' "$workflow"
grep -Fq 'group: dhole-agent-staging' "$workflow"
grep -Fq 'MaerskCircuit__Enabled' "$script"
grep -Fq 'AGENT_RUNTIME_RECOVERY_INACTIVE' "$script"
grep -Fq 'AGENT_RUNTIME_RECOVERY_COOLDOWN' "$script"
grep -Fq -- '--force-recreate --pull never' "$script"
grep -Fq 'AGENT_RUNTIME_RECOVERY_NO_ACTION' "$script"
if grep -Eq 'docker (volume rm|system prune)|down --volumes|down -v|rm -rf.*browser-profiles' "$script"; then
  echo 'Unsafe removal of persistent state in recovery' >&2; exit 1
fi
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin" "$tmp/deploy" "$tmp/state"
touch "$tmp/deploy/docker-compose.services.yml"
touch "$tmp/deploy/docker-compose.infrastructure.yml"
touch "$tmp/deploy/docker-compose.staging-infra.yml"
touch "$tmp/deploy/docker-compose.staging.yml"
echo 'test=only' > "$tmp/runtime.env"
cat > "$tmp/bin/docker" <<'FAKE_DOCKER'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$1" == ps ]]; then
  args="$*"
  case "$args" in
    *dhole-agent-api*) echo api-id ;;
    *dhole-agent-workers*) echo worker-id ;;
    *hermes-agent-staging*) echo hermes-id ;;
    *hermes-agent*) echo hermes-id ;;
    *) exit 1 ;;
  esac
elif [[ "$1" == inspect ]]; then
  args="$*"
  if [[ "$args" == *'.Config.Env'* ]]; then
    [[ "${FAKE_CIRCUIT:-on}" == on ]] && echo 'MaerskCircuit__Enabled=true' ||
      echo 'MaerskCircuit__Enabled=false'
  elif [[ "$args" == *'.State.Status'* ]]; then
    if [[ "$args" == *worker-id* && "${FAKE_UNHEALTHY:-no}" == yes ]]; then
      echo 'restarting|none'
    else
      echo 'running|none'
    fi
  fi
else
  echo "Unexpected Docker mutation during check: $*" >&2
  exit 1
fi
FAKE_DOCKER
chmod +x "$tmp/bin/docker"
export PATH="$tmp/bin:$PATH"
export AGENT_RUNTIME_RECOVERY_STATE_DIR="$tmp/state"
inactive_output="$(FAKE_CIRCUIT=off bash "$script" production --check "$tmp" "$tmp/runtime.env")"
grep -Fq 'AGENT_RUNTIME_RECOVERY_INACTIVE' <<< "$inactive_output"
healthy_output="$(bash "$script" production --check "$tmp" "$tmp/runtime.env")"
grep -Fq 'AGENT_RUNTIME_RECOVERY_NO_ACTION' <<< "$healthy_output"
unhealthy_output="$(FAKE_UNHEALTHY=yes bash "$script" staging --check "$tmp" "$tmp/runtime.env")"
grep -Fq 'AGENT_RUNTIME_RECOVERY_CANDIDATE: staging/dhole-agent-workers' <<< "$unhealthy_output"
echo 'Guarded runtime recovery smoke tests passed.'
