#!/bin/bash
# Phase B: create 30 proposals with this script's own tellings,
# kill -9 after 5 successful confirmations, then verify the full
# check list below. Every check is asserted: a violation prints
# FAIL and the script exits nonzero. A rerun is a self-enforcing
# pass or fail, not numbers a human must judge.
#
# The proposal set is scoped to the events this script creates
# (Id above the maximum Id present before its tellings), so the
# seeded Proposed notice is excluded and the set is exactly the
# 30 proposals the label claims.
#
# Check list, scoped to the 30 proposals unless a check
# is database-wide by nature:
#   1. integrity_check returns ok (database-wide).
#   2. foreign_key_check returns no rows (database-wide).
#   3. Zero partial events: no null or empty Body.
#   4. Zero null timestamps: no null Events.CreatedAtUtc on the
#      proposals, and no null EventAudits.AtUtc on their rows.
#   5. Zero audit and state mismatches.
#   6. Zero audit-less proposals.
#   7. Every proposal stands whole on exactly one side of the
#      transition: Confirmed count plus Proposed count equals
#      30, and Confirmed count equals the successful (302)
#      confirmations in the log.
set -u
export PATH="$HOME/.dotnet:$PATH"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_DIR="$ROOT/src/HouseholdCoordinator"
DB=/tmp/powercutB.db
PORT=5096
BASE=http://127.0.0.1:$PORT
JAR=/tmp/powercutB-cookies.txt
LOG=/tmp/powercutB-posts.log

FAILURES=0
fail() { echo "FAIL: $1" >&2; FAILURES=$((FAILURES+1)); }
assert_eq() {
  echo "$1: $2 (expected $3)"
  if [ "$2" != "$3" ]; then fail "$1: got $2, expected $3"; fi
}
assert_zero() {
  echo "$1: $2 (expected 0)"
  if [ "$2" != "0" ]; then fail "$1: got $2, expected 0"; fi
}

rm -f "$DB"* "$JAR" "$LOG"
cd "$APP_DIR"

start_app() {
  ConnectionStrings__HouseholdCoordinator="Data Source=$DB" \
  ASPNETCORE_URLS="$BASE" ASPNETCORE_ENVIRONMENT=Development \
    dotnet bin/Debug/net10.0/HouseholdCoordinator.dll > /tmp/powercutB-app.log 2>&1 &
  APP_PID=$!
  for i in $(seq 1 40); do
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$BASE/" || true)
    [ "$code" = "200" ] && return 0
    sleep 0.5
  done
  echo "APP FAILED TO START"; exit 1
}

start_app
# Snapshot the record before this script's tellings, so the
# proposal set below counts only what this script created.
MAX_ID_BEFORE=$(sqlite3 "$DB" "SELECT COALESCE(MAX(Id),0) FROM Events;")
echo "max event Id before this script's tellings: $MAX_ID_BEFORE"
TOKEN=$(curl -s -c "$JAR" -b "$JAR" "$BASE/" | grep -oP 'name="__RequestVerificationToken" type="hidden" value="\K[^"]+' | head -1)
for i in $(seq 1 30); do
  curl -s -o /dev/null -b "$JAR" -c "$JAR" -X POST "$BASE/?handler=Tell" \
    --data-urlencode "speakerId=1" \
    --data-urlencode "telling=Could you ask Lisa to print two copies of the bus pass?" \
    --data-urlencode "__RequestVerificationToken=$TOKEN"
done
PROPOSED=$(sqlite3 "$DB" "SELECT Id FROM Events WHERE ConfirmationState='Proposed' AND Id > $MAX_ID_BEFORE;")
PROPOSED_COUNT=$(echo "$PROPOSED" | grep -c .)
echo "proposals created by this script: $PROPOSED_COUNT"
assert_eq "proposals created by this script" "$PROPOSED_COUNT" "30"
ID_LIST=$(echo "$PROPOSED" | paste -sd,)

(
  for id in $PROPOSED; do
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -b "$JAR" -c "$JAR" \
      -X POST "$BASE/?handler=Confirm" \
      --data-urlencode "eventId=$id" \
      --data-urlencode "actorPersonId=1" \
      --data-urlencode "__RequestVerificationToken=$TOKEN" || echo "FAIL")
    echo "confirm $id -> $code" >> "$LOG"
  done
) &
BURST_PID=$!
for i in $(seq 1 80); do
  n=$(grep -c ' -> 302' "$LOG" 2>/dev/null || echo 0)
  [ "${n:-0}" -ge 5 ] && break
  sleep 0.2
