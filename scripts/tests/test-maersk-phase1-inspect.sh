#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/bin"
cat > "$fixture/runtime.env" <<'ENV'
AGENT_POSTGRES_CONNECTION_STRING=Host=postgres;Port=5432;Database=dhole_agent;Username=fixture;Password=SENSITIVE_FIXTURE
MaerskCircuit__Enabled=false
MaerskMonitoring__Enabled=false
AgentQueue__ConcurrentDispatcherEnabled=false
AgentQueue__MaxConcurrentMaersk=1
ENV
cat > "$fixture/bin/docker" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
case "$1" in
  ps)
    if [[ "$*" == *"dhole-agent-api"* ]]; then echo api123; else echo worker456; fi ;;
  inspect)
    case "$*" in
      *'.Image'*) echo sha256:abc123 ;;
      *'org.opencontainers.image.revision'*) printf '%040d\n' 0 ;;
      *)
        cat <<'ENV'
Postgres__ConnectionString=Host=postgres;Port=5432;Database=dhole_agent;Username=fixture;Password=SENSITIVE_FIXTURE
MaerskCircuit__Enabled=false
MaerskMonitoring__Enabled=false
AgentQueue__ConcurrentDispatcherEnabled=false
AgentQueue__MaxConcurrentMaersk=1
ENV
        if [[ "$*" == *worker456* ]] && [ "$MOCK_MISMATCH" = 1 ]; then
          echo 'MaerskCircuit__Enabled=true'
        fi ;;
    esac ;;
  run)
    if [[ "$*" == *' --rm -i '* ]]; then
      echo 'profiles|total=1|blocked=1'
      echo 'circuit|Open|operator=true|reason=maersk_hcaptcha_required'
    else
      echo 'PHASE8_SCHEMA_READY'
    fi ;;
  *) echo 'Unexpected mock command' >&2; exit 1 ;;
esac
MOCK
chmod +x "$fixture/bin/docker"
export PATH="$fixture/bin:$PATH"
export MOCK_MISMATCH=0
bash -n "$root/scripts/inspect-maersk-phase1.sh"
bash "$root/scripts/inspect-maersk-phase1.sh" staging "$fixture/runtime.env" > "$fixture/success.log"
grep -q 'phase1|status|INVENTORY_CAPTURED' "$fixture/success.log"
grep -q 'database|schema|READY' "$fixture/success.log"
grep -q 'runtime|dhole-agent-api|db|MATCHES_ENV' "$fixture/success.log"
if grep -q 'SENSITIVE_FIXTURE' "$fixture/success.log"; then
  echo 'Sensitive connection leaked' >&2; exit 1
fi
export MOCK_MISMATCH=1
if bash "$root/scripts/inspect-maersk-phase1.sh" staging "$fixture/runtime.env" > "$fixture/fail.log" 2>&1; then
  echo 'Mismatched worker was accepted' >&2; exit 1
fi
grep -q 'INCOMPLETE_DO_NOT_ACTIVATE' "$fixture/fail.log"
echo 'Maersk phase 1 read-only regression passed.'
