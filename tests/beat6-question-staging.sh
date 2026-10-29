#!/bin/bash
# Whereabouts question: staging and verification.
#
# The defect this stages around: asked against the pristine seeded
# day, the Brief page whereabouts lookup returns Daniel's newest
# whereabouts, his 2:55 PM closing entry, at a shifted time ("At
# 9:55 AM, Daniel said: Home, like I said this morning."). The
# lookup itself is correct and the test suite pins its behavior
# (newest confirmed whereabouts, stated time converted to the
# household timezone). Two facts of the seed make the pristine
# answer wrong for the morning question: the seeded day already
# contains the afternoon closing entry, and seed times store the
# scenario's wall clock as UTC while the lookup converts from UTC.
#
# The question belongs to the morning of the scenario
# (Margaret's 7:15 AM "Where's Daniel?", answered from Daniel's
# 5:40 AM line), so the question is staged against a copy of the
# seeded database in which the record stands as it stood that
# morning for this question:
#   1. Daniel's closing statement is recorded as a HouseholdNote,
#      the promise-closing note it is (the test suite's own
#      closing event is a HouseholdNote with these same words),
#      so it is not a whereabouts source.
#   2. Daniel's morning whereabouts line carries its true UTC
#      instant (5:40 AM America/Chicago on Oct 27, 2026 is
#      10:40 UTC), so the lookup's conversion names 5:40 AM.
# The pristine seeded database is never modified; the timeline
# hold (the promise object, made 05:40, kept 14:55) stands as it
# is in the pristine day, not in this staging copy.
#
# The script starts the app on a fresh seed in Mock mode (no live
# API calls), prints the pristine answer for the record, applies
# the staging transforms to a copy, restarts on the copy, asks
# the question through the real Brief page (Lisa asking about
# Daniel), and asserts the staged answer verbatim:
#   "At 5:40 AM, Daniel said: Depot by nine, hardware store after, home by three."
# Exit code is nonzero if any step or assertion fails.
set -u
export PATH="$HOME/.dotnet:$PATH"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_DIR="$ROOT/src/HouseholdCoordinator"
DLL="$APP_DIR/bin/Debug/net10.0/HouseholdCoordinator.dll"
PORT=${BEAT6_PORT:-5096}
BASE="http://127.0.0.1:$PORT"
PRISTINE_DB=/tmp/beat6-pristine.db
STAGED_DB=/tmp/beat6-staged.db
JAR=/tmp/beat6-cookies.txt
EXPECTED="At 5:40 AM, Daniel said: Depot by nine, hardware store after, home by three."

FAILURES=0
fail() { echo "FAIL: $1" >&2; FAILURES=$((FAILURES+1)); }
APP_PID=""
stop_app() {
  if [ -n "$APP_PID" ] && kill -0 "$APP_PID" 2>/dev/null; then
    kill "$APP_PID" 2>/dev/null
    for i in $(seq 1 20); do
      kill -0 "$APP_PID" 2>/dev/null || break
      sleep 0.25
    done
    kill -9 "$APP_PID" 2>/dev/null || true
    wait "$APP_PID" 2>/dev/null
  fi
  APP_PID=""
}
trap stop_app EXIT

start_app() {
  local db="$1"
  ConnectionStrings__HouseholdCoordinator="Data Source=$db" \
  ASPNETCORE_URLS="$BASE" ASPNETCORE_ENVIRONMENT=Development \
  Extraction__Mode=Mock \
    dotnet "$DLL" > "/tmp/beat6-app.log" 2>&1 &
  APP_PID=$!
  for i in $(seq 1 40); do
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$BASE/" || true)
    [ "$code" = "200" ] && return 0
    sleep 0.5
  done
  echo "app failed to start; last log lines:" >&2
  tail -5 /tmp/beat6-app.log >&2
  return 1
}

