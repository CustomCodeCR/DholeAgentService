#!/usr/bin/env bash
set -euo pipefail

echo "AGENT_WORKER_ENTRYPOINT_STARTED utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"

export DISPLAY="${DISPLAY:-:99}"

if command -v Xvfb >/dev/null 2>&1; then
  rm -f /tmp/.X99-lock /tmp/.X11-unix/X99 2>/dev/null || true

  Xvfb "$DISPLAY"     -screen 0 1920x1080x24     -nolisten tcp     -ac     >/tmp/agent-xvfb.log 2>&1 &

  XVFB_PID=$!

  for attempt in $(seq 1 20); do
    if ! kill -0 "$XVFB_PID" 2>/dev/null; then
      echo "AGENT_WORKER_XVFB_FAILED"
      cat /tmp/agent-xvfb.log || true
      exit 1
    fi

    if [ -S "/tmp/.X11-unix/X${DISPLAY#:}" ]; then
      echo "AGENT_WORKER_XVFB_READY display=$DISPLAY pid=$XVFB_PID"
      break
    fi

    if [ "$attempt" -eq 20 ]; then
      echo "AGENT_WORKER_XVFB_TIMEOUT display=$DISPLAY pid=$XVFB_PID"
      cat /tmp/agent-xvfb.log || true
      exit 1
    fi

    sleep 0.25
  done
else
  echo "AGENT_WORKER_XVFB_NOT_FOUND"
  exit 1
fi

echo "AGENT_WORKER_DOTNET_STARTING"
exec dotnet Dhole.Agent.Workers.dll
