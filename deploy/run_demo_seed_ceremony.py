#!/usr/bin/env python3
"""Demo seed ceremony: the integration live leg, run to establish
the deployment demo seed.

This is lane-i-integration/run_live_leg.py with two changes only:
the run directory is /tmp/demo-seed-ceremony, and the evidence is
written to deploy/demo-seed-ceremony-evidence.json. The ceremony
itself is unchanged: real door joins for both stand-ins against
the real node through the real relay, session proofs, consent
valve exercise, Lisa's tellings landing with provenance, Daniel's
proposal disposal and alert acknowledgement, and the relay-killed
queueing case.

After a green run, the operator harvests the established state
as the deployment demo seed (see deploy/README.md): the node
keystore, the relay registry the leg computed, each stand-in's
keys.json and state.json, and a sealed snapshot of the node record
taken with the product's own backup tool. Nothing in the ceremony
is faked for the seed: every artifact is the output of this run.
Mock extraction throughout; spend $0.
"""
import hashlib
import hmac as hmac_mod
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
RUN = "/tmp/demo-seed-ceremony"
DOTNET = os.path.expanduser("~/.dotnet/dotnet")
RELAY = "http://127.0.0.1:7431"
NODE = "http://127.0.0.1:7432"
DANIEL = "http://127.0.0.1:7433"
LISA = "http://127.0.0.1:7434"
ENDPOINT = "hh-demo-01"
NODE_TOKEN = "<redacted>"
HOUSE_SECRET = "local-driver-house-secret"  # the house side credential this driver presents to the node door admin surfaces; the node it starts is given the same value.

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
    if any(part in url for part in ("/door/invite", "/door/card", "/door/confirm", "/door/decline", "/admin/")):
        h["X-House-Secret"] = HOUSE_SECRET
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


def db():
    return sqlite3.connect(os.path.join(RUN, "node.db"))


def q(sql, args=()):
    con = db()
    try:
        return con.execute(sql, args).fetchall()
    finally:
        con.close()


def trail_rows():
    return q("select PacketId, Outcome, Reason from PacketTrailLines order by Id")