done
kill -9 "$APP_PID" 2>/dev/null
wait "$BURST_PID" 2>/dev/null
TOTAL_OK=$(grep -c ' -> 302' "$LOG")
echo "successful confirms in log: $TOTAL_OK"
echo "failed confirms: $(grep -c 'FAIL' "$LOG")"

start_app
echo "restarted, / returned 200"
kill "$APP_PID" 2>/dev/null; wait "$APP_PID" 2>/dev/null

# Check 1: integrity.
INTEGRITY=$(sqlite3 "$DB" "PRAGMA integrity_check;")
assert_eq "integrity_check" "$INTEGRITY" "ok"

# Check 2: foreign keys.
FK_ROWS=$(sqlite3 "$DB" "PRAGMA foreign_key_check;" | wc -l)
assert_zero "foreign_key_check violating rows" "$FK_ROWS"

# Check 7, first half: whole on exactly one side of the transition.
CONFIRMED=$(sqlite3 "$DB" "SELECT COUNT(*) FROM Events WHERE Id IN ($ID_LIST) AND ConfirmationState='Confirmed';")
STILL_PROPOSED=$(sqlite3 "$DB" "SELECT COUNT(*) FROM Events WHERE Id IN ($ID_LIST) AND ConfirmationState='Proposed';")
echo "of the 30 proposals, now Confirmed: $CONFIRMED"
echo "of the 30 proposals, still Proposed: $STILL_PROPOSED"
assert_eq "Confirmed plus Proposed among the 30" "$((CONFIRMED + STILL_PROPOSED))" "30"
# Check 7, second half: exactly the completed confirmations stood.
assert_eq "Confirmed among the 30 vs successful confirms" "$CONFIRMED" "$TOTAL_OK"

# Check 3: partial events.
PARTIAL=$(sqlite3 "$DB" "SELECT COUNT(*) FROM Events WHERE Id IN ($ID_LIST) AND (Body IS NULL OR Body='');")
assert_zero "partial events (null or empty Body)" "$PARTIAL"

# Check 4: null timestamps, on the proposals and their audit rows.
NULL_EVENT_TS=$(sqlite3 "$DB" "SELECT COUNT(*) FROM Events WHERE Id IN ($ID_LIST) AND CreatedAtUtc IS NULL;")
assert_zero "proposals with null CreatedAtUtc" "$NULL_EVENT_TS"
NULL_AUDIT_TS=$(sqlite3 "$DB" "
SELECT COUNT(*) FROM EventAudits a
WHERE a.EventId IN ($ID_LIST) AND a.ObjectKind='Event' AND a.AtUtc IS NULL;")
assert_zero "proposal audit rows with null AtUtc" "$NULL_AUDIT_TS"

# Check 5: audit and state mismatches.
MISMATCH=$(sqlite3 "$DB" "
SELECT COUNT(*) FROM Events e
WHERE e.Id IN ($ID_LIST)
  AND EXISTS (SELECT 1 FROM EventAudits a WHERE a.EventId = e.Id AND a.ObjectKind='Event')
  AND (SELECT a.ToState FROM EventAudits a WHERE a.EventId = e.Id AND a.ObjectKind='Event' ORDER BY a.Id DESC LIMIT 1)
      <> e.ConfirmationState;")
assert_zero "audit/state mismatches among the 30" "$MISMATCH"

# Check 6: audit-less proposals.
AUDITLESS=$(sqlite3 "$DB" "
SELECT COUNT(*) FROM Events e
WHERE e.Id IN ($ID_LIST)
  AND NOT EXISTS (SELECT 1 FROM EventAudits a WHERE a.EventId = e.Id AND a.ObjectKind='Event');")
assert_zero "proposals with no audit row" "$AUDITLESS"

if [ "$FAILURES" -ne 0 ]; then
  echo "POWERCUT PHASE B: FAILED ($FAILURES check(s) failed)" >&2
  exit 1
fi
echo "POWERCUT PHASE B: PASS (all checks asserted)"
