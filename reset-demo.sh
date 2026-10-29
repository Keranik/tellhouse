#!/bin/bash
# Lifecycle for the Tellhouse demo environment: start, stop, and
# reset to the pristine seeded day. See DEPLOY.md.
#
# This script is the local packaging lifecycle tool. Its reset
# stops the app, deletes the database, starts again (the app
# recreates and reseeds on first start), then verifies the
# pristine day: HTTP 200, 6 persons, 16 events, 23 audits, 1 promise.
# It is not the deployed product's reset mechanism. On a deployed
# instance the reset is the sealed backup and restore machinery
# (see DEPLOY.md): per-session pristine instances where the
# hosting makes that practical, and the sealed backup/restore
# reset as the floor.
#
# Required environment:
#   APP_DLL   path to the published HomeNode.dll
#   DEMO_DB   path to the demo database file
# Optional environment:
#   DEMO_PORT     listen port (default 8080)
#   DEMO_PIDFILE  pid file (default /tmp/tellhouse-demo.pid)
#   MODEL_API_KEY passed through to the app when set in the
#                 caller's environment (Live mode; never stored
#                 by this script)
# Extraction runs Live on the STANDARD model (muse-spark-1.3).
# Words a visitor types never run on the training tier. For
# local packaging verification only, set
# EXTRACTION_MODE=Mock to spend no live calls.
set -u
export PATH="$HOME/.dotnet:$PATH"

APP_DLL=${APP_DLL:?set APP_DLL to the published HomeNode.dll}
DEMO_DB=${DEMO_DB:?set DEMO_DB to the demo database path}
DEMO_PORT=${DEMO_PORT:-8080}
PIDFILE=${DEMO_PIDFILE:-/tmp/tellhouse-demo.pid}
EXTRACTION_MODE=${EXTRACTION_MODE:-Live}
BASE="http://127.0.0.1:$DEMO_PORT"

app_pid() {
  [ -f "$PIDFILE" ] && cat "$PIDFILE" || true
}

start() {
  if [ -n "$(app_pid)" ] && kill -0 "$(app_pid)" 2>/dev/null; then
    echo "already running (pid $(app_pid))"
    return 0
  fi
  ASPNETCORE_URLS="$BASE" \
  ASPNETCORE_ENVIRONMENT=Production \
  ConnectionStrings__HomeNode="Data Source=$DEMO_DB" \
  Extraction__Mode="$EXTRACTION_MODE" \
  Extraction__Model="muse-spark-1.3" \
  MODEL_API_KEY="${MODEL_API_KEY:-}" \
    nohup dotnet "$APP_DLL" > "${PIDFILE}.log" 2>&1 &
  echo $! > "$PIDFILE"
  for i in $(seq 1 120); do
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$BASE/" || true)
    [ "$code" = "200" ] && { echo "started (pid $(app_pid)), / returned 200"; return 0; }
    sleep 0.5
  done
  echo "FAILED to start; last log lines:"
  tail -5 "${PIDFILE}.log"
  return 1
}

stop() {
  pid=$(app_pid)
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
    kill "$pid"
    for i in $(seq 1 20); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.25
    done
    kill -9 "$pid" 2>/dev/null || true
    echo "stopped (pid $pid)"
  else
    echo "not running"
  fi
  rm -f "$PIDFILE"
}

verify() {
  persons=$(sqlite3 "$DEMO_DB" "SELECT COUNT(*) FROM Persons;" 2>/dev/null || echo "?")
  events=$(sqlite3 "$DEMO_DB" "SELECT COUNT(*) FROM Events;" 2>/dev/null || echo "?")
  audits=$(sqlite3 "$DEMO_DB" "SELECT COUNT(*) FROM EventAudits;" 2>/dev/null || echo "?")
  promises=$(sqlite3 "$DEMO_DB" "SELECT COUNT(*) FROM Promises;" 2>/dev/null || echo "?")
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$BASE/" || true)
  echo "verify: / -> $code, persons=$persons, events=$events, audits=$audits, promises=$promises"
  [ "$code" = "200" ] && [ "$persons" = "6" ] && [ "$events" = "16" ] && [ "$audits" = "23" ] && [ "$promises" = "1" ]
}

case "${1:-}" in
  start) start ;;
  stop) stop ;;
  verify) verify ;;
  reset)
    stop
    rm -f "$DEMO_DB" "$DEMO_DB-wal" "$DEMO_DB-shm"
    start && verify && echo "reset complete: pristine seeded day"
    ;;
  *)
    echo "usage: $0 start|stop|reset|verify" >&2
    exit 2
    ;;
esac