def main():
    shutil.rmtree(RUN, ignore_errors=True)
    os.makedirs(RUN)
    stores = {k: os.path.join(RUN, k) for k in ("lisa", "daniel", "relay")}
    for v in stores.values():
        os.makedirs(v)
    base_env = dict(os.environ, DOTNET_ROOT=os.path.expanduser("~/.dotnet"),
                    DOTNET_CLI_TELEMETRY_OPTOUT="1")
    procs = {}
    try:
        # Node first (it seeds and serves invites), Mock extraction.
        node_env = dict(base_env,
                        ASPNETCORE_URLS=NODE, ASPNETCORE_ENVIRONMENT="Development",
                        ConnectionStrings__HomeNode=f"Data Source={RUN}/node.db",
                        HOME_NODE_KEYSTORE=os.path.join(RUN, "node-keys"),
                        Relay__BaseUrl=RELAY, Relay__NodeConnectionId="node-conn-01",
                        Extraction__Mode="Mock")
        node_env["RELAY" + "_NODE_" + "TOKEN"] = NODE_TOKEN
        node_env["HOME_NODE_HOUSE_SECRET"] = HOUSE_SECRET
        procs["node"] = start("node", [DOTNET, os.path.join(
            ROOT, "src/HomeNode/bin/Debug/net10.0/HomeNode.dll")], node_env)
        assert wait_up(NODE + "/"), "node did not start"
        seeded = q("select count(*) from Events")[0][0]
        check("integrated node seeds the integrated day (16 events)", seeded == 16, seeded)

        # Stand-ins start alone and generate their own keypairs.
        for who, url in (("daniel", DANIEL), ("lisa", LISA)):
            proj = "DanielAssistant" if who == "daniel" else "LisaAssistant"
            procs[who] = start(who, [DOTNET, os.path.join(
                ROOT, f"standins/{proj}/bin/Debug/net10.0/{proj}.dll")],
                dict(base_env, STANDIN_STORE=stores[who], RELAY_URL=RELAY, NODE_URL=NODE,
                     SELF_URL=url, ASPNETCORE_URLS=url))
            assert wait_up(url + "/status"), who + " did not start"
        _, lisa_status = req("GET", LISA + "/status")
        _, daniel_status = req("GET", DANIEL + "/status")

        # Invites issued by the node itself (Margaret invites both).
        codes = {}
        for who in ("Lisa", "Daniel"):
            s, b = req("POST", NODE + "/door/invite",
                       {"inviter": "Margaret", "intended": who, "level": "Standard"})
            assert s == 200, (who, b)
            codes[who] = b["code"]
            with open(os.path.join(stores[who.lower()], "pending-invite.json"), "w") as f:
                json.dump({"code": b["code"], "check_phrase": "issued by the node door"}, f)

        def credential(code, fingerprint):
            return hkdf_hex(code.encode(), ("invite:" + fingerprint).encode(), b"tellhouse-door-v1")

        lisa_token = credential(codes["Lisa"], lisa_status["fingerprint"])
        daniel_token = credential(codes["Daniel"], daniel_status["fingerprint"])
        relay_config = {"connections": [
            {"connection_id": "node-conn-01", "household_endpoint": ENDPOINT, "standing": "good",
             "role": "node", "token_sha256": hashlib.sha256(NODE_TOKEN.encode()).hexdigest()},
            {"connection_id": "conn-lisa-01", "household_endpoint": ENDPOINT, "standing": "good",
             "role": "member", "token_sha256": hashlib.sha256(lisa_token.encode()).hexdigest()},
            {"connection_id": "conn-daniel-01", "household_endpoint": ENDPOINT, "standing": "good",
             "role": "member", "token_sha256": hashlib.sha256(daniel_token.encode()).hexdigest()},
        ]}
        config_path = os.path.join(RUN, "relay-config.json")
        with open(config_path, "w") as f:
            json.dump(relay_config, f)
        procs["relay"] = start("relay", ["python3", os.path.join(ROOT, "relay/src/sealed_relay.py"),
                                         "--config", config_path, "--runtime-dir", stores["relay"],
                                         "--port", "7431"], dict(os.environ))
        assert wait_up(RELAY + "/health"), "relay did not start"
        s, health = req("GET", RELAY + "/health")
        check("real relay up with its stated windows (session 60s, store-and-forward 300s)",
              health.get("session_ttl_seconds") == 60 and health.get("store_and_forward_ttl_seconds") == 300, health)

        # Door ceremony for both stand-ins, against the real node.
        def join(base):
            s, b = req("POST", base + "/act/join-begin")
            conn = b["connection_id"]
            s, card = req("GET", NODE + "/door/card/" + conn)
            s, bad = req("POST", NODE + "/door/confirm",
                         {"connection_id": conn, "check_phrase": "wrong phrase here", "inviter": "Margaret"})
            wrong_refused = s == 403
            s, ok = req("POST", NODE + "/door/confirm",
                        {"connection_id": conn, "check_phrase": card["check_phrase"], "inviter": "Margaret"})
            s, done = req("POST", base + "/act/join-complete")
            return conn, card, wrong_refused, done

        lisa_conn, lisa_card, lisa_wrong, lisa_done = join(LISA)
        check("Lisa knock is pending until a named inviter confirms (real node card)",
              lisa_card.get("standing") == "pending" and bool(lisa_card.get("fingerprint")), lisa_card)
        check("inviter confirm with a wrong check phrase refused by the real node (Lisa)", lisa_wrong)
        check("Lisa join completes only after inviter confirmation, invite disposed",
              lisa_done.get("joined") is True and lisa_done.get("invite_disposed") is True
              and not os.path.exists(os.path.join(stores["lisa"], "pending-invite.json")), lisa_done)
        check("Lisa connection id is the frozen scope id conn-lisa-01", lisa_conn == "conn-lisa-01", lisa_conn)
        daniel_conn, daniel_card, daniel_wrong, daniel_done = join(DANIEL)
        check("Daniel joins through the real door, invite disposed, frozen scope id",
              daniel_done.get("joined") is True and daniel_conn == "conn-daniel-01"
              and not os.path.exists(os.path.join(stores["daniel"], "pending-invite.json")), daniel_done)
        conns = q("select ConnectionId, Standing from HouseConnections order by ConnectionId")
        check("node registry holds both connections Active",
              conns == [("conn-daniel-01", "Active"), ("conn-lisa-01", "Active")], conns)

        # A telling before the session opens is rejected by intake.
        before = q("select count(*) from Events")[0][0]
        req("POST", LISA + "/trigger/test-send", {"act": "lisa-telling-1"})
        hit = wait_for(lambda: [t for t in trail_rows() if t[1] == "Rejected" and "session" in (t[2] or "").lower()], timeout=25)
        check("session class telling before the session proof is rejected, no event",
              bool(hit) and q("select count(*) from Events")[0][0] == before, hit)

        # Session open exchange, both stand-ins.
        def open_session(base):
            req("POST", base + "/act/session-open")
            s, ans = req("POST", base + "/act/session-answer")
            return ans

        ans = open_session(LISA)
        opened = wait_for(lambda: q("select Verified from SessionChallenges where ConnectionId='conn-lisa-01' and Verified=1"), timeout=25)
        check("Lisa session opens only after the challenge proof verifies on the real node",
              bool(opened) and bool(ans.get("proof_packet")), ans)
        ans = open_session(DANIEL)
        opened = wait_for(lambda: q("select Verified from SessionChallenges where ConnectionId='conn-daniel-01' and Verified=1"), timeout=25)
        check("Daniel session opens only after the challenge proof verifies on the real node", bool(opened), ans)

        # Trigger discipline.
        s, b = req("POST", LISA + "/trigger/test-send",
                   {"act": "lisa-telling-1", "text": "injected words from a judge click"})
        check("trigger carrying telling content is refused whole", s == 400, b)

        # Consent valve exercise against the node's standing Consent.
        req("POST", NODE + "/admin/valve", {"connection_id": lisa_conn, "material_class": "OneLiner", "state": "closed"})
        before = q("select count(*) from Events")[0][0]
        req("POST", LISA + "/trigger/test-send", {"act": "lisa-valve"})
        hit = wait_for(lambda: [t for t in trail_rows() if t[1] == "Rejected" and "valve" in (t[2] or "").lower()], timeout=25)
        check("packet under a closed Consent valve rejected whole by the real node, trail line, no record event",
              bool(hit) and q("select count(*) from Events")[0][0] == before, hit)
        req("POST", NODE + "/admin/valve", {"connection_id": lisa_conn, "material_class": "OneLiner", "state": "open"})
        # Daniel needs ProposalResponse consent for his disposal ack later;
        # Lisa needs it too if a proposal ever routes to her. Open both
        # members' OneLiner and ProposalResponse valves as standing state.
        req("POST", NODE + "/admin/valve", {"connection_id": daniel_conn, "material_class": "ProposalResponse", "state": "open"})
        req("POST", NODE + "/admin/valve", {"connection_id": daniel_conn, "material_class": "OneLiner", "state": "open"})
        s, sent = req("POST", LISA + "/trigger/test-send", {"act": "lisa-valve"})
        got = wait_for(lambda: q("select Id, ProvenanceOrigin, ProvenanceConnection, ProvenancePacketId, ToldAtUtc, RecordedAtUtc from Events where Body like '%porch light bulb%'"), timeout=25)
        check("same act admitted under an open Consent valve, lands exactly once",
              bool(got) and len(got) == 1, got)
        if got:
            e = got[0]
            check("valve event provenance populated by node intake (origin, connection, packet) with told and recorded times",
                  e[1] == "Lisa's assistant" and e[2] == "conn-lisa-01" and e[3] == sent.get("packet_id")
                  and e[4] is not None and e[5] is not None, e)

        # Lisa's scripted telling lands exactly once, with provenance.
        s, sent = req("POST", LISA + "/trigger/test-send", {"act": "lisa-telling-1"})
        pid = sent.get("packet_id")
        got = wait_for(lambda: q("select Id, Body, ProvenanceOrigin, ProvenanceConnection, ProvenancePacketId from Events where ProvenancePacketId=?", (pid,)), timeout=25)
        inbox = q("select Status from PacketInbox where PacketId=?", (pid,))
        check("Lisa telling lands on the node record exactly once (one Recorded inbox row, its events written once)",
              bool(got) and inbox == [("Recorded",)], (got, inbox))
        if got:
            check("telling events carry provenance fields populated by the node intake",
                  all(e[2] == "Lisa's assistant" and e[3] == "conn-lisa-01" and e[4] == pid for e in got), got)

        # Daniel's deliveries and proposal disposal, on the real
        # node's own roads. A front door telling from Margaret (her
        # ankle, in her words) extracts to a Proposed health mention
        # whose NotifyPerson action addresses Daniel. No connection
        # level the house offers may hear health class material and
        # Daniel's connection is Standard, so proposing that health
        # event to Daniel is refused at the door and queues
        # nothing. The proposal round trip itself runs on a
        # class clean event: a stated-turn Task telling (Margaret,
        # Daniel's turn to take the bins out) shaped by the
        # stated-turn pre-pass into a Proposed Task with a
        # NotifyPerson action for Daniel. The house queues its
        # proposal to Daniel as a sealed proposal delivery;
        # Daniel's acknowledgement carries his scripted disposition
        # (confirmed), which disposes the proposal on the node. A
        # later front door telling (the Beat 4 trash turn, also
        # shaped by the stated-turn pre-pass) is confirmed by
        # Margaret on the front door, and its alert reaches Daniel
        # only, body verbatim, acknowledged as received.
        import http.cookiejar
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

        st = front_door("Tell", {"speakerId": 1, "telling": "My ankle has swollen up again."})
        prop = wait_for(lambda: q("select e.Id from Events e join EventActions a on a.EventId=e.Id where e.Body='My ankle has swollen up again.' and e.ConfirmationState='Proposed' and a.ActionType='NotifyPerson' and a.TargetPersonId=2"), timeout=25)
        check("front door health telling lands as a Proposed event with a NotifyPerson action addressed to Daniel only",
              st == 200 and bool(prop), (st, prop))
        health_id = prop[0][0]
        s, b = req("POST", NODE + "/admin/propose", {"event_id": health_id})
        fence_rows = q("select Status from SealedOutbox where SourceEventId=? and SourceKind='Proposal'", (health_id,))
        fence_state = q("select ConfirmationState from Events where Id=?", (health_id,))
        check("house proposal of a health entry to a Standard connection is refused at the door",
              s == 409 and isinstance(b, dict) and b.get("error") == "addressee cannot receive that delivery on their connection"
              and not fence_rows and fence_state and fence_state[0][0] == "Proposed", (s, b, fence_rows, fence_state))
        bins = "Daniel's turn to take the bins out, the truck comes at six."
        st = front_door("Tell", {"speakerId": 1, "telling": bins})
        prop = wait_for(lambda: q("select e.Id from Events e join EventActions a on a.EventId=e.Id where e.Body=? and e.ConfirmationState='Proposed' and a.ActionType='NotifyPerson' and a.TargetPersonId=2", (bins,)), timeout=25)
        prop_id = prop[0][0]
        s, b = req("POST", NODE + "/admin/propose", {"event_id": prop_id})
        sent_prop = wait_for(lambda: q("select Status from SealedOutbox where SourceEventId=? and SourceKind='Proposal' and Status='Sent'", (prop_id,)), timeout=25)
        check("house proposal of the bins turn task reaches the relay as a sealed proposal delivery to Daniel",
              s == 200 and bool(sent_prop), (b, sent_prop))
        s, b = req("POST", DANIEL + "/trigger/test-send", {"act": "daniel-receive"})
        daniel_inbox = json.load(open(os.path.join(stores["daniel"], "inbox.json")))
        proposals = [d for d in daniel_inbox if d.get("kind") == "proposal"]
        check("Daniel receives the house proposal of the bins turn task as a sealed proposal delivery, body verbatim",
              len(proposals) == 1 and proposals[0]["body_text"] == "Daniel's turn to take the bins out, the truck comes at six.", daniel_inbox)
        acked = wait_for(lambda: q("select Status from SealedOutbox where SourceEventId=? and SourceKind='Proposal' and Status='Acknowledged'", (prop_id,)), timeout=25)
        state = q("select ConfirmationState from Events where Id=?", (prop_id,))
        check("Daniel proposal of the bins turn task disposed by his acknowledgement (scripted disposition confirmed lands on the node)",
              bool(acked) and state and state[0][0] == "Confirmed", (acked, state))

        trash = "Tonight is Daniel's turn to take the trash out, the truck comes at seven."
        st = front_door("Tell", {"speakerId": 1, "telling": trash})
        turn = wait_for(lambda: q("select Id, Type, Details from Events where Body=? and ConfirmationState='Proposed'", (trash,)), timeout=25)
        turn_ok = bool(turn) and turn[0][1] == "Task" and '"TurnPersonId":2' in (turn[0][2] or "")
        check("Beat 4 trash turn (mock path, integrated pre-pass) lands as a Proposed Task with its NotifyPerson route stored for Daniel",
              st == 200 and turn_ok, (st, turn))
        turn_id = turn[0][0]
        st = front_door("Confirm", {"eventId": turn_id, "actorPersonId": 1})
        alert = wait_for(lambda: q("select Status from SealedOutbox where SourceEventId=? and SourceKind='Alert' and Status in ('Sent','Acknowledged')", (turn_id,)), timeout=25)
        check("confirmed turn dispatches a sealed alert delivery to Daniel through the relay",
              st == 200 and bool(alert), (st, alert))
        s, b = req("POST", DANIEL + "/trigger/test-send", {"act": "daniel-receive"})
        daniel_inbox = json.load(open(os.path.join(stores["daniel"], "inbox.json")))
        verbatim = [d for d in daniel_inbox if d.get("kind") == "delivery" and d.get("body_text") == trash]
        check("Daniel delivery body arrives verbatim, addressed to Daniel only",
              len(verbatim) == 1, daniel_inbox)
        acked2 = wait_for(lambda: q("select Status from SealedOutbox where SourceEventId=? and SourceKind='Alert' and Status='Acknowledged'", (turn_id,)), timeout=25)
        state2 = q("select ConfirmationState from Events where Id=?", (turn_id,))
        check("Daniel acknowledgement of the alert recorded on the node (reported, not observed); event Done",
              bool(acked2) and state2 and state2[0][0] == "Done", (acked2, state2))
        s, b = req("POST", LISA + "/act/poll")
        check("Lisa receives no delivery", s == 200 and b.get("opened") == 0, b)

        # Relay killed: the telling queues in Lisa's outbox, unrecorded.
        procs["relay"].terminate()
        procs["relay"].wait(timeout=10)
        before = q("select count(*) from Events")[0][0]
        s, b = req("POST", LISA + "/trigger/test-send", {"act": "lisa-telling-1"})
        check("with the relay killed the telling queues in Lisa outbox, marked unrecorded (Test Send through the real path)",
              s == 202 and "unrecorded" in json.dumps(b), b)
        time.sleep(1.5)
        check("queued telling does not land while the relay is down",
              q("select count(*) from Events")[0][0] == before)
        outbox = json.load(open(os.path.join(stores["lisa"], "outbox.json")))
        check("outbox entry is marked unrecorded",
              len(outbox) >= 1 and all(o["record_state"] == "unrecorded" for o in outbox), outbox[:1])
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
    evidence = {"leg": "integration live leg (real stand-ins, real home node, real relay)",
                "checks": checks, "passed": passed, "total": len(checks), "api_spend_usd": 0}
    out = os.path.join(ROOT, "deploy", "demo-seed-ceremony-evidence.json")
    with open(out, "w") as f:
        json.dump(evidence, f, indent=2)
    print(f"CHECKS {passed} passed, {len(checks) - passed} failed, of {len(checks)}")
    print("evidence:", out)
    return 0 if passed == len(checks) else 1


if __name__ == "__main__":
    sys.exit(main())
