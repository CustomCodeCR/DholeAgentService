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
grep -Fq -- '--no-deps --force-recreate --pull never' "$script"
grep -Fq 'MaerskCircuit__Enabled=' "$script"
grep -Fq 'dhole-agent-prod1:dhole-agent-workers-prod1' "$script"
grep -Fq 'primary worker unexpectedly active' "$script"
if grep -Eq 'docker (volume rm|system prune)|down --volumes|down -v|DELETE FROM|UPDATE agent[.]' "$script"; then
  echo "Unsafe test-mode operation detected" >&2
  exit 1
fi
if GITHUB_REF=refs/heads/master bash "$script" staging disable BAD /dev/null 2>/dev/null; then
  echo "Mismatched branch unexpectedly accepted" >&2
  exit 1
fi
echo "Maersk circuit test-mode safety checks passed."
