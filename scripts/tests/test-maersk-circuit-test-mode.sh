#!/usr/bin/env bash
# Offline guards: no real Docker, DB, or provider calls.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
script="$root/scripts/maersk-circuit-test-mode.sh"
workflow="$root/.github/workflows/maersk-circuit-test-mode.yml"
bash -n "$script"
test -r "$workflow"
grep -Fq 'workflow_dispatch:' "$workflow"
grep -Fq 'group: dhole-agent-' "$workflow"
grep -Fq 'refs/heads/master' "$script"
grep -Fq 'refs/heads/develop' "$script"
grep -Fq 'DISABLE_MAERSK_CIRCUIT_SCHEDULES_PAUSED' "$script"
grep -Fq 'RESTORE_MAERSK_CIRCUIT' "$script"
grep -Fq 'MAERSK_TEST_ROLLBACK' "$script"
grep -Fq -- '--no-deps --no-build --force-recreate --pull never' "$script"
grep -Fq 'MaerskCircuit__Enabled=' "$script"
grep -Fq 'dhole-agent-prod1:dhole-agent-workers-prod1' "$script"
grep -Fq 'primary worker unexpectedly active' "$script"
if grep -Eq 'docker (volume rm|system prune)|down --volumes|down -v|DELETE FROM|UPDATE agent[.]' "$script"; then
  echo "Unsafe test-mode operation detected" >&2
  exit 1
fi
# Regression: older production API containers may have no Compose provenance
# labels. Known infrastructure manifests must be sufficient for safe operation.
! grep -Fq 'com.docker.compose.project.config_files' "$script"
grep -Fq 'docker-compose.agent-prod1.yml' "$script"
grep -Fq 'docker-compose.services.yml' "$script"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin" "$tmp/system/deploy" "$tmp/state"
for file in docker-compose.services.yml docker-compose.agent-prod1.yml docker-compose.staging.yml; do
  echo 'services: {}' > "$tmp/system/deploy/$file"
done
echo 'MaerskCircuit__Enabled=true' > "$tmp/runtime.env"
echo true > "$tmp/state/circuit"
cat > "$tmp/bin/docker" <<'FAKE_DOCKER'
#!/usr/bin/env bash
set -Eeuo pipefail
case "$1" in
  ps)
    arguments=" $* "
    case "$arguments" in
      *label=com.docker.compose.service=dhole-agent-workers-prod1*) echo worker-prod1 ;;
      *label=com.docker.compose.service=dhole-agent-api-prod1*) echo api-prod1 ;;
      *label=com.docker.compose.service=dhole-agent-api*) echo api-main ;;
      *label=com.docker.compose.service=dhole-agent-workers*) : ;;
      *) echo "Unexpected container query" >&2; exit 3 ;;
    esac ;;
  image)
    [[ "$2" == inspect ]] || exit 3
    echo 'sha256:unmodified-image' ;;
  inspect)
    template="$3"
    case "$template" in
      '{{.State.Status}}') echo running ;;
      '{{.Image}}') echo 'sha256:unmodified-image' ;;
      '{{range .Config.Env}}{{println .}}{{end}}') echo "MaerskCircuit__Enabled=$(cat "$MOCK_CIRCUIT_FILE")" ;;
      *'.Mounts'*) echo 'unchanged-volumes' ;;
      *) echo "Unexpected inspect call (especially a Compose label)" >&2; exit 3 ;;
    esac ;;
  compose)
    arguments=" $* "
    if [[ "$arguments" == *' config -q'* ]]; then
      [[ "$arguments" == *docker-compose.services.yml* || "$arguments" == *docker-compose.agent-prod1.yml* ]] || exit 3
    elif [[ "$arguments" == *' up -d '* ]]; then
      grep -Eq '^MaerskCircuit__Enabled=(true|false)
  echo "Mismatched branch unexpectedly accepted" >&2
  exit 1
fi
echo "Maersk circuit test-mode safety checks passed."
 "$DHOLE_ENV_FILE" || exit 3
      sed -n 's/^MaerskCircuit__Enabled=//p' "$DHOLE_ENV_FILE" > "$MOCK_CIRCUIT_FILE"
    else
      echo 'Unexpected Compose operation' >&2; exit 3
    fi ;;
  *)
    echo 'Unexpected Docker operation' >&2; exit 3 ;;
esac
FAKE_DOCKER
chmod +x "$tmp/bin/docker"
export MOCK_CIRCUIT_FILE="$tmp/state/circuit"
export RUNNER_TEMP="$tmp/state"
export PATH="$tmp/bin:$PATH"
GITHUB_REF=refs/heads/master bash "$script" production disable \
  DISABLE_MAERSK_CIRCUIT_SCHEDULES_PAUSED "$tmp/runtime.env" "$tmp/system" \
  > "$tmp/disable.log"
grep -Fxq false "$tmp/state/circuit"
grep -Fq 'MAERSK_TEST_DONE: production circuit=false' "$tmp/disable.log"
GITHUB_REF=refs/heads/master bash "$script" production restore \
  RESTORE_MAERSK_CIRCUIT "$tmp/runtime.env" "$tmp/system" > "$tmp/restore.log"
grep -Fxq true "$tmp/state/circuit"
grep -Fq 'MAERSK_TEST_DONE: production circuit=true' "$tmp/restore.log"

if GITHUB_REF=refs/heads/master bash "$script" staging disable BAD /dev/null 2>/dev/null; then
  echo "Mismatched branch unexpectedly accepted" >&2
  exit 1
fi
echo "Maersk circuit test-mode safety checks passed."
