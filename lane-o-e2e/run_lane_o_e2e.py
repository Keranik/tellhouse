#!/usr/bin/env python3
"""the Test Send driver end-to-end proof: the website Test Send control on
the real HomeNode (Mock extraction), the real relay, and
Lisa's real stand-in.

Unlike the live leg driver, which posts straight to the stand-in's
trigger endpoint, this driver fires the node's own website control:
a form POST to the FrontDoor page's TestSend handler, exactly what a
judge's click sends. The node is configured (Demo:StandinEndpoints,
via environment) with Lisa's real trigger endpoint and her scripted
act lisa-telling-1. Assertions about the record read the node's own
SQLite database and Lisa's own store files. Mock extraction
throughout; spend $0.
"""
import hashlib
import hmac as hmac_mod
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
RUN = "/tmp/lane-o-e2e"
DOTNET = os.path.expanduser("~/.dotnet/dotnet")
RELAY = "http://127.0.0.1:7441"
NODE = "http://127.0.0.1:7442"
LISA = "http://127.0.0.1:7444"
ENDPOINT = "hh-demo-01"
NODE_TOKEN = "lane-o-e2e-node-token"

checks = []


def check(name, ok, detail=""):
    checks.append({"name": name, "passed": bool(ok), "detail": str(detail)[:500]})
    print(("PASS " if ok else "FAIL ") + name + ((" :: " + str(detail)[:300]) if not ok else ""))


def req(method, url, body=None, headers=None, form=None):
    data = None
    h = {}
    if form is not None:
        data = urllib.parse.urlencode(form).encode()
        h["Content-Type"] = "application/x-www-form-urlencoded"
    elif body is not None:
        data = body if isinstance(body, (bytes, str)) else json.dumps(body)
        if isinstance(data, str):
            data = data.encode()
        h["Content-Type"] = "application/json"
    if headers:
        h.update(headers)
    r = urllib.request.Request(url, data=data, headers=h, method=method)
    try:
        with urllib.request.urlopen(r, timeout=30) as resp:
            text = resp.read().decode()
            try:
                return resp.status, json.loads(text)
            except Exception:
                return resp.status, text
    except urllib.error.HTTPError as e:
        text = e.read().decode()
        try:
            return e.code, json.loads(text)
        except Exception:
            return e.code, text
    except Exception as e:
        return 0, str(e)


def wait_up(url, tries=90):
    for _ in range(tries):
        s, _ = req("GET", url)
        if s and s < 500:
            return True
        time.sleep(0.5)
    return False


def wait_for(fn, timeout=30.0, interval=0.5):
    end = time.time() + timeout
    last = None
    while time.time() < end:
        last = fn()
        if last:
            return last
        time.sleep(interval)
    return last


def hkdf_hex(ikm, salt, info, length=32):
    prk = hmac_mod.new(salt, ikm, hashlib.sha256).digest()
    out, t, counter = b"", b"", 1
    while len(out) < length:
        t = hmac_mod.new(prk, t + info + bytes([counter]), hashlib.sha256).digest()
        out += t
        counter += 1
    return out[:length].hex()


def start(name, argv, env, cwd=None):
    log = open(os.path.join(RUN, name + ".log"), "w")
    return subprocess.Popen(argv, env=env, cwd=cwd, stdout=log, stderr=subprocess.STDOUT)


def q(sql, args=()):
    con = sqlite3.connect(os.path.join(RUN, "node.db"))
    try:
        return con.execute(sql, args).fetchall()
    finally:
        con.close()


