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
timeout --signal=TERM --kill-after=8s 1800 "$chrome" \
  --no-sandbox --disable-dev-shm-usage --no-first-run \
  --user-data-dir="$profile" --new-window https://www.maersk.com/ \
  >/tmp/standby-browser.log 2>&1 || true
echo "STANDBY_AUTH_VIEWER_STOPPED"
