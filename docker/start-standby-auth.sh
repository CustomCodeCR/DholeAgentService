#!/usr/bin/env bash
# Browser-only standby login. NEVER starts Dhole.Agent.Workers or queue consumers.
set -euo pipefail
set +x
credential="${1:?credential id required}"
[[ "$credential" =~ ^[0-9a-f]{32}$ ]] || exit 2
export DISPLAY=:99
root="/data/browser-profiles/MAERSK"
mkdir -p "$root"
profile="$root/$credential"
[[ ! -L "$root" && ! -L "$profile" ]] || exit 2
mkdir -p "$profile"
chmod 700 "$profile"
# The host orchestration has verified exclusive use of this generation's
# browser volume. A prior container can leave Chromium's singleton symlinks
# behind even when its Chrome process is gone. Never clear them unguarded.
[[ "${STANDBY_PROFILE_LOCK_CLEANUP_ALLOWED:-}" == "1" ]] || {
  echo "STANDBY_AUTH_PROFILE_LOCK_GUARD_MISSING" >&2
  exit 2
}
for lock in SingletonLock SingletonSocket SingletonCookie; do
  candidate="$profile/$lock"
  if [[ -L "$candidate" || -f "$candidate" ]]; then
    rm -f -- "$candidate"
  elif [[ -e "$candidate" ]]; then
    echo "STANDBY_AUTH_UNEXPECTED_SINGLETON_ENTRY" >&2
    exit 2
  fi
done
echo "STANDBY_AUTH_STALE_SINGLETON_LOCKS_CLEARED"
Xvfb "$DISPLAY" -screen 0 1600x900x24 -nolisten tcp -ac >/tmp/standby-xvfb.log 2>&1 &
xvfb_pid=$!
for i in $(seq 1 40); do
  [[ -S /tmp/.X11-unix/X99 ]] && break
  kill -0 "$xvfb_pid" 2>/dev/null || exit 1
  sleep 0.25
done
[[ -S /tmp/.X11-unix/X99 ]] || exit 1
x11vnc -display "$DISPLAY" -forever -shared -localhost -nopw -rfbport 5900 >/tmp/standby-x11vnc.log 2>&1 &
vnc_pid=$!
websockify --web=/usr/share/novnc 0.0.0.0:6080 127.0.0.1:5900 >/tmp/standby-novnc.log 2>&1 &
web_pid=$!
for i in $(seq 1 40); do
  if bash -c '</dev/tcp/127.0.0.1/6080' 2>/dev/null; then break; fi
  kill -0 "$web_pid" 2>/dev/null || exit 1
  sleep 0.25
done
bash -c '</dev/tcp/127.0.0.1/6080' 2>/dev/null || exit 1
chrome="$(find /ms-playwright -type f -name chrome -print -quit)"
[[ -n "$chrome" ]] || exit 1
cleanup() {
  kill "$vnc_pid" "$web_pid" "$xvfb_pid" 2>/dev/null || true
}
trap cleanup EXIT
echo "STANDBY_AUTH_VIEWER_READY"
# A bounded operator session; no credential submission or provider challenge bypass.
# Chromium writes directly into THIS generation's persistent browser volume.
browser_started_at=$SECONDS
set +e
timeout --signal=TERM --kill-after=8s 5400 "$chrome" \
  --no-sandbox --disable-dev-shm-usage --no-first-run \
  --remote-debugging-address=127.0.0.1 --remote-debugging-port=9222 \
  --remote-allow-origins=http://127.0.0.1:9222,http://localhost:9222 \
  --user-data-dir="$profile" --new-window https://www.maersk.com/ \
  >/tmp/standby-browser.log 2>&1
browser_exit=$?
set -e
if (( SECONDS - browser_started_at < 5 )); then
  echo "STANDBY_AUTH_CHROMIUM_EARLY_EXIT status=$browser_exit" >&2
  exit 1
fi
if [[ "$browser_exit" -ne 0 && "$browser_exit" -ne 124 ]]; then
  echo "STANDBY_AUTH_CHROMIUM_UNEXPECTED_EXIT status=$browser_exit" >&2
  exit 1
fi
echo "STANDBY_AUTH_VIEWER_STOPPED"
