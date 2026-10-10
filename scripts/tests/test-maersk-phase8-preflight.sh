#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
temp="$(mktemp -d "$HOME/maersk-phase8-test.XXXXXX")"
trap 'rm -rf "$temp"' EXIT
mkdir -p "$temp/bin" "$temp/backup" "$temp/source"
cat > "$temp/bin/docker" <<'FAKE_DOCKER'
#!/bin/sh
# Archive is represented by an offline fixture; only preflight orchestration is tested.
exit 0
FAKE_DOCKER
chmod +x "$temp/bin/docker"
echo test-database-archive > "$temp/backup/agent.dump"
echo browser-profile-fixture > "$temp/source/profile"
tar -czf "$temp/backup/browser-profiles.tar.gz" -C "$temp/source" .
echo data-protection-fixture > "$temp/source/key"
tar -czf "$temp/backup/agent-keys.tar.gz" -C "$temp/source" .
printf 'environment=staging\ncompose_project=dhole-staging\ndatabase_name=dhole_agent\nprofile_volume=dhole-staging_agent-browser-profiles\nkeys_volume=dhole-staging_agent-data-protection-keys\ncreated_epoch=%s\n' "$(date -u +%s)" > "$temp/backup/metadata.txt"
(cd "$temp/backup" && sha256sum agent.dump browser-profiles.tar.gz agent-keys.tar.gz metadata.txt > SHA256SUMS)
cat > "$temp/runtime.env" <<'ENV'
AGENT_POSTGRES_CONNECTION_STRING=Host=postgres;Port=5432;Database=dhole_agent;Username=fixture;Password=fixture
AgentQueue__ConcurrentDispatcherEnabled=false
MaerskCircuit__Enabled=false
MaerskMonitoring__Enabled=false
ENV
preflight="$root/scripts/maersk-phase8-preflight.sh"
PATH="$temp/bin:$PATH" bash "$preflight" staging "$temp/backup" "$temp/runtime.env" >/dev/null

if PATH="$temp/bin:$PATH" bash "$preflight" production "$temp/backup" "$temp/runtime.env" >/dev/null 2>&1; then
  echo "Cross-environment evidence must be rejected" >&2; exit 1
fi
printf '\nMaerskCircuit__Enabled=true\n' >> "$temp/runtime.env"
if PATH="$temp/bin:$PATH" bash "$preflight" staging "$temp/backup" "$temp/runtime.env" >/dev/null 2>&1; then
  echo "Premature feature activation must be rejected" >&2; exit 1
fi
sed -i '/^MaerskCircuit__Enabled=true$/d' "$temp/runtime.env"
echo corrupted >> "$temp/backup/agent.dump"
if PATH="$temp/bin:$PATH" bash "$preflight" staging "$temp/backup" "$temp/runtime.env" >/dev/null 2>&1; then
  echo "Damaged backups must be rejected" >&2; exit 1
fi
echo "Phase 8 backup preflight regression suite passed."