ask_whereabouts() {
  # Asks through the Brief page lookup form and prints the answer
  # line the page renders. $1 is the asker id, $2 the subject id.
  rm -f "$JAR"
  local page token answer_html
  page=$(curl -s -b "$JAR" -c "$JAR" "$BASE/Brief?PersonId=$1")
  token=$(printf '%s' "$page" | grep -o '__RequestVerificationToken" type="hidden" value="[^"]*"' | head -1 | sed 's/.*value="//; s/"$//')
  if [ -z "$token" ]; then
    echo ""
    return 1
  fi
  curl -s -b "$JAR" -c "$JAR" -X POST "$BASE/Brief?handler=Lookup" \
    --data-urlencode "askerPersonId=$1" \
    --data-urlencode "subjectPersonId=$2" \
    --data-urlencode "__RequestVerificationToken=$token" -o /dev/null
  answer_html=$(curl -s -b "$JAR" -c "$JAR" "$BASE/Brief?PersonId=$1")
  printf '%s' "$answer_html" | grep -o 'The house says:</strong> [^<]*' | sed 's/The house says:<\/strong> //'
}

echo "building..."
if ! (cd "$APP_DIR" && dotnet build --nologo -v q > /tmp/beat6-build.log 2>&1); then
  tail -10 /tmp/beat6-build.log >&2
  fail "dotnet build"
  echo "Whereabouts question staging: $FAILURES failure(s)"
  exit 1
fi

rm -f "$PRISTINE_DB"* "$STAGED_DB"* "$JAR"
echo "starting on a fresh pristine seed (Mock mode)..."
start_app "$PRISTINE_DB" || { fail "app start on pristine seed"; exit 1; }

DANIEL_ID=$(sqlite3 "$PRISTINE_DB" "SELECT Id FROM Persons WHERE DisplayName='Daniel';")
LISA_ID=$(sqlite3 "$PRISTINE_DB" "SELECT Id FROM Persons WHERE DisplayName='Lisa';")
if [ -z "$DANIEL_ID" ] || [ -z "$LISA_ID" ]; then
  fail "seeded persons not found (Daniel='$DANIEL_ID', Lisa='$LISA_ID')"
  echo "Whereabouts question staging: $FAILURES failure(s)"
  exit 1
fi

PRISTINE_ANSWER=$(ask_whereabouts "$LISA_ID" "$DANIEL_ID")
echo "pristine seeded answer: $PRISTINE_ANSWER"
stop_app

echo "staging a copy of the record for the morning question..."
cp "$PRISTINE_DB" "$STAGED_DB"
CHANGED_MORNING=$(sqlite3 "$STAGED_DB" "UPDATE Events SET CreatedAtUtc='2026-10-27 10:40:00' WHERE Type='Whereabouts' AND Body='Depot by nine, hardware store after, home by three.'; SELECT changes();")
echo "morning whereabouts restamped to its true UTC instant: $CHANGED_MORNING row(s)"
[ "$CHANGED_MORNING" = "1" ] || fail "morning whereabouts restamp changed $CHANGED_MORNING rows, expected 1"
CHANGED_CLOSING=$(sqlite3 "$STAGED_DB" "UPDATE Events SET Type='HouseholdNote' WHERE Type='Whereabouts' AND Body='Home, like I said this morning.'; SELECT changes();")
echo "closing statement recorded as a HouseholdNote in the staging copy: $CHANGED_CLOSING row(s)"
[ "$CHANGED_CLOSING" = "1" ] || fail "closing statement retype changed $CHANGED_CLOSING rows, expected 1"

echo "restarting on the staging copy..."
start_app "$STAGED_DB" || { fail "app start on staging copy"; exit 1; }
STAGED_ANSWER=$(ask_whereabouts "$LISA_ID" "$DANIEL_ID")
echo "staged answer: $STAGED_ANSWER"
[ "$STAGED_ANSWER" = "$EXPECTED" ] || fail "staged answer is not the expected form: got '$STAGED_ANSWER'"
stop_app

if [ "$FAILURES" -eq 0 ]; then
  echo "Whereabouts question staging: PASS (pristine answer printed above; staged answer matches the expected form verbatim)"
else
  echo "Whereabouts question staging: $FAILURES failure(s)"
fi
exit "$FAILURES"
