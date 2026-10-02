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

if [ "${AGENT_NOVNC_ENABLED:-true}" = "true" ]; then
  NOVNC_PORT="${AGENT_NOVNC_PORT:-6080}"
  NOVNC_WEB_ROOT="${AGENT_NOVNC_WEB_ROOT:-/usr/share/novnc}"

  if ! command -v x11vnc >/dev/null 2>&1; then
    echo "AGENT_WORKER_X11VNC_NOT_FOUND"
    exit 1
  fi

  if ! command -v websockify >/dev/null 2>&1; then
    echo "AGENT_WORKER_WEBSOCKIFY_NOT_FOUND"
    exit 1
  fi

  if [ ! -d "$NOVNC_WEB_ROOT" ]; then
    echo "AGENT_WORKER_NOVNC_WEB_ROOT_NOT_FOUND path=$NOVNC_WEB_ROOT"
    exit 1
  fi

  x11vnc \
    -display "$DISPLAY" \
    -forever \
    -shared \
    -rfbport 5900 \
    -localhost \
    -nopw \
    >/tmp/agent-x11vnc.log 2>&1 &

  X11VNC_PID=$!

  for attempt in $(seq 1 20); do
    if ! kill -0 "$X11VNC_PID" 2>/dev/null; then
      echo "AGENT_WORKER_X11VNC_FAILED"
      cat /tmp/agent-x11vnc.log || true
      exit 1
    fi

    if bash -c "</dev/tcp/127.0.0.1/5900" >/dev/null 2>&1; then
      echo "AGENT_WORKER_X11VNC_READY display=$DISPLAY pid=$X11VNC_PID"
      break
    fi

    if [ "$attempt" -eq 20 ]; then
      echo "AGENT_WORKER_X11VNC_TIMEOUT"
      cat /tmp/agent-x11vnc.log || true
      exit 1
    fi

    sleep 0.25
  done

  websockify \
    --web="$NOVNC_WEB_ROOT" \
    "0.0.0.0:$NOVNC_PORT" \
    127.0.0.1:5900 \
    >/tmp/agent-novnc.log 2>&1 &

  NOVNC_PID=$!

  for attempt in $(seq 1 20); do
    if ! kill -0 "$NOVNC_PID" 2>/dev/null; then
      echo "AGENT_WORKER_NOVNC_FAILED"
      cat /tmp/agent-novnc.log || true
      exit 1
    fi

    if bash -c "</dev/tcp/127.0.0.1/$NOVNC_PORT" >/dev/null 2>&1; then
      echo "AGENT_WORKER_NOVNC_READY port=$NOVNC_PORT pid=$NOVNC_PID"
      break
    fi

    if [ "$attempt" -eq 20 ]; then
      echo "AGENT_WORKER_NOVNC_TIMEOUT port=$NOVNC_PORT"
      cat /tmp/agent-novnc.log || true
      exit 1
    fi

    sleep 0.25
  done
fi

echo "AGENT_WORKER_DOTNET_STARTING"
exec dotnet Dhole.Agent.Workers.dll