def main():
    shutil.rmtree(RUN, ignore_errors=True)
    os.makedirs(RUN)
    stores = {k: os.path.join(RUN, k) for k in ("lisa", "relay")}
    for v in stores.values():
        os.makedirs(v)
    base_env = dict(os.environ, DOTNET_ROOT=os.path.expanduser("~/.dotnet"),
                    DOTNET_CLI_TELEMETRY_OPTOUT="1")
    procs = {}
    cj = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))

    def website_test_send():
        # One judge click: GET the front door for the antiforgery
        # token, POST the TestSend handler, follow the redirect, and
        # read the answer the page shows.
        with opener.open(NODE + "/") as resp:
            html = resp.read().decode()
        m = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html)
        data = urllib.parse.urlencode(
            {"standInName": "LisaStandIn",
             "__RequestVerificationToken": m.group(1)}).encode()
        r = urllib.request.Request(NODE + "/?handler=TestSend", data=data, method="POST")
        with opener.open(r) as resp:
            page = resp.read().decode()
        note = re.search(r"Test Send from Agent \(([^)]*)\): ([a-z\-]+)\. Packet (pkt-[0-9a-f]+)\.", page)
        plain = re.search(r"Test Send from Agent \(([^)]*)\): ([a-z\-]+)\.", page)
        return page, note, plain

    try:
        # Node first (it seeds and serves invites), Mock extraction,
        # with Lisa's stand-in configured for the website control.
        node_env = dict(base_env,
                        ASPNETCORE_URLS=NODE, ASPNETCORE_ENVIRONMENT="Development",
                        ConnectionStrings__HomeNode=f"Data Source={RUN}/node.db",
                        HOME_NODE_KEYSTORE=os.path.join(RUN, "node-keys"),
                        Relay__BaseUrl=RELAY, Relay__NodeConnectionId="node-conn-01",
                        Extraction__Mode="Mock",
                        Demo__StandinEndpoints__0__Name="LisaStandIn",
                        Demo__StandinEndpoints__0__PersonName="Lisa",
                        Demo__StandinEndpoints__0__TriggerUrl=LISA + "/trigger/test-send",
                        Demo__StandinEndpoints__0__ActId="lisa-telling-1")
        node_env["RELAY_NODE_TOKEN"] = NODE_TOKEN
        procs["node"] = start("node", [DOTNET, os.path.join(
            ROOT, "src/HomeNode/bin/Debug/net10.0/HomeNode.dll")], node_env)
        assert wait_up(NODE + "/"), "node did not start"
        seeded = q("select count(*) from Events")[0][0]
        check("node seeds the integrated day (16 events)", seeded == 16, seeded)
        with opener.open(NODE + "/") as resp:
            front = resp.read().decode()
        check("front door renders the Test Send control for the configured stand-in",
              "Test Send from Agent: Lisa via LisaStandIn" in front)

        # Lisa's stand-in starts alone and generates its own keypair.
        procs["lisa"] = start("lisa", [DOTNET, os.path.join(
            ROOT, "standins/LisaAssistant/bin/Debug/net10.0/LisaAssistant.dll")],
            dict(base_env, STANDIN_STORE=stores["lisa"], RELAY_URL=RELAY, NODE_URL=NODE,
                 SELF_URL=LISA, ASPNETCORE_URLS=LISA))
        assert wait_up(LISA + "/status"), "lisa did not start"
        _, lisa_status = req("GET", LISA + "/status")

        # Invite issued by the node itself (Margaret invites Lisa).
        s, b = req("POST", NODE + "/door/invite",
                   {"inviter": "Margaret", "intended": "Lisa", "level": "Standard"})
        assert s == 200, b
        with open(os.path.join(stores["lisa"], "pending-invite.json"), "w") as f:
            json.dump({"code": b["code"], "check_phrase": "issued by the node door"}, f)

        def credential(code, fingerprint):
            return hkdf_hex(code.encode(), ("invite:" + fingerprint).encode(), b"tellhouse-door-v1")

        lisa_token = credential(b["code"], lisa_status["fingerprint"])
        relay_config = {"connections": [
            {"connection_id": "node-conn-01", "household_endpoint": ENDPOINT, "standing": "good",
             "role": "node", "token_sha256": hashlib.sha256(NODE_TOKEN.encode()).hexdigest()},
            {"connection_id": "conn-lisa-01", "household_endpoint": ENDPOINT, "standing": "good",
             "role": "member", "token_sha256": hashlib.sha256(lisa_token.encode()).hexdigest()},
        ]}
        config_path = os.path.join(RUN, "relay-config.json")
        with open(config_path, "w") as f:
            json.dump(relay_config, f)
        procs["relay"] = start("relay", ["python3", os.path.join(ROOT, "relay/src/sealed_relay.py"),
                                         "--config", config_path, "--runtime-dir", stores["relay"],
                                         "--port", "7441"], dict(os.environ))
        assert wait_up(RELAY + "/health"), "relay did not start"

        # Door ceremony for Lisa, against the real node.
        s, jb = req("POST", LISA + "/act/join-begin")
        conn = jb["connection_id"]
        s, card = req("GET", NODE + "/door/card/" + conn)
        s, ok = req("POST", NODE + "/door/confirm",
                    {"connection_id": conn, "check_phrase": card["check_phrase"], "inviter": "Margaret"})
        s, done = req("POST", LISA + "/act/join-complete")
        check("Lisa joins through the real door, frozen scope id, invite disposed",
              done.get("joined") is True and conn == "conn-lisa-01"
              and not os.path.exists(os.path.join(stores["lisa"], "pending-invite.json")), done)

        # Session open exchange.
        req("POST", LISA + "/act/session-open")
        s, ans = req("POST", LISA + "/act/session-answer")
        opened = wait_for(lambda: q(
            "select Verified from SessionChallenges where ConnectionId='conn-lisa-01' and Verified=1"),
            timeout=25)
        check("Lisa session opens after the challenge proof verifies on the node",
              bool(opened) and bool(ans.get("proof_packet")), ans)

        # Open Lisa's OneLiner Consent valve (standing node state).
        req("POST", NODE + "/admin/valve",
            {"connection_id": conn, "material_class": "OneLiner", "state": "open"})

        # THE WEBSITE CLICK, relay up: Lisa's telling lands once,
        # with provenance.
        page, note, plain = website_test_send()
        check("website control reports the stand-in's sent answer with its packet id",
              note is not None and note.group(2) == "sent", page[:400])
        pid = note.group(3) if note else None
        got = wait_for(lambda: q(
            "select Id, ProvenanceOrigin, ProvenanceConnection, ProvenancePacketId "
            "from Events where ProvenancePacketId=?", (pid,)), timeout=30) if pid else None
        inbox = q("select Status from PacketInbox where PacketId=?", (pid,)) if pid else []
        check("website-triggered telling lands on the node record exactly once (one Recorded inbox row)",
              bool(got) and inbox == [("Recorded",)], (got, inbox))
        if got:
            check("landed events carry provenance (origin, connection, packet id)",
                  all(e[1] == "Lisa's assistant" and e[2] == "conn-lisa-01" and e[3] == pid
                      for e in got), got)

        # THE WEBSITE CLICK, relay stopped: nothing lands, and
        # Lisa's outbox holds the telling as unrecorded.
        procs["relay"].terminate()
        procs["relay"].wait(timeout=10)
        before = q("select count(*) from Events")[0][0]
        page, note, plain = website_test_send()
        check("with the relay stopped the website control surfaces queued-unrecorded",
              plain is not None and plain.group(2) == "queued-unrecorded", page[:400])
        time.sleep(2.0)
        check("queued telling does not land while the relay is down",
              q("select count(*) from Events")[0][0] == before)
        outbox = json.load(open(os.path.join(stores["lisa"], "outbox.json")))
        check("Lisa outbox holds the telling marked unrecorded",
              len(outbox) >= 1 and all(o["record_state"] == "unrecorded" for o in outbox),
              outbox[:1])
        sent_log = json.load(open(os.path.join(stores["lisa"], "sent.json")))
        queued_rows = [r for r in sent_log if r.get("result") == "queued locally, unrecorded"]
        check("Lisa sent log records the queued, unrecorded send",
              len(queued_rows) == 1, sent_log[-2:])
    finally:
        for p in procs.values():
            try:
                p.terminate()
            except Exception:
                pass
        for p in procs.values():
            try:
                p.wait(timeout=10)
            except Exception:
                try:
                    p.kill()
                except Exception:
                    pass

    passed = sum(1 for c in checks if c["passed"])
    evidence = {"leg": "end-to-end (website Test Send control, real node Mock extraction, real relay, real Lisa stand-in)",
                "checks": checks, "passed": passed, "total": len(checks), "api_spend_usd": 0}
    out = os.path.join(ROOT, "lane-o-e2e", "lane-o-e2e-evidence.json")
    with open(out, "w") as f:
        json.dump(evidence, f, indent=2)
    print(f"CHECKS {passed} passed, {len(checks) - passed} failed, of {len(checks)}")
    print("evidence:", out)
    return 0 if passed == len(checks) else 1


if __name__ == "__main__":
    sys.exit(main())
