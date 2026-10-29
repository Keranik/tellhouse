#!/bin/bash
# Phase A: kill -9 mid-burst during telling submissions, then verify
# the full check list below. Every check is asserted: a violation
# prints FAIL and the script exits nonzero. A rerun is a
# self-enforcing pass or fail, not numbers a human must judge.
# Check list, scoped to the burst events (Id above the
# seeded baseline) unless a check is database-wide by nature:
#   1. integrity_check returns ok (database-wide).
#   2. foreign_key_check returns no rows (database-wide).
#   3. Zero partial events: no null or empty Body.
#   4. Zero null timestamps: no null Events.CreatedAtUtc on burst
#      events, and no null EventAudits.AtUtc on their audit rows.
#   5. Zero audit and state mismatches: each burst event's state
#      equals the latest row of its audit trail.
#   6. Zero audit-less burst events.
#   7. Exactly the completed writes stood: burst events after
#      restart equal the successful (302) POSTs in the log.
set -u
export PATH="$HOME/.dotnet:$PATH"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_DIR="$ROOT/src/HouseholdCoordinator"
DB=/tmp/powercutA.db
PORT=5097
BASE=http://127.0.0.1:$PORT
JAR=/tmp/powercutA-cookies.txt
LOG=/tmp/powercutA-posts.log

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

ConnectionStrings__HouseholdCoordinator="Data Source=$DB" \
ASPNETCORE_URLS="$BASE" ASPNETCORE_ENVIRONMENT=Development \
  dotnet bin/Debug/net10.0/HouseholdCoordinator.dll > /tmp/powercutA-app.log 2>&1 &
APP_PID=$!
for i in $(seq 1 40); do
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$BASE/" || true)
  [ "$code" = "200" ] && break
  sleep 0.5
done
BASELINE=$(sqlite3 "$DB" "SELECT COUNT(*) FROM Events;")
echo "seeded baseline events: $BASELINE"

TOKEN=$(curl -s -c "$JAR" -b "$JAR" "$BASE/" | grep -oP 'name="__RequestVerificationToken" type="hidden" value="\K[^"]+' | head -1)

(
  for i in $(seq 1 90); do
    case $((i % 3)) in
      0) T="I'm at Domino's, heading home after the store." ;;
      1) T="Toilet paper is low." ;;
      2) T="I'm at the hardware store, heading home after the store." ;;
    esac
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -b "$JAR" -c "$JAR" \
      -X POST "$BASE/?handler=Tell" \
      --data-urlencode "speakerId=2" \
      --data-urlencode "telling=$T" \
      --data-urlencode "__RequestVerificationToken=$TOKEN" || echo "FAIL")
    echo "tell $i -> $code" >> "$LOG"
  done
) &
BURST_PID=$!
for i in $(seq 1 80); do
  n=$(grep -c ' -> 302' "$LOG" 2>/dev/null || echo 0)
  [ "${n:-0}" -ge 25 ] && break
  sleep 0.2
done
kill -9 "$APP_PID" 2>/dev/null
KILLED_AT=$(grep -c ' -> 302' "$LOG" 2>/dev/null || echo 0)
wait "$BURST_PID" 2>/dev/null
TOTAL_OK=$(grep -c ' -> 302' "$LOG")
echo "successful (302) POSTs at kill moment: $KILLED_AT"
echo "total 302s in log: $TOTAL_OK"
echo "total failed POSTs: $(grep -c 'FAIL' "$LOG")"

# Restart on the same database and verify.
ConnectionStrings__HouseholdCoordinator="Data Source=$DB" \
ASPNETCORE_URLS="$BASE" ASPNETCORE_ENVIRONMENT=Development \
  dotnet bin/Debug/net10.0/HouseholdCoordinator.dll > /tmp/powercutA-app2.log 2>&1 &
APP_PID=$!
for i in $(seq 1 40); do
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$BASE/" || true)
  [ "$code" = "200" ] && break
  sleep 0.5
done
echo "restarted, / returned 200"
kill "$APP_PID" 2>/dev/null; wait "$APP_PID" 2>/dev/null

# Check 1: integrity.
INTEGRITY=$(sqlite3 "$DB" "PRAGMA integrity_check;")
assert_eq "integrity_check" "$INTEGRITY" "ok"

# Check 2: foreign keys.
FK_ROWS=$(sqlite3 "$DB" "PRAGMA foreign_key_check;" | wc -l)
assert_zero "foreign_key_check violating rows" "$FK_ROWS"

# Check 7: exactly the completed writes stood.
BURST_EVENTS=$(sqlite3 "$DB" "SELECT COUNT(*) FROM Events WHERE Id > $BASELINE;")
assert_eq "events created by burst vs successful POSTs" "$BURST_EVENTS" "$TOTAL_OK"

# Check 3: partial events.
PARTIAL=$(sqlite3 "$DB" "SELECT COUNT(*) FROM Events WHERE Id > $BASELINE AND (Body IS NULL OR Body='');")
assert_zero "partial events (null or empty Body)" "$PARTIAL"

# Check 4: null timestamps, on the burst events and their audit rows.
NULL_EVENT_TS=$(sqlite3 "$DB" "SELECT COUNT(*) FROM Events WHERE Id > $BASELINE AND CreatedAtUtc IS NULL;")
assert_zero "burst events with null CreatedAtUtc" "$NULL_EVENT_TS"
NULL_AUDIT_TS=$(sqlite3 "$DB" "
SELECT COUNT(*) FROM EventAudits a
JOIN Events e ON e.Id = a.EventId
WHERE e.Id > $BASELINE AND a.ObjectKind='Event' AND a.AtUtc IS NULL;")
assert_zero "burst audit rows with null AtUtc" "$NULL_AUDIT_TS"

# Check 5: audit and state mismatches.
MISMATCH=$(sqlite3 "$DB" "
SELECT COUNT(*) FROM Events e
WHERE e.Id > $BASELINE
  AND EXISTS (SELECT 1 FROM EventAudits a WHERE a.EventId = e.Id AND a.ObjectKind='Event')
  AND (SELECT a.ToState FROM EventAudits a WHERE a.EventId = e.Id AND a.ObjectKind='Event' ORDER BY a.Id DESC LIMIT 1)
      <> e.ConfirmationState;")
assert_zero "audit/state mismatches (burst events)" "$MISMATCH"

# Check 6: audit-less events.
AUDITLESS=$(sqlite3 "$DB" "
SELECT COUNT(*) FROM Events e
WHERE e.Id > $BASELINE
  AND NOT EXISTS (SELECT 1 FROM EventAudits a WHERE a.EventId = e.Id AND a.ObjectKind='Event');")
assert_zero "burst events with no audit row" "$AUDITLESS"

if [ "$FAILURES" -ne 0 ]; then
  echo "POWERCUT PHASE A: FAILED ($FAILURES check(s) failed)" >&2
  exit 1
fi
echo "POWERCUT PHASE A: PASS (all checks asserted)"
