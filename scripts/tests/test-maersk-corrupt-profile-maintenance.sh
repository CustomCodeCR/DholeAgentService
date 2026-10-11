#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
script="$root/scripts/maersk-corrupt-profile-maintenance.sh"
bash -n "$script"
for guard in 'maersk_browser_profile_corrupt' 'maersk_hcaptcha_required' \
  'maersk_authentication_edge_denied' 'maersk_circuits' \
  'sha256sum -c --status' 'Running' 'flock -n 9' \
  'rm -rf -- "$target"' 'PROFILE_ID="$flat_id"'; do
  grep -Fq "$guard" "$script" || { echo "Missing safe cleanup guard: $guard" >&2; exit 1; }
done
if grep -Eq 'docker volume rm|docker volume prune|docker system prune|down --volumes|down -v' "$script"; then
  echo "Cleanup may not remove entire Docker volumes" >&2; exit 1
fi
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin" "$tmp/backup" "$tmp/source"
printf 'fixture\n' > "$tmp/backup/agent.dump"
printf 'profile\n' > "$tmp/source/profile"
tar -czf "$tmp/backup/browser-profiles.tar.gz" -C "$tmp/source" .
tar -czf "$tmp/backup/agent-keys.tar.gz" -C "$tmp/source" .
printf 'environment=production\ncompose_project=dhole\nprofile_volume=dhole_agent-browser-profiles\ndatabase_name=agenttest\ncreated_epoch=%s\n' "$(date -u +%s)" > "$tmp/backup/metadata.txt"
(cd "$tmp/backup" && sha256sum agent.dump browser-profiles.tar.gz agent-keys.tar.gz metadata.txt > SHA256SUMS)
cat > "$tmp/env" <<'ENV'
AGENT_POSTGRES_CONNECTION_STRING=Host=postgres;Port=5432;Database=agenttest;Username=demo;Password=demo
ENV
cat > "$tmp/bin/docker" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
case "$1" in
  ps) exit 0 ;;
  run)
    [[ "$*" == *'postgres:16-alpine'* ]] || exit 7
    echo RESTRICTED
    ;;
  *) exit 7 ;;
esac
FAKE
chmod +x "$tmp/bin/docker"
export PATH="$tmp/bin:$PATH"
uuid=aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee
if bash "$script" production --apply "$uuid" "$tmp/backup" "$tmp/env" WRONG >/dev/null 2>&1; then
  echo 'Invalid confirmation accepted' >&2; exit 1
fi
if bash "$script" production --check "$uuid" "$tmp/backup" "$tmp/env" >/dev/null 2>&1; then
  echo 'Provider restrictions were not blocked' >&2; exit 1
fi
echo 'Chromium technical-corruption-only purge safety tests passed.'
