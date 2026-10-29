#!/usr/bin/env python3
"""Beat 4 live batch: the integrated home
node running in Live extraction mode (Contributor tier model,
muse-spark-1.3-contributor), one ready batch, counted.

The batch is two front door tellings from Margaret in one node
process:
  1. the trash turn telling, which the deterministic stated-turn
     pre-pass shapes (no API call is spent on it, by design), and
  2. one plain note telling, which only the live model extracts
     (the batch's one counted live API call).
Then the turn is confirmed on the front door and its delivery is
checked: exactly one delivery, to Daniel only.
"""
import http.cookiejar
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import time
import urllib.request
import urllib.error
import urllib.parse

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUN = "/tmp/int-beat4-live"
DOTNET = os.path.expanduser("~/.dotnet/dotnet")
NODE = "http://127.0.0.1:7442"
checks = []


def check(name, ok, detail=""):
    checks.append({"name": name, "passed": bool(ok), "detail": str(detail)[:500]})
    print(("PASS " if ok else "FAIL ") + name + ((" :: " + str(detail)[:300]) if not ok else ""))


def q(sql, args=()):
    con = sqlite3.connect(os.path.join(RUN, "node.db"))
    try:
        return con.execute(sql, args).fetchall()
    except sqlite3.OperationalError:
        return []
    finally:
        con.close()


def wait_for(fn, timeout=180.0, interval=1.0):
    end = time.time() + timeout
    last = None
    while time.time() < end:
        last = fn()
        if last:
            return last
        time.sleep(interval)
    return last


def main():
    shutil.rmtree(RUN, ignore_errors=True)
    os.makedirs(RUN)
    env = dict(os.environ, DOTNET_ROOT=os.path.expanduser("~/.dotnet"),
               DOTNET_CLI_TELEMETRY_OPTOUT="1",
               ASPNETCORE_URLS=NODE, ASPNETCORE_ENVIRONMENT="Development",
               ConnectionStrings__HomeNode=f"Data Source={RUN}/node.db",
               HOME_NODE_KEYSTORE=os.path.join(RUN, "node-keys"),
               Extraction__Mode="Live", Extraction__Model="muse-spark-1.3-contributor")
    log = open(os.path.join(RUN, "node.log"), "w")
    proc = subprocess.Popen([DOTNET, os.path.join(
        ROOT, "src/HomeNode/bin/Debug/net10.0/HomeNode.dll")], env=env,
        stdout=log, stderr=subprocess.STDOUT)
    try:
        up = wait_for(lambda: (lambda r: r if r and r[0][0] == 16 else None)(
            q("select count(*) from Events") if os.path.exists(os.path.join(RUN, "node.db")) else []), timeout=60)
        check("live-mode node seeds the integrated day (16 events)", bool(up) and up[0][0] == 16, up)

        def http_up():
            try:
                with urllib.request.urlopen(NODE + "/", timeout=5) as resp:
                    return resp.status == 200
            except Exception:
                return False

        if not wait_for(http_up, timeout=60):
            raise RuntimeError("node http surface did not come up")
        cj = http.cookiejar.CookieJar()
        opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))

        def front_door(handler, fields):
            with opener.open(NODE + "/") as resp:
                html = resp.read().decode()
            m = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html)
            fields = dict(fields, __RequestVerificationToken=m.group(1))
            data = urllib.parse.urlencode(fields).encode()
            r = urllib.request.Request(NODE + "/?handler=" + handler, data=data, method="POST")
            try:
                with opener.open(r) as resp:
                    return resp.status
            except urllib.error.HTTPError as e:
                return e.code

        trash = "Tonight is Daniel's turn to take the trash out, the truck comes at seven."
        st = front_door("Tell", {"speakerId": 1, "telling": trash})
        turn = wait_for(lambda: q("select Id, Type, ConfirmationState, Details, AudienceRule from Events where Body=?", (trash,)), timeout=60)
        ok = bool(turn) and turn[0][1] == "Task" and turn[0][2] == "Proposed" \
            and '"TurnPersonId":2' in (turn[0][3] or "") and '"RoutePersonId":2' in (turn[0][4] or "")
        action = q("select ActionType, TargetPersonId from EventActions where EventId=?", (turn[0][0],)) if turn else []
        check("Beat 4 live: trash turn through the real front door in Live mode lands as the Proposed Task with its NotifyPerson and stored Named route to Daniel",
              st == 200 and ok and action == [("NotifyPerson", 2)], (st, turn, action))
        turn_id = turn[0][0]

        note = "June is bringing the mail in at noon."
        st = front_door("Tell", {"speakerId": 1, "telling": note})
        got = wait_for(lambda: q("select Id, Type from Events where Body=?", (note,)), timeout=240)
        check("Beat 4 live batch: one plain telling extracted by the live model on the Contributor tier lands on the record",
              st == 200 and bool(got), (st, got))

        st = front_door("Confirm", {"eventId": turn_id, "actorPersonId": 1})
        state = wait_for(lambda: q("select ConfirmationState from Events where Id=? and ConfirmationState='Done'", (turn_id,)), timeout=60)
        deliveries = q("select ToPersonId, BodyText from AlertDeliveries where EventId=?", (turn_id,))
        check("Beat 4 live: confirmed turn is delivered to Daniel only, body verbatim, event Done",
              st == 200 and bool(state) and deliveries == [(2, trash)], (st, state, deliveries))
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except Exception:
            proc.kill()
    log_text = open(os.path.join(RUN, "node.log")).read()
    live_calls = log_text.count("Live extraction completed")
    passed = sum(1 for c in checks if c["passed"])
    evidence = {"batch": "Beat 4 live batch, integrated node, Live mode, Contributor tier",
                "model": "muse-spark-1.3-contributor", "live_api_calls_counted": live_calls,
                "checks": checks, "passed": passed, "total": len(checks)}
    out = os.path.join(ROOT, "lane-i-integration", "beat4-live-evidence.json")
    with open(out, "w") as f:
        json.dump(evidence, f, indent=2)
    print(f"CHECKS {passed} passed, {len(checks) - passed} failed, of {len(checks)}; live API calls counted: {live_calls}")
    print("evidence:", out)
    return 0 if passed == len(checks) else 1


if __name__ == "__main__":
    sys.exit(main())
