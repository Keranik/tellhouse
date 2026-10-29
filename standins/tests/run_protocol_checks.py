#!/usr/bin/env python3
"""Stand-in protocol checks, against the frozen protocol
contract version 1.0 and the real relay.

Boots the two stand-ins, the real relay (from this tree), and
the stand-in node double (a test harness standing in for the
home node). Runs the door ceremony, the contract section 7
session open exchange, the scripted acts, the Consent valve
exercise, and the negative checks, and writes an evidence JSON.

The node double is harness code, not a home node deliverable; the
results are evidence about the stand-ins' protocol behavior and
about the stand-ins crossing the real relay. No live model API
is used anywhere in this run. Spend: $0.
"""
import base64
import hashlib
import hmac as hmac_mod
import json
import os
import secrets
import shutil
import subprocess
import sys
import time
import urllib.request
import urllib.error

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RELAY_SCRIPT = os.path.join(
    os.path.dirname(ROOT), "relay", "src", "sealed_relay.py")
RUN = "/tmp/standin-run"
DOTNET = os.path.expanduser("~/.dotnet/dotnet")
RELAY = "http://127.0.0.1:7411"
NODE = "http://127.0.0.1:7412"
DANIEL = "http://127.0.0.1:7413"
LISA = "http://127.0.0.1:7414"
ENDPOINT = "hh-demo-01"

checks = []


def check(name, ok, detail=""):
    checks.append({"name": name, "passed": bool(ok), "detail": str(detail)[:400]})
    print(("PASS " if ok else "FAIL ") + name + ((" :: " + str(detail)[:200]) if detail and not ok else ""))


def req(method, url, body=None, headers=None):
    data = None
    h = {"Content-Type": "application/json"}
    if headers:
        h.update(headers)
    if body is not None:
        data = body if isinstance(body, (bytes, str)) else json.dumps(body)
        if isinstance(data, str):
            data = data.encode()
    r = urllib.request.Request(url, data=data, headers=h, method=method)
    try:
        with urllib.request.urlopen(r, timeout=25) as resp:
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


def wait_up(url, tries=80):
    for _ in range(tries):
        s, _ = req("GET", url)
        if s and s < 500:
            return True
        time.sleep(0.5)
    return False


def wait_for(fn, timeout=30.0, interval=0.4):
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


def record():
    s, b = req("GET", NODE + "/harness/record")
    return b if isinstance(b, list) else []


def trail():
    s, b = req("GET", NODE + "/harness/trail")
    return b if isinstance(b, list) else []


