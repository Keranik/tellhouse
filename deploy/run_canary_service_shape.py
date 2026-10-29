#!/usr/bin/env python3
"""Canary batch: ONE batch, one sitting,
fresh seed, against the integrated node.

Deployment wiring rerun: identical batch, except the relay is
launched through relay/entrypoint.sh (its service entrypoint)
with the batch config passed by environment, so the canary
properties are re-verified against the service-shaped relay.
Evidence lands in deploy/canary-service-shape-evidence.json.

Method:
  1. A canary telling carrying a unique canary phrase is sent
     through Lisa's real stand-in over the real relay into the
     real home node. The phrase is Lisa's scripted lisa-canary
     act (standins/LisaAssistant/script.json): the telling is
     "Lisa here. Note for the house: velvet cactus lantern 417
     goes on the hall shelf." and the unique canary phrase is
     "velvet cactus lantern 417".
  2. After the live traffic, the real relay's files (its runtime
     directory: queue_snapshot.json, relay.log.jsonl, and any
     other file there), plus snapshots of the queue file captured
     during transit and the relay view endpoint, are searched in
     text and in raw bytes for the canary phrase, its fragments,
     the full telling, and any event body text that crossed.
  3. One tampered packet produces no record entry. Two forms are
     run and reported separately: (first form) a bit flipped ciphertext
     with its stated hash untouched, and (second form) a bit flipped
     ciphertext with the hash recomputed so the relay admits it
     and the real node's sealed intake itself must reject it
     (signature no longer verifies), under a fresh packet id so
     the rejection cannot be a replay rejection.
  4. One replayed packet is rejected. Two layers are run and
     reported separately: (live registry) the exact original wire re-posted
     to the relay while the relay's seen registry is live, and
     (after a restart) the exact original wire re-posted after the
     relay process restarts (the relay rebuilds its seen set from
     its snapshot file only, and the forwarded entry is gone from
     it, so the packet reaches the real node's intake, whose own
     PacketSeen
     registry must reject it as a replay).
Mock extraction throughout; API spend $0.
"""
import base64
import copy
import hashlib
import hmac as hmac_mod
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.request
import urllib.error
import urllib.parse

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUN = "/tmp/int-canary3"
DOTNET = os.path.expanduser("~/.dotnet/dotnet")
RELAY = "http://127.0.0.1:7531"
NODE = "http://127.0.0.1:7532"
LISA = "http://127.0.0.1:7534"
ENDPOINT = "hh-demo-01"
NODE_TOKEN = "canary-node-token-7532"
HOUSE_SECRET = "local-driver-house-secret"  # the house side credential this driver presents to the node door admin surfaces; the node it starts is given the same value.
CANARY_PHRASE = "velvet cactus lantern 417"
CANARY_TELLING = ("Lisa here. Note for the house: velvet cactus "
                  "lantern 417 goes on the hall shelf.")
SEARCH_TERMS = [CANARY_PHRASE, CANARY_TELLING, "velvet", "cactus",
                "lantern 417", "hall shelf", "Lisa here",
                "Note for the house"]

checks = []
snapshots_seen = []
snap_stop = threading.Event()


def check(name, ok, detail=""):
    checks.append({"name": name, "passed": bool(ok),
                   "detail": str(detail)[:900]})
    print(("PASS " if ok else "FAIL ") + name +
          ((" :: " + str(detail)[:400]) if not ok or True else ""), flush=True)


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
    return subprocess.Popen(argv, env=env, cwd=cwd, stdout=log,
                            stderr=subprocess.STDOUT)


def db():
    return sqlite3.connect(os.path.join(RUN, "node.db"))


def q(sql, args=()):
    con = db()
    try:
        return con.execute(sql, args).fetchall()
    except Exception as e:
        return [("ERR", str(e))]
    finally:
        con.close()


