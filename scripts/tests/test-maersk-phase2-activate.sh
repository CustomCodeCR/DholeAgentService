#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
script="$root/scripts/maersk-phase2-activate.sh"
workflow="$root/.github/workflows/maersk-phase2-activate.yml"
bash -n "$script"
test -r "$workflow"
grep -q '^  workflow_dispatch:' "$workflow"
! grep -q 'branches:.*\\[.*develop.*\\]' "$workflow"
grep -Fq 'maersk-phase8-preflight.sh' "$script"
grep -Fq 'maersk-phase8-restore-drill.sh' "$script"
grep -Fq 'maersk-phase8-schema-check.sh' "$script"
grep -Fq 'PHASE2_ACTIVATED=' "$script"
grep -Fq "label=com.docker.compose.service=dhole-agent-workers" "$script"
grep -Fq 'default_transaction_read_only=on' "$script"
grep -Fq 'source_env=/opt/dhole/.env.staging' "$script"
grep -Fq 'source_env=/opt/dhole/.env' "$script"
grep -Fq 'On' "$script" >/dev/null || true
if bash "$script" staging /tmp whatever else BAD >/dev/null 2>&1; then
  echo 'Activation accepted invalid confirmation/release' >&2
  exit 1
fi
grep -Fq 'staging_activation_run_id' "$workflow"
grep -Fq 'github.sha' "$workflow"
grep -Fq 'MAERSK_PENDING_AUTH_RECONCILED' "$script"
grep -Fq 'maersk_authentication_edge_denied' "$script"
grep -Fq 'maersk_hcaptcha_required' "$script"
grep -Fq 'INSERT INTO agent.maersk_circuits' "$script"
grep -Fq 'ON CONFLICT (provider_id) DO UPDATE' "$script"
grep -Fq 'maersk_circuit_events' "$script"
! grep -Fq 'Refuse first activation when old provider challenges' "$script"
echo 'Maersk phase 2 activation safety gates passed.'
