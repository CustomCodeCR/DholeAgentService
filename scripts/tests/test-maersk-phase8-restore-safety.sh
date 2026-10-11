#!/usr/bin/env bash
# Offline regression: destructive restore options are permitted ONLY against
# a no-network disposable PostgreSQL container; never the live database.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
script="$root/scripts/maersk-phase8-restore-drill.sh"
bash -n "$script"
for marker in '--network none' '--clean --if-exists' '--single-transaction' \
  'PHASE8_RESTORE_REFERENCE_INTEGRITY_FAILED' \
  'PHASE8_RESTORE_OBJECT_CONFLICT' \
  'PHASE8_RESTORE_UNCLASSIFIED' 'docker rm -f -v "$container"'; do
  grep -Fq -- "$marker" "$script" || { echo "Restore safety marker absent: $marker" >&2; exit 1; }
done
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
mkdir -p "$scratch/bin" "$scratch/backup"
printf 'archived fixture\n' > "$scratch/backup/agent.dump"
printf 'postgres_major=17\n' > "$scratch/backup/metadata.txt"
cat > "$scratch/bin/docker" <<'FAKE_DOCKER'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$1" == container && "$2" == inspect ]]; then exit 1; fi
if [[ "$1" == run && "$2" == -d ]]; then echo isolated-id; exit 0; fi
if [[ "$1" == rm && "$2" == -f ]]; then exit 0; fi
if [[ "$1" == exec && "$*" == *pg_isready* ]]; then exit 0; fi
if [[ "$1" == exec && "$*" == *pg_restore* ]]; then
  printf 'pg_restore: error: could not execute query: ERROR: insert or update on table violates foreign key constraint\n' >&2
  exit 1
fi
echo "Unexpected Docker command" >&2
exit 1
FAKE_DOCKER
chmod +x "$scratch/bin/docker"
export PATH="$scratch/bin:$PATH"
export GITHUB_RUN_ID=48739909
output="$scratch/restore-output"
if bash "$script" "$scratch/backup" >"$output" 2>&1; then
  echo 'Restore must fail when foreign keys are violated' >&2
  exit 1
fi
grep -Fq PHASE8_RESTORE_REFERENCE_INTEGRITY_FAILED "$output" ||
  { echo 'Expected safe category on restore failure' >&2; cat "$output" >&2; exit 1; }
! grep -Fq 'pg_restore: error:' "$output" ||
  { echo 'Raw SQL diagnostics leaked into workflow output' >&2; exit 1; }
echo 'Phase 8 isolated restoration safety regression passed.'