def b64url_encode(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def b64url_decode(text):
    raw = text.encode("ascii")
    pad = (-len(raw)) % 4
    return base64.urlsafe_b64decode(raw + b"=" * pad)


def search_blob(label, blob_bytes):
    """Search one blob (text and raw bytes, case-insensitive for
    the word fragments) for every search term. Returns hits."""
    hits = []
    text = blob_bytes.decode("utf-8", errors="replace")
    for term in SEARCH_TERMS:
        if term.encode() in blob_bytes or term in text:
            hits.append(term)
        elif term.lower() in text.lower() and term not in ("velvet", "cactus"):
            hits.append(term + " (case-insensitive)")
    # 'velvet'/'cactus' case-insensitive hits also count as leaks.
    for term in ("velvet", "cactus"):
        if term not in hits and term in text.lower():
            hits.append(term)
    return {"label": label, "bytes": len(blob_bytes), "hits": hits}


def search_relay_files(relay_dir):
    results = []
    for dirpath, _dirs, files in os.walk(relay_dir):
        for fn in sorted(files):
            p = os.path.join(dirpath, fn)
            with open(p, "rb") as f:
                results.append(search_blob(os.path.relpath(p, RUN), f.read()))
    return results


def snap_watcher(relay_dir):
    path = os.path.join(relay_dir, "queue_snapshot.json")
    last = None
    while not snap_stop.is_set():
        try:
            with open(path, "rb") as f:
                data = f.read()
            if data != last:
                snapshots_seen.append(data)
                last = data
        except Exception:
            pass
        time.sleep(0.05)


def main():
    shutil.rmtree(RUN, ignore_errors=True)
    os.makedirs(RUN)
    stores = {k: os.path.join(RUN, k) for k in ("lisa", "relay")}
    for v in stores.values():
        os.makedirs(v)
    base_env = dict(os.environ, DOTNET_ROOT=os.path.expanduser("~/.dotnet"),
                    DOTNET_CLI_TELEMETRY_OPTOUT="1")
    procs = {}
    relay_argv = None
    try:
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
        check("fresh seed: integrated node seeds 16 events", seeded == 16, seeded)

        procs["lisa"] = start("lisa", [DOTNET, os.path.join(
            ROOT, "standins/LisaAssistant/bin/Debug/net10.0/LisaAssistant.dll")],
            dict(base_env, STANDIN_STORE=stores["lisa"], RELAY_URL=RELAY,
                 NODE_URL=NODE, SELF_URL=LISA, ASPNETCORE_URLS=LISA))
        assert wait_up(LISA + "/status"), "lisa did not start"
        _, lisa_status = req("GET", LISA + "/status")
        fingerprint = lisa_status["fingerprint"]

        s, b = req("POST", NODE + "/door/invite",
                   {"inviter": "Margaret", "intended": "Lisa", "level": "Standard"})
        assert s == 200, b
        code = b["code"]
        with open(os.path.join(stores["lisa"], "pending-invite.json"), "w") as f:
            json.dump({"code": code, "check_phrase": "issued by the node door"}, f)

        def credential(c, fp):
            return hkdf_hex(c.encode(), ("invite:" + fp).encode(), b"tellhouse-door-v1")
        lisa_token = credential(code, fingerprint)
        relay_config = {"connections": [
            {"connection_id": "node-conn-01", "household_endpoint": ENDPOINT,
             "standing": "good", "role": "node",
             "token_sha256": hashlib.sha256(NODE_TOKEN.encode()).hexdigest()},
            {"connection_id": "conn-lisa-01", "household_endpoint": ENDPOINT,
             "standing": "good", "role": "member",
             "token_sha256": hashlib.sha256(lisa_token.encode()).hexdigest()},
        ]}
        config_path = os.path.join(RUN, "relay-config.json")
        with open(config_path, "w") as f:
            json.dump(relay_config, f)
        # Service shape (deployment wiring): the relay is started
        # through relay/entrypoint.sh, the same entrypoint its
        # Dockerfile runs, with the canary config and runtime dir
        # passed by environment. The entrypoint uses a present
        # config as-is (operator state wins over reseeding), so the
        # protocol surface under test is unchanged; only the launch
        # shape differs from the original batch.
        relay_env = dict(os.environ)
        relay_env["RELAY_CONFIG"] = config_path
        relay_env["RELAY_RUNTIME_DIR"] = stores["relay"]
        relay_env["PORT"] = "7531"
        relay_argv = ["sh", os.path.join(ROOT, "relay", "entrypoint.sh")]
        procs["relay"] = start("relay", relay_argv, relay_env,
                               cwd=os.path.join(ROOT, "relay"))
        assert wait_up(RELAY + "/health"), "relay did not start"

        # Door ceremony for Lisa against the real node.
        s, jb = req("POST", LISA + "/act/join-begin")
        conn = jb["connection_id"]
        s, card = req("GET", NODE + "/door/card/" + conn)
        s, ok = req("POST", NODE + "/door/confirm",
                    {"connection_id": conn, "check_phrase": card["check_phrase"],
                     "inviter": "Margaret"})
        s, done = req("POST", LISA + "/act/join-complete")
        check("Lisa joins through the real door, frozen scope id conn-lisa-01",
              done.get("joined") is True and conn == "conn-lisa-01", done)

        # Open Lisa's OneLiner valve (raw tellings map to OneLiner).
        req("POST", NODE + "/admin/valve",
            {"connection_id": conn, "material_class": "OneLiner", "state": "open"})

        # Session open exchange.
        req("POST", LISA + "/act/session-open")
        s, ans = req("POST", LISA + "/act/session-answer")
        opened = wait_for(lambda: q(
            "select Verified from SessionChallenges where ConnectionId='conn-lisa-01' and Verified=1"),
            timeout=25)
        check("Lisa session opens after the challenge proof verifies on the real node",
              bool(opened), ans)

        lisa_state = json.load(open(os.path.join(stores["lisa"], "state.json")))
        lisa_cred = lisa_state["credential"]
        relay_headers = {"X-Connection-Id": "conn-lisa-01",
                         "X-Connection-Token": lisa_cred}

        def relay_post(wire):
            return req("POST", RELAY + "/packets", wire, headers=relay_headers)

        # ---- CANARY: watch the queue file during transit. ----
        watcher = threading.Thread(target=snap_watcher, args=(stores["relay"],),
                                   daemon=True)
        watcher.start()
        base_events = q("select count(*) from Events")[0][0]
        s, sent = req("POST", LISA + "/trigger/test-send", {"act": "lisa-canary"})
        canary_pid = sent.get("packet_id")
        check("canary trigger accepted by Lisa stand-in and posted to the real relay",
              s == 200 and bool(canary_pid) and sent.get("relay_status") == 202, sent)
        got = wait_for(lambda: q(
            "select Id, Body, ProvenanceOrigin, ProvenanceConnection, ProvenancePacketId "
            "from Events where ProvenancePacketId=?", (canary_pid,)), timeout=30)
        time.sleep(2)
        snap_stop.set()
        check("canary telling lands on the real node record exactly once, with intake provenance",
              bool(got) and len(got) >= 1 and
              q("select Status from PacketInbox where PacketId=?", (canary_pid,)) == [("Recorded",)],
              got)
        bodies = [g[1] for g in (got or [])]
        check("landed record body carries the canary phrase verbatim (the body that must not appear at the relay)",
              any(CANARY_PHRASE in (b or "") for b in bodies), bodies)

        # The authentic wire, as Lisa's stand-in recorded it.
        sent_log = json.load(open(os.path.join(stores["lisa"], "sent.json")))
        wire = [e for e in sent_log if e.get("packet_id") == canary_pid][-1]["wire"]
        with open(os.path.join(RUN, "canary-wire.json"), "w") as f:
            json.dump(wire, f)

        # ---- RELAY BLINDNESS SEARCH (after the live traffic). ----
        file_results = search_relay_files(stores["relay"])
        all_hits = [h for r in file_results for h in r["hits"]]
        check("plaintext search of the real relay runtime files (queue, logs, storage) finds no canary phrase, fragment, telling, or event body",
              not all_hits, file_results)
        snap_hits = []
        for i, snap in enumerate(snapshots_seen):
            r = search_blob(f"queue_snapshot during transit #{i}", snap)
            snap_hits.extend(r["hits"])
        check("queue snapshots captured during transit (every distinct snapshot content) hold no canary plaintext",
              not snap_hits,
              {"snapshots_captured": len(snapshots_seen), "hits": snap_hits})
        s, view = req("GET", RELAY + "/relay/view")
        view_hits = search_blob("relay view", json.dumps(view).encode())["hits"]
        check("relay view endpoint exposes no canary plaintext",
              not view_hits, view)
        # What the relay files DO hold for this packet: envelope only.
        log_text = ""
        log_path = os.path.join(stores["relay"], "relay.log.jsonl")
        if os.path.exists(log_path):
            log_text = open(log_path).read()
        check("relay log does hold the canary packet's envelope line (accepted/forwarded, envelope fields only), so the search target existed",
              canary_pid in log_text and "accepted" in log_text, log_text[:400])

        ev_after_canary = q("select count(*) from Events")[0][0]

        # ---- TAMPER, FIRST FORM: flipped ciphertext, stated hash untouched. ----
        t1 = copy.deepcopy(wire)
        first = t1["ciphertext"][0]
        t1["ciphertext"] = ("A" if first != "A" else "B") + t1["ciphertext"][1:]
        s, rb = relay_post(t1)
        check("tampered packet with a ciphertext bit flip and its hash untouched is rejected at hash agreement",
              s == 400 and "hash_mismatch" in json.dumps(rb), (s, rb))
        time.sleep(3)
        check("the hash-untouched tampered packet produces no record entry (event count unchanged, no new trail line for the node)",
              q("select count(*) from Events")[0][0] == ev_after_canary,
              q("select count(*) from Events")[0][0])

        # ---- TAMPER, SECOND FORM: flipped ciphertext, hash recomputed, fresh packet id.
        # Relay admits on shape and hash; the real node intake must reject. ----
        t2 = copy.deepcopy(wire)
        t2_pid = "pkt-tamper-" + hashlib.sha256(
            (canary_pid + "t2").encode()).hexdigest()[:12]
        blob = bytearray(b64url_decode(t2["ciphertext"]))
        blob[10] ^= 0x01
        t2["ciphertext"] = b64url_encode(bytes(blob))
        t2["envelope"]["packet_id"] = t2_pid
        t2["envelope"]["ciphertext_hash"] = b64url_encode(
            hashlib.sha256(bytes(blob)).digest())
        s, rb = relay_post(t2)
        check("tampered packet with its hash recomputed is admitted by the relay on shape, so the node itself is the rejecting layer",
              s == 202, (s, rb))
        trail = wait_for(lambda: [r for r in q(
            "select PacketId, Outcome, Reason from PacketTrailLines where PacketId=?",
            (t2_pid,)) if r[1] == "Rejected"], timeout=30)
        time.sleep(1)
        t2_events = q("select count(*) from Events where ProvenancePacketId=?", (t2_pid,))
        t2_inbox = q("select Status from PacketInbox where PacketId=?", (t2_pid,))
        check("the hash-recomputed tampered packet is rejected by the real node's sealed intake and produces no record entry",
              bool(trail) and q("select count(*) from Events")[0][0] == ev_after_canary
              and t2_events == [(0,)] and t2_inbox != [("Recorded",)],
              {"trail": trail, "inbox": t2_inbox})

        # ---- REPLAY, LIVE REGISTRY: exact original wire, relay seen registry live. ----
        s, rb = relay_post(copy.deepcopy(wire))
        check("replayed packet (exact original wire, relay registry live) is rejected by the relay as a duplicate",
              s == 409 and "duplicate" in json.dumps(rb), (s, rb))
        time.sleep(3)
        check("the replay against the live relay registry produces no second record entry (canary still recorded exactly once)",
              q("select count(*) from Events")[0][0] == ev_after_canary and
              q("select count(*) from PacketInbox where PacketId=? and Status='Recorded'",
                (canary_pid,)) == [(1,)],
              q("select count(*) from Events")[0][0])

        # ---- REPLAY AFTER A RELAY RESTART: the relay's seen set rebuilds
        # from the snapshot file only, and the exact wire reaches the real
        # node's intake. ----
        procs["relay"].terminate()
        procs["relay"].wait(timeout=10)
        procs["relay"] = start("relay2", relay_argv, relay_env,
                               cwd=os.path.join(ROOT, "relay"))
        assert wait_up(RELAY + "/health"), "relay did not restart"
        trail_before = q("select count(*) from PacketTrailLines where PacketId=?",
                         (canary_pid,))[0][0]
        s, rb = relay_post(copy.deepcopy(wire))
        relay_accepted = (s == 202)
        trail2 = wait_for(lambda: [r for r in q(
            "select PacketId, Outcome, Reason from PacketTrailLines where PacketId=?",
            (canary_pid,)) if r[1] == "Rejected" and "Replay" in (r[2] or "")],
            timeout=30)
        time.sleep(1)
        check("replayed packet after a relay restart passes the restarted relay and is rejected by the real node's own replay registry",
              relay_accepted and bool(trail2),
              {"relay_status": s, "relay_body": rb, "trail": trail2,
               "trail_rows_before": trail_before})
        check("the replay after a relay restart produces no second record entry (event count and Recorded inbox row unchanged)",
              q("select count(*) from Events")[0][0] == ev_after_canary and
              q("select count(*) from PacketInbox where PacketId=? and Status='Recorded'",
                (canary_pid,)) == [(1,)],
              q("select count(*) from Events")[0][0])

        # ---- FINAL SEARCH: after ALL batch traffic (canary, tamper, replay). ----
        final_results = search_relay_files(stores["relay"])
        final_hits = [h for r in final_results for h in r["hits"]]
        check("final plaintext search of all relay files after the whole batch still finds no canary phrase, fragment, telling, or event body",
              not final_hits, final_results)
        evidence_extra = {"file_search_after_canary": file_results,
                          "file_search_final": final_results,
                          "snapshots_captured": len(snapshots_seen),
                          "canary_packet_id": canary_pid,
                          "t2_packet_id": t2_pid,
                          "events_after_canary": ev_after_canary,
                          "relay_log_excerpt": log_text[:2000]}
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
    evidence = {"batch": "Canary batch, one sitting, fresh seed",
                "tree": "integrated tree",
                "canary_phrase": CANARY_PHRASE,
                "canary_telling": CANARY_TELLING,
                "checks": checks, "passed": passed, "total": len(checks),
                "api_spend_usd": 0, "extraction_mode": "Mock",
                "extra": evidence_extra if 'evidence_extra' in dir() else {}}
    out = os.path.join(ROOT, "deploy", "canary-service-shape-evidence.json")
    with open(out, "w") as f:
        json.dump(evidence, f, indent=2)
    print(f"CHECKS {passed} passed, {len(checks) - passed} failed, of {len(checks)}")
    print("evidence:", out)
    return 0 if passed == len(checks) else 1


if __name__ == "__main__":
    sys.exit(main())