def main():
    shutil.rmtree(RUN, ignore_errors=True)
    os.makedirs(RUN)
    stores = {k: os.path.join(RUN, k) for k in ("lisa", "daniel", "relay", "node")}
    for v in stores.values():
        os.makedirs(v)

    invites = [
        {"code": "invite-lisa-7f2", "person": "Lisa", "check_phrase": "maple river stone", "used": False},
        {"code": "invite-daniel-9c1", "person": "Daniel", "check_phrase": "copper field road", "used": False},
        {"code": "invite-june-3d8", "person": "June", "check_phrase": "willow gate path", "used": False},
    ]
    with open(os.path.join(stores["node"], "invites.json"), "w") as f:
        json.dump(invites, f)
    for who, inv in (("lisa", invites[0]), ("daniel", invites[1])):
        with open(os.path.join(stores[who], "pending-invite.json"), "w") as f:
            json.dump({"code": inv["code"], "check_phrase": inv["check_phrase"]}, f)

    base_env = dict(os.environ, DOTNET_ROOT=os.path.expanduser("~/.dotnet"),
                    DOTNET_CLI_TELEMETRY_OPTOUT="1")
    procs = {}
    try:
        # Stand-ins start first, alone: each generates its own
        # keypair into its own store. The driver then reads each
        # fingerprint and derives the same invite credential the
        # stand-in will derive, to write the relay registry.
        procs["daniel"] = start("daniel", [DOTNET, os.path.join(ROOT, "DanielAssistant/bin/Debug/net10.0/DanielAssistant.dll")],
                                dict(base_env, STANDIN_STORE=stores["daniel"], RELAY_URL=RELAY, NODE_URL=NODE,
                                     SELF_URL=DANIEL, ASPNETCORE_URLS=DANIEL))
        procs["lisa"] = start("lisa", [DOTNET, os.path.join(ROOT, "LisaAssistant/bin/Debug/net10.0/LisaAssistant.dll")],
                              dict(base_env, STANDIN_STORE=stores["lisa"], RELAY_URL=RELAY, NODE_URL=NODE,
                                   SELF_URL=LISA, ASPNETCORE_URLS=LISA))
        assert wait_up(LISA + "/status"), "lisa did not start"
        assert wait_up(DANIEL + "/status"), "daniel did not start"
        _, lisa_status = req("GET", LISA + "/status")
        _, daniel_status = req("GET", DANIEL + "/status")

        def credential(inv, fingerprint):
            return hkdf_hex(inv["code"].encode(),
                            ("invite:" + fingerprint).encode(),
                            b"tellhouse-door-v1")

        lisa_token = credential(invites[0], lisa_status["fingerprint"])
        daniel_token = credential(invites[1], daniel_status["fingerprint"])
        node_token = secrets.token_hex(16)

        relay_config = {"connections": [
            {"connection_id": "node-conn-01", "household_endpoint": ENDPOINT, "standing": "good",
             "role": "node", "token_sha256": hashlib.sha256(node_token.encode()).hexdigest()},
            {"connection_id": "conn-lisa-01", "household_endpoint": ENDPOINT, "standing": "good",
             "role": "member", "token_sha256": hashlib.sha256(lisa_token.encode()).hexdigest()},
            {"connection_id": "conn-daniel-01", "household_endpoint": ENDPOINT, "standing": "good",
             "role": "member", "token_sha256": hashlib.sha256(daniel_token.encode()).hexdigest()},
        ]}
        config_path = os.path.join(RUN, "relay-config.json")
        with open(config_path, "w") as f:
            json.dump(relay_config, f)
        procs["relay"] = start("relay", ["python3", RELAY_SCRIPT, "--config", config_path,
                                         "--runtime-dir", stores["relay"], "--port", "7411"],
                               dict(os.environ))
        procs["node"] = start("node", [DOTNET, os.path.join(ROOT, "MockNode/bin/Debug/net10.0/MockNode.dll")],
                              dict(base_env, NODE_STORE=stores["node"], RELAY_URL=RELAY,
                                   NODE_RELAY_TOKEN=node_token, ASPNETCORE_URLS=NODE))
        assert wait_up(RELAY + "/health"), "relay did not start"
        assert wait_up(NODE + "/harness/trail"), "node did not start"
        s, health = req("GET", RELAY + "/health")
        check("real relay up with its stated windows (session 60s, store-and-forward 300s)",
              health.get("session_ttl_seconds") == 60 and health.get("store_and_forward_ttl_seconds") == 300,
              health)

        # Door ceremony for both stand-ins.
        def join(base, inv, phrase):
            s, b = req("POST", base + "/act/join-begin")
            conn = b["connection_id"]
            s, card = req("GET", NODE + "/door/card/" + conn)
            s, bad = req("POST", NODE + "/door/confirm",
                         {"connection_id": conn, "check_phrase": "wrong phrase here", "inviter": "Margaret"})
            wrong_refused = s == 403
            s, ok = req("POST", NODE + "/door/confirm",
                        {"connection_id": conn, "check_phrase": phrase, "inviter": "Margaret"})
            s, done = req("POST", base + "/act/join-complete")
            return conn, card, wrong_refused, done

        lisa_conn, lisa_card, lisa_wrong, lisa_done = join(LISA, invites[0], "maple river stone")
        check("Lisa knock is pending until a named inviter confirms",
              lisa_card.get("standing") == "pending" and bool(lisa_card.get("fingerprint")), lisa_card)
        check("inviter confirm with a wrong check phrase refused (Lisa)", lisa_wrong)
        check("Lisa join completes only after inviter confirmation, invite disposed",
              lisa_done.get("joined") is True and lisa_done.get("invite_disposed") is True
              and not os.path.exists(os.path.join(stores["lisa"], "pending-invite.json")), lisa_done)
        check("Lisa connection id is the frozen scope id conn-lisa-01", lisa_conn == "conn-lisa-01", lisa_conn)

        daniel_conn, daniel_card, daniel_wrong, daniel_done = join(DANIEL, invites[1], "copper field road")
        check("Daniel joins through the door, invite disposed, frozen scope id",
              daniel_done.get("joined") is True and daniel_conn == "conn-daniel-01"
              and not os.path.exists(os.path.join(stores["daniel"], "pending-invite.json")), daniel_done)

        # A telling before the session opens is rejected by the
        # node (contract section 7, step 4).
        req("POST", LISA + "/trigger/test-send", {"act": "lisa-telling-1"})
        hit = wait_for(lambda: any(t["result"] == "rejected" and "session not open" in t["reason"]
                                   for t in trail()), timeout=20)
        check("session class telling before the session proof is rejected, no event",
              bool(hit) and len(record()) == 0, trail()[-2:])

        # Session open exchange, contract section 7, both stand-ins.
        def open_session(base, conn):
            s, b = req("POST", base + "/act/session-open")
            s, ans = req("POST", base + "/act/session-answer")
            opened = wait_for(lambda: (req("GET", NODE + "/harness/sessions")[1].get(conn) or {}).get("open") is True,
                              timeout=25)
            return b, ans, opened

        b, ans, opened = open_session(LISA, lisa_conn)
        check("Lisa session opens only after the challenge proof verifies",
              bool(opened) and ans.get("challenge_packet") and ans.get("proof_packet"), ans)
        b, ans, opened = open_session(DANIEL, daniel_conn)
        check("Daniel session opens only after the challenge proof verifies", bool(opened), ans)

        # Trigger discipline.
        s, b = req("POST", LISA + "/trigger/test-send",
                   {"act": "lisa-telling-1", "text": "injected words from a judge click"})
        check("trigger carrying telling content is refused whole", s == 400, b)

        # Consent valve exercise, closed then open.
        req("POST", NODE + "/admin/valve", {"connection_id": lisa_conn, "material_class": "OneLiner", "state": "closed"})
        req("POST", LISA + "/trigger/test-send", {"act": "lisa-valve"})
        hit = wait_for(lambda: any(t["result"] == "rejected" and "valve closed" in t["reason"]
                                   for t in trail()), timeout=20)
        check("packet under a closed Consent valve rejected whole, trail line, no record event (standing node Consent state: closed, class OneLiner, raw telling maps to OneLiner)",
              bool(hit) and len(record()) == 0, trail()[-2:])
        req("POST", NODE + "/admin/valve", {"connection_id": lisa_conn, "material_class": "OneLiner", "state": "open"})
        req("POST", LISA + "/trigger/test-send", {"act": "lisa-valve"})
        got = wait_for(lambda: len(record()) == 1, timeout=20)
        check("same act admitted under an open Consent valve (standing node Consent state: open, class OneLiner)",
              bool(got), record())

        # Lisa's scripted telling lands with federated provenance.
        s, sent = req("POST", LISA + "/trigger/test-send", {"act": "lisa-telling-1"})
        got = wait_for(lambda: len(record()) == 2, timeout=20)
        telling_events = [e for e in record() if "gauze pads" in e["body_text"]]
        check("Lisa telling lands on the record exactly once", bool(got) and len(telling_events) == 1, record())
        if telling_events:
            e = telling_events[0]
            check("record event provenance names origin, connection, and packet",
                  e["origin"] == "federated" and e["connection_id"] == lisa_conn
                  and e["packet_id"] == sent["packet_id"] and "reference assistant" in e["provenance"], e)

        # Canary telling, then relay blindness over the real
        # relay's runtime files and view.
        req("POST", LISA + "/trigger/test-send", {"act": "lisa-canary"})
        wait_for(lambda: len(record()) == 3, timeout=20)
        relay_blob = ""
        for fn in os.listdir(stores["relay"]):
            relay_blob += open(os.path.join(stores["relay"], fn)).read()
        check("real relay snapshot and logs hold no canary phrase, sender name, check phrase, or token",
              "velvet cactus lantern" not in relay_blob and "Lisa" not in relay_blob
              and "maple river stone" not in relay_blob and "gauze" not in relay_blob
              and lisa_token not in relay_blob)
        s, view = req("GET", RELAY + "/relay/view")
        f3 = {"packet_id", "household_endpoint", "connection_id", "envelope_class", "delivery_mode",
              "ciphertext_length", "issued_at", "ciphertext_hash", "received_at", "expires_at"}
        packets_view = view.get("packets") if isinstance(view, dict) else view
        check("relay view carries only the envelope fields plus relay timing",
              isinstance(packets_view, list)
              and all(isinstance(p, dict) and set(p.keys()) <= f3 for p in packets_view), view)

        # Replay, tamper, and bad signature, using Lisa's own sent
        # packet from her own store.
        lisa_sent = json.load(open(os.path.join(stores["lisa"], "sent.json")))
        canary_wire = next(e["wire"] for e in lisa_sent if e["act"] == "lisa-canary")
        auth = {"X-Connection-Id": lisa_conn, "X-Connection-Token": lisa_token}
        before = len(record())
        s, b = req("POST", RELAY + "/packets", json.dumps(canary_wire), headers=auth)
        check("replayed packet rejected by the relay as a duplicate", s == 409 and "duplicate" in json.dumps(b), b)
        tampered = json.loads(json.dumps(canary_wire))
        ct = tampered["ciphertext"]
        tampered["ciphertext"] = ct[:40] + ("B" if ct[40] != "B" else "C") + ct[41:]
        tampered["envelope"]["packet_id"] = "pkt-tampered-1"
        s, b = req("POST", RELAY + "/packets", json.dumps(tampered), headers=auth)
        check("bit flipped ciphertext rejected on hash agreement before any opening",
              s == 400 and "hash_mismatch" in json.dumps(b), b)
        badsig = json.loads(json.dumps(canary_wire))
        badsig["signature"] = base64.urlsafe_b64encode(b"\x00" * 64).decode().rstrip("=")
        badsig["envelope"]["packet_id"] = "pkt-badsig-1"
        s, b = req("POST", RELAY + "/packets", json.dumps(badsig), headers=auth)
        hit = wait_for(lambda: any(t["packet_id"] == "pkt-badsig-1" and t["result"] == "rejected"
                                   and "signature" in t["reason"] for t in trail()), timeout=20)
        check("packet with a non-enrolled signature is accepted by the relay on shape and rejected by the node on verification",
              s == 202 and bool(hit), (b, trail()[-2:]))
        check("negative packets created no record events", len(record()) == before)

        # Daniel receives, Lisa does not, Daniel acknowledges.
        s, b = req("POST", DANIEL + "/trigger/test-send", {"act": "daniel-receive"})
        check("Daniel trigger act runs his receive and acknowledge act", s == 200, b)
        daniel_inbox = json.load(open(os.path.join(stores["daniel"], "inbox.json")))
        admitted = {"Lisa here. The porch light bulb burned out, adding it to the house list.",
                    "Lisa here. We are out of gauze pads for Margaret, and Daniel is the one picking up groceries on Thursday.",
                    "Lisa here. Note for the house: velvet cactus lantern 417 goes on the hall shelf."}
        got_texts = {d["body_text"] for d in daniel_inbox if d.get("kind") == "delivery"}
        check("Daniel delivery bodies arrive verbatim, addressed to Daniel only",
              got_texts == admitted, got_texts)
        proposals_inbox = [d for d in daniel_inbox if d.get("kind") == "proposal"]
        check("Daniel receives the house proposal as a sealed proposal delivery",
              len(proposals_inbox) == 1 and "gauze pads" in proposals_inbox[0]["body_text"], proposals_inbox)
        acks = wait_for(lambda: (lambda a: a if len(a) == len(daniel_inbox) else None)(
            req("GET", NODE + "/harness/acks")[1]), timeout=25) or []
        check("Daniel acknowledgements recorded as reported, not observed",
              len(acks) == len(daniel_inbox) and len(acks) > 0
              and all(a["provenance"] == "reported, not observed" for a in acks)
              and all(a["from_person"] == "Daniel" for a in acks), acks)
        if proposals_inbox:
            pid = proposals_inbox[0]["packet_id"]
            prop_acks = [a for a in acks if a["acked_packet_id"] == pid]
            check("Daniel proposal acknowledgement carries the disposing disposition, not a receipt",
                  len(prop_acks) == 1 and prop_acks[0]["disposition"] == "confirmed", prop_acks)
            props = wait_for(lambda: (lambda pr: pr if pr and pr[0]["disposition"] == "confirmed" else None)(
                req("GET", NODE + "/harness/proposals")[1]), timeout=20) or []
            check("node records the proposal disposed by the addressed member acknowledgement",
                  len(props) == 1 and props[0]["disposition"] == "confirmed"
                  and props[0].get("disposed_by") == "Daniel", props)
        s, b = req("POST", LISA + "/act/poll")
        check("Lisa receives no delivery", s == 200 and b.get("opened") == 0, b)

        # Stale challenge recovery: two knocks in a row (the
        # second supersedes the first at the node, but the first
        # challenge delivery is still queued at the relay). The
        # answer hunt takes both deliveries; it must answer the
        # newest challenge, paired with the newest knock, and the
        # session must open. Answering the leftover challenge
        # mixes two exchanges' nonces and the node rejects it,
        # while the real challenge, already taken by the same
        # poll, could never be answered at all.
        def session_rec(conn):
            return req("GET", NODE + "/harness/sessions")[1].get(conn) or {}
        req("POST", LISA + "/act/session-open")
        got = wait_for(lambda: (lambda r: r if (r.get("stage") == "challenge"
                                                and r.get("server_nonce")) else None)(
                           session_rec(lisa_conn)), timeout=25)
        nonce_a = (got or {}).get("server_nonce")
        req("POST", LISA + "/act/session-open")
        wait_for(lambda: (lambda r: r if (r.get("stage") == "challenge"
                                          and r.get("server_nonce") not in (None, nonce_a)) else None)(
                     session_rec(lisa_conn)), timeout=25)
        s, ans = req("POST", LISA + "/act/session-answer")
        opened = wait_for(lambda: session_rec(lisa_conn).get("open") is True, timeout=25)
        check("a challenge left over from a superseded open attempt does not poison the next one: the answer pairs with the newest challenge and the session opens",
              bool(opened) and bool(ans.get("challenge_packet")), ans)

        # A delivery that lands behind a session challenge is
        # taken by the same destructive poll that hunts for the
        # challenge. It must be held and then acknowledged once
        # the session opens, never stored by the hunt without its
        # acknowledgement (which strands it: the house waits for
        # a receipt that never comes).
        daniel_inbox_before = {d["packet_id"] for d in
                               json.load(open(os.path.join(stores["daniel"], "inbox.json")))}
        req("POST", DANIEL + "/act/session-open")
        wait_for(lambda: session_rec(daniel_conn).get("stage") == "challenge" or None, timeout=25)
        req("POST", LISA + "/trigger/test-send", {"act": "lisa-telling-1"})
        def delivery_queued():
            s_, view_ = req("GET", RELAY + "/relay/view")
            pk = view_.get("packets") if isinstance(view_, dict) else view_
            return bool(pk) and any(p.get("connection_id") == daniel_conn
                                    and p.get("envelope_class") == "session" for p in pk)
        wait_for(delivery_queued, timeout=25)
        req("POST", DANIEL + "/act/session-answer")
        req("POST", DANIEL + "/act/poll")

        def new_delivery():
            inbox_now = json.load(open(os.path.join(stores["daniel"], "inbox.json")))
            new = [d for d in inbox_now
                   if d["packet_id"] not in daniel_inbox_before and d.get("kind") == "delivery"]
            return new or None
        new_del = wait_for(new_delivery, timeout=25) or []
        new_ids = {d["packet_id"] for d in new_del}
        acks_now = wait_for(lambda: (lambda a: a if any(
            x["acked_packet_id"] in new_ids for x in a) else None)(
            req("GET", NODE + "/harness/acks")[1]), timeout=25) or []
        check("a delivery taken during a cold session exchange is held, then acknowledged once the session opens, not stranded",
              len(new_del) >= 1
              and any(x["acked_packet_id"] in new_ids and x["from_person"] == "Daniel"
                      for x in acks_now),
              {"new_deliveries": [d["packet_id"] for d in new_del],
               "acks": [x["acked_packet_id"] for x in acks_now]})
        inbox_final = json.load(open(os.path.join(stores["daniel"], "inbox.json")))
        check("the held delivery reaches Daniel inbox exactly once",
              len(new_del) >= 1
              and all(sum(1 for d in inbox_final if d["packet_id"] == pid) == 1
                      for pid in new_ids),
              [d["packet_id"] for d in inbox_final])

        # Custody and separation.
        lisa_blob = ""
        for fn in os.listdir(stores["lisa"]):
            lisa_blob += open(os.path.join(stores["lisa"], fn)).read()
        check("Lisa store holds no record events and no plaintext telling bodies",
              "evt-" not in lisa_blob and "velvet cactus" not in lisa_blob
              and "gauze pads for Margaret" not in lisa_blob, lisa_blob[:200])
        check("stand-ins hold distinct key fingerprints in distinct stores",
              lisa_status["fingerprint"] != daniel_status["fingerprint"])
        daniel_keys = json.load(open(os.path.join(stores["daniel"], "keys.json")))
        lisa_keys = json.load(open(os.path.join(stores["lisa"], "keys.json")))
        relay_all = relay_blob + open(os.path.join(RUN, "relay-config.json")).read()
        check("no member private key appears in the relay files or the node store",
              daniel_keys["d"] not in relay_all and lisa_keys["d"] not in relay_all
              and daniel_keys["d"] not in open(os.path.join(stores["node"], "connections.json")).read())

        # Relay killed: the telling queues locally, marked
        # unrecorded, and nothing lands.
        procs["relay"].terminate()
        procs["relay"].wait(timeout=10)
        before = len(record())
        s, b = req("POST", LISA + "/trigger/test-send", {"act": "lisa-telling-1"})
        check("with the relay killed the telling queues in Lisa outbox, marked unrecorded",
              s == 202 and "unrecorded" in json.dumps(b), b)
        time.sleep(1.5)
        check("queued telling does not land while the relay is down", len(record()) == before)
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
    evidence = {"suite": "standins", "checks": checks, "passed": passed, "total": len(checks),
                "api_spend_usd": 0,
                "valve_exercise": {
                    "mapping": "Packet kind to material class mapping: raw telling to OneLiner; Consent is standing node state, no valve field in band; content classes are recorded on the node's Consents and were not checked in band",
                    "closed_leg": "standing node Consent state closed for conn-lisa-01 class OneLiner: packet rejected whole at intake, one trail line, no record event",
                    "open_leg": "standing node Consent state open for conn-lisa-01 class OneLiner: same act admitted and recorded"},
                "note": "Stand-ins and the stand-in node double run against the real relay and the frozen protocol contract vectors. The node double follows the home node's stated mappings (valve by kind, proposal disposal by acknowledgement) but is not the home node; a separate integration exercise runs the stand-ins against the real node as integration evidence."}
    out = os.path.join(ROOT, "evidence", "protocol-checks-latest.json")
    with open(out, "w") as f:
        json.dump(evidence, f, indent=2)
    print(f"CHECKS {passed} passed, {len(checks) - passed} failed, of {len(checks)}")
    print("evidence:", out)
    return 0 if passed == len(checks) else 1


if __name__ == "__main__":
    sys.exit(main())
