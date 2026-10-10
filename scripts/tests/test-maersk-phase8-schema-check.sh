#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fixture="$(mktemp -d "$HOME/maersk-phase8-schema-test.XXXXXX")"
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/bin"
cat > "$fixture/bin/docker" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$DOCKER_MOCK_LOG"
if [[ "$*" == *" psql "* ]]; then
  printf '%s\n' "${MOCK_SCHEMA_RESULT:-PHASE8_SCHEMA_READY}"
fi
MOCK
chmod +x "$fixture/bin/docker"
cat > "$fixture/agent.env" <<'ENV'
AGENT_POSTGRES_CONNECTION_STRING=Host=postgres;Port=5432;Database=dhole_agent;Username=fixture;Password=fixture
ENV

export PATH="$fixture/bin:$PATH"
export DOCKER_MOCK_LOG="$fixture/docker.log"
export DHOLE_ENV_FILE="$fixture/agent.env"
script="$root/scripts/maersk-phase8-schema-check.sh"
bash -n "$script"

bash "$script" staging
grep -Fq -- '--network dhole-staging' "$DOCKER_MOCK_LOG"
bash "$script" production
grep -Fq -- '--network dhole ' "$DOCKER_MOCK_LOG"

export MOCK_SCHEMA_RESULT=PHASE8_SCHEMA_MISSING
if bash "$script" production >/dev/null 2>&1; then
  echo "Schema gate accepted a missing migration" >&2
  exit 1
fi
echo "Phase 8 migration schema gate regressions passed."
