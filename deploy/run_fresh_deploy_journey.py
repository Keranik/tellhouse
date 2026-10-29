#!/usr/bin/env python3
"""Fresh deployment journey: proof that the deployed shape
runs on fresh values, never on the published demo material.

Stages the four services exactly as the service journey does
(published apps, entrypoints, and the baked demo material
present in each image layout), then boots all four in fresh
deploy mode (TELLHOUSE_DEPLOY_MODE=fresh) with EMPTY runtime
state and drives the operator sequence written in DEPLOY.md,
"Deploying fresh", over HTTP:

  relay boots fresh with only the node token hash set (a fresh
  random token the driver picks, standing in for the
  operator); its registry is built from that hash alone;
  node boots fresh: no baked key copied, no baked snapshot
  restored; it generates its own keypair, seeds the fictional
  day, and its entrypoint takes this deployment's first
  sealed backup, capturing a fresh recovery phrase into the
  keystore only;
  both stand-ins boot fresh: no baked store copied; each
  generates its own keypair and starts unjoined;
  the operator (this driver) issues two invites at the node's
  door, sets the invite codes on the stand-ins (they restart
  onto their deployment keypairs and knock on their own),
  reads their fingerprints, computes the relay registry
  hashes with the same HKDF the product publishes, restarts
  the relay with the full fresh registry, and confirms both
  knocks as the inviter (a wrong phrase is refused first);
  both stand-ins complete the join through the real door and
  open their own sessions;
  the judge journey then runs on the fresh values: a
  direct website telling lands, the website Test Send fires Lisa
  through the real relay with provenance, the house proposal
  of a health entry is refused at the door, a
  house proposal of a bins turn task is disposed by Daniel's
  acknowledgement, and with the relay down a Test Send queues
  unrecorded;
  a reset with THIS deployment's snapshot and phrase returns
  the node to its determined state;
  finally, a distinctness sweep reads every runtime file the
  deployment produced and fails if any published demo
  credential value (the demo recovery phrase, the demo node
  key, the ceremony driver's tokens, the shipped stand-in
  credentials or private keys, the shipped registry's token
  hashes) appears in any of them.

Extraction runs in Mock mode locally: $0 spend. The deployed
node uses the Standard model; that is a configuration value,
not a code path difference.

Evidence is written to deploy/fresh-deploy-journey-evidence.json.
This driver is build tooling, not product code.
"""
import hashlib
import hmac as hmac_mod
import json
import os
import re
import secrets
import shutil
import signal
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from http.cookiejar import CookieJar
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HOUSE_SECRET = "local-driver-house-secret"  # the house side credential this driver presents to the node door admin surfaces; the node it starts is given the same value.
STAGE = Path("/tmp/fresh-deploy-journey")
EVIDENCE = ROOT / "deploy" / "fresh-deploy-journey-evidence.json"
DOTNET_DIR = Path.home() / ".dotnet"

RELAY_PORT = 7731
NODE_PORT = 7732
LISA_PORT = 7733
DANIEL_PORT = 7734
RELAY_URL = f"http://127.0.0.1:{RELAY_PORT}"
NODE_URL = f"http://127.0.0.1:{NODE_PORT}"
LISA_URL = f"http://127.0.0.1:{LISA_PORT}"
DANIEL_URL = f"http://127.0.0.1:{DANIEL_PORT}"

results = []
procs = []


def check(name, ok, detail=""):
    results.append({"check": name, "ok": bool(ok), "detail": detail})
    print(("PASS " if ok else "FAIL ") + name + (f" [{detail}]" if detail and not ok else ""))


def http(method, url, body=None, headers=None, timeout=30):
    data = None
    hdrs = dict(headers or {})
    if body is not None:
        if isinstance(body, (dict, list)):
            data = json.dumps(body).encode()
            hdrs["Content-Type"] = "application/json"
        elif isinstance(body, str):
            data = body.encode()
        else:
            data = body
    if any(part in url for part in ("/door/invite", "/door/card", "/door/confirm", "/door/decline", "/admin/")):
        hdrs["X-House-Secret"] = HOUSE_SECRET
    req = urllib.request.Request(url, data=data, headers=hdrs, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")
    except Exception:
        return 0, ""


def wait_http(url, timeout_s=90):
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        status, _ = http("GET", url, timeout=5)
        if status == 200:
            return True
        time.sleep(1)
    return False


def start(name, argv, cwd, env, logname):
    full_env = dict(os.environ)
    full_env["PATH"] = f"{DOTNET_DIR}:{full_env['PATH']}"
    full_env["DOTNET_ROOT"] = str(DOTNET_DIR)
    full_env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    full_env.update(env)
    log = open(STAGE / f"{logname}.log", "ab")
    proc = subprocess.Popen(argv, cwd=str(cwd), env=full_env,
                            stdout=log, stderr=subprocess.STDOUT)
    procs.append((name, proc))
    return proc


def stop(proc):
    if proc.poll() is None:
        proc.send_signal(signal.SIGTERM)
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=10)


def sql(db, query, args=()):
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        return con.execute(query, args).fetchall()
    finally:
        con.close()


def hkdf_hex(ikm, salt, info, length=32):
    prk = hmac_mod.new(salt, ikm, hashlib.sha256).digest()
    out, t, counter = b"", b"", 1
    while len(out) < length:
        t = hmac_mod.new(prk, t + info + bytes([counter]), hashlib.sha256).digest()
        out += t
        counter += 1
    return out[:length].hex()


def sha256_hex(text):
    return hashlib.sha256(text.encode()).hexdigest()


def main():
    if STAGE.exists():
        shutil.rmtree(STAGE)
    (STAGE / "runtime").mkdir(parents=True)
    dotnet = str(DOTNET_DIR / "dotnet")

    # ---- Stage the four services the way their images lay them out.
    relay_app = STAGE / "relay-app"
    shutil.copytree(ROOT / "relay", relay_app,
                    ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    node_app = STAGE / "node-app"
    subprocess.run([dotnet, "publish", str(ROOT / "src/HomeNode/HomeNode.csproj"),
                    "-c", "Release", "-o", str(node_app), "--nologo", "-v", "q"],
                   check=True, capture_output=True)
    subprocess.run([dotnet, "publish", str(ROOT / "src/TellhouseBackup/TellhouseBackup.csproj"),
                    "-c", "Release", "-o", str(node_app / "backup"), "--nologo", "-v", "q"],
                   check=True, capture_output=True)
    shutil.copy(ROOT / "src/HomeNode/entrypoint.sh", node_app / "entrypoint.sh")
    shutil.copytree(ROOT / "src/HomeNode/demo-seed", node_app / "demo-seed", dirs_exist_ok=True)
    lisa_app = STAGE / "lisa-app"
    subprocess.run([dotnet, "publish", str(ROOT / "standins/LisaAssistant/LisaAssistant.csproj"),
                    "-c", "Release", "-o", str(lisa_app), "--nologo", "-v", "q"],
                   check=True, capture_output=True)
    shutil.copy(ROOT / "standins/LisaAssistant/entrypoint.sh", lisa_app / "entrypoint.sh")
    shutil.copytree(ROOT / "standins/LisaAssistant/demo-seed", lisa_app / "demo-seed", dirs_exist_ok=True)
    daniel_app = STAGE / "daniel-app"
    subprocess.run([dotnet, "publish", str(ROOT / "standins/DanielAssistant/DanielAssistant.csproj"),
                    "-c", "Release", "-o", str(daniel_app), "--nologo", "-v", "q"],
                   check=True, capture_output=True)
    shutil.copy(ROOT / "standins/DanielAssistant/entrypoint.sh", daniel_app / "entrypoint.sh")
    shutil.copytree(ROOT / "standins/DanielAssistant/demo-seed", daniel_app / "demo-seed", dirs_exist_ok=True)

    relay_state = STAGE / "relay-state"
    node_db = STAGE / "runtime" / "home-node.db"
    node_keys = STAGE / "runtime" / "node-keys"
    lisa_store = STAGE / "lisa-store"
    daniel_store = STAGE / "daniel-store"

    # ---- The published toy values, for the distinctness assertions.
    toy_phrase = (node_app / "demo-seed" / "demo-recovery-phrase.txt").read_text().strip()
    toy_node_key_lines = [ln.strip() for ln in
                          (node_app / "demo-seed" / "node-keys" / "node-content-key.txt")
                          .read_text().splitlines() if ln.strip()]
    ceremony_src = (ROOT / "deploy" / "run_demo_seed_ceremony.py").read_text()
    m = re.search(r'^NODE_TOKEN\s*=\s*"([^"]+)"', ceremony_src, re.M)
    assert m, "NODE_TOKEN not found in the ceremony driver"
    toy_tokens = {"NODE_TOKEN": m.group(1)}
    toy_registry_hashes = [c["token_sha256"] for c in json.loads(
        (relay_app / "demo-seed" / "relay-config.json").read_text())["connections"]]
    toy_store_values = []
    for app_dir in (lisa_app, daniel_app):
        keys = json.loads((app_dir / "demo-seed" / "store" / "keys.json").read_text())
        state = json.loads((app_dir / "demo-seed" / "store" / "state.json").read_text())
        toy_store_values.append(keys["d"])
        toy_store_values.append(state["credential"])

    # ---- Operator step: choose a fresh node token.
    fresh_token = "fresh-" + secrets.token_hex(24)
    fresh_node_hash = sha256_hex(fresh_token)

    # ---- Boot the relay fresh, node hash only.
    relay_env = {"RELAY_CONFIG": str(relay_state / "relay-config.json"),
                 "RELAY_RUNTIME_DIR": str(relay_state / "runtime"),
                 "PORT": str(RELAY_PORT),
                 "TELLHOUSE_DEPLOY_MODE": "fresh",
                 "RELAY_NODE_TOKEN_SHA256": fresh_node_hash}
    relay_proc = start("relay", ["sh", "entrypoint.sh"], relay_app, relay_env, "relay")
    check("fresh relay boots through its entrypoint and answers health",
          wait_http(RELAY_URL + "/health", 30))
    cfg = json.loads((relay_state / "relay-config.json").read_text())
    cfg_text = (relay_state / "relay-config.json").read_text()
    check("fresh relay registry is built from the operator hash alone: one node connection, no member connections, no published hash",
          len(cfg["connections"]) == 1
          and cfg["connections"][0]["connection_id"] == "node-conn-01"
          and cfg["connections"][0]["token_sha256"] == fresh_node_hash
          and all(h not in cfg_text for h in toy_registry_hashes),
          f"cfg={cfg}")

    # ---- Boot the node fresh.
    node_env = {
        "ASPNETCORE_URLS": NODE_URL,
        "ASPNETCORE_ENVIRONMENT": "Production",
        "ConnectionStrings__HomeNode": f"Data Source={node_db}",
        "HOME_NODE_KEYSTORE": str(node_keys),
        "DEMO_SEED_DIR": str(node_app / "demo-seed"),
        "BACKUP_DLL": str(node_app / "backup" / "TellhouseBackup.dll"),
        "APP_DLL": str(node_app / "HomeNode.dll"),
        "Relay__BaseUrl": RELAY_URL,
        "Relay__NodeConnectionId": "node-conn-01",
        "RELAY_NODE_TOKEN": fresh_token,
        "HOME_NODE_HOUSE_SECRET": HOUSE_SECRET,
        "Extraction__Mode": "Mock",
        "TELLHOUSE_DEPLOY_MODE": "fresh",
        "Demo__StandinEndpoints__0__Name": "LisaStandIn",
        "Demo__StandinEndpoints__0__PersonName": "Lisa",
        "Demo__StandinEndpoints__0__TriggerUrl": LISA_URL + "/trigger/test-send",
        "Demo__StandinEndpoints__0__ActId": "lisa-telling-1",
    }
    node_proc = start("node", ["sh", "entrypoint.sh"], node_app, node_env, "node")
    check("fresh node boots through its entrypoint (own keypair, seeded day, own sealed backup)",
          wait_http(NODE_URL + "/", 240) and node_db.exists())
    node_log = (STAGE / "node.log").read_text(errors="replace")
    persons0 = sql(node_db, "select count(*) from Persons")[0][0] if node_db.exists() else -1
    conns0 = sql(node_db, "select count(*) from HouseConnections")[0][0] if node_db.exists() else -1
    key_path = node_keys / "node-content-key.txt"
    phrase_path = node_keys / "deployment-recovery-phrase.txt"
    snap_path = node_keys / "deployment-snapshot.thb"
    fresh_phrase = phrase_path.read_text().strip() if phrase_path.exists() else ""
    check("fresh node generated its own keypair: keystore key exists and is not the published demo key",
          key_path.exists()
          and all(ln not in key_path.read_text() for ln in toy_node_key_lines))
    check("fresh node took its own sealed backup at first boot: deployment snapshot and a fresh recovery phrase exist in the keystore, the phrase is not the published demo phrase, and the phrase never reached the service log",
          snap_path.exists() and len(fresh_phrase.split()) >= 12
          and fresh_phrase != toy_phrase and toy_phrase not in fresh_phrase
          and fresh_phrase not in node_log
          and "fresh deploy mode" in node_log)
    check("fresh node record is the seeded fictional day with no connections yet (no ceremony state rode in)",
          persons0 == 6 and conns0 == 0, f"persons={persons0} conns={conns0}")

    # ---- Boot both stand-ins fresh, no invite codes yet.
    standin_env = {"STANDIN_AUTO_SESSION": "1", "STANDIN_AUTO_JOIN": "1",
                   "TELLHOUSE_DEPLOY_MODE": "fresh",
                   "RELAY_URL": RELAY_URL, "NODE_URL": NODE_URL}
    lisa_proc = start("lisa", ["sh", "entrypoint.sh"], lisa_app,
                      {**standin_env, "ASPNETCORE_URLS": LISA_URL, "SELF_URL": LISA_URL,
                       "STANDIN_STORE": str(lisa_store),
                       "STANDIN_SEED_DIR": str(lisa_app / "demo-seed")}, "lisa")
    daniel_proc = start("daniel", ["sh", "entrypoint.sh"], daniel_app,
                        {**standin_env, "ASPNETCORE_URLS": DANIEL_URL, "SELF_URL": DANIEL_URL,
                         "STANDIN_STORE": str(daniel_store),
                         "STANDIN_SEED_DIR": str(daniel_app / "demo-seed")}, "daniel")
    ok_lisa = wait_http(LISA_URL + "/status", 60)
    ok_daniel = wait_http(DANIEL_URL + "/status", 60)
    _, lisa_status = http("GET", LISA_URL + "/status")
    _, daniel_status = http("GET", DANIEL_URL + "/status")
    lisa_keys = json.loads((lisa_store / "keys.json").read_text()) if (lisa_store / "keys.json").exists() else {}
    daniel_keys = json.loads((daniel_store / "keys.json").read_text()) if (daniel_store / "keys.json").exists() else {}
    check("fresh stand-ins boot through their entrypoints unjoined, each with its own generated keypair, not the published demo keys",
          ok_lisa and ok_daniel
          and json.loads(lisa_status).get("joined") is False
          and json.loads(daniel_status).get("joined") is False
          and lisa_keys.get("d") and daniel_keys.get("d")
          and lisa_keys["d"] not in toy_store_values
          and daniel_keys["d"] not in toy_store_values
          and lisa_keys["d"] != daniel_keys["d"])

    # ---- Operator: issue the two invites at the node's door.
    s1, b1 = http("POST", NODE_URL + "/door/invite",
                  {"inviter": "Margaret", "intended": "Lisa", "level": "Standard"})
    s2, b2 = http("POST", NODE_URL + "/door/invite",
                  {"inviter": "Margaret", "intended": "Daniel", "level": "Standard"})
    lisa_code = json.loads(b1).get("code", "") if s1 == 200 else ""
    daniel_code = json.loads(b2).get("code", "") if s2 == 200 else ""
    check("operator issues fresh invite codes for both stand-ins at the deployed node's door",
          s1 == 200 and s2 == 200 and lisa_code and daniel_code and lisa_code != daniel_code,
          f"s1={s1} s2={s2}")

    # ---- Operator: set the invite codes on the stand-ins; they
    # restart onto their deployment keypairs and knock on their own.
    stop(lisa_proc)
    stop(daniel_proc)
    lisa_proc = start("lisa2", ["sh", "entrypoint.sh"], lisa_app,
                      {**standin_env, "ASPNETCORE_URLS": LISA_URL, "SELF_URL": LISA_URL,
                       "STANDIN_STORE": str(lisa_store),
                       "STANDIN_SEED_DIR": str(lisa_app / "demo-seed"),
                       "STANDIN_INVITE_CODE": lisa_code}, "lisa2")
    daniel_proc = start("daniel2", ["sh", "entrypoint.sh"], daniel_app,
                        {**standin_env, "ASPNETCORE_URLS": DANIEL_URL, "SELF_URL": DANIEL_URL,
                         "STANDIN_STORE": str(daniel_store),
                         "STANDIN_SEED_DIR": str(daniel_app / "demo-seed"),
                         "STANDIN_INVITE_CODE": daniel_code}, "daniel2")
    wait_http(LISA_URL + "/status", 60)
    wait_http(DANIEL_URL + "/status", 60)

    def pending(party):
        # A knock that is waiting for the inviter lives in
        # JoinRequests with status Pending; the HouseConnections
        # row is created by the confirmation itself.
        rows = sql(node_db, "select Status from JoinRequests where ClaimedPartyId=?", (party,))
        return rows and rows[0][0] == "Pending"
    deadline = time.time() + 120
    while time.time() < deadline and not (pending("member-lisa") and pending("member-daniel")):
        time.sleep(2)
    check("both stand-ins knocked on their own through auto-join and stand pending at the node",
          pending("member-lisa") and pending("member-daniel"))

    # ---- Operator: fingerprints from the restarted instances,
    # registry hashes, relay restarts with the full fresh registry.
    _, lisa_status = http("GET", LISA_URL + "/status")
    _, daniel_status = http("GET", DANIEL_URL + "/status")
    lisa_fp = json.loads(lisa_status)["fingerprint"]
    daniel_fp = json.loads(daniel_status)["fingerprint"]
    lisa_cred = hkdf_hex(lisa_code.encode(), ("invite:" + lisa_fp).encode(), b"tellhouse-door-v1")
    daniel_cred = hkdf_hex(daniel_code.encode(), ("invite:" + daniel_fp).encode(), b"tellhouse-door-v1")
    stop(relay_proc)
    (relay_state / "relay-config.json").unlink()
    relay_env2 = dict(relay_env)
    relay_env2["RELAY_LISA_TOKEN_SHA256"] = sha256_hex(lisa_cred)
    relay_env2["RELAY_DANIEL_TOKEN_SHA256"] = sha256_hex(daniel_cred)
    relay_proc = start("relay2", ["sh", "entrypoint.sh"], relay_app, relay_env2, "relay2")
    ok_relay = wait_http(RELAY_URL + "/health", 30)
    cfg2 = json.loads((relay_state / "relay-config.json").read_text())
    by_id = {c["connection_id"]: c["token_sha256"] for c in cfg2["connections"]}
    check("relay restarts with the full fresh registry: node and both member hashes are the operator's fresh values, no published hash present",
          ok_relay and by_id.get("node-conn-01") == fresh_node_hash
          and by_id.get("conn-lisa-01") == sha256_hex(lisa_cred)
          and by_id.get("conn-daniel-01") == sha256_hex(daniel_cred)
          and all(h not in json.dumps(cfg2) for h in toy_registry_hashes),
          f"by_id={by_id}")

    # ---- Operator: confirm both knocks as the inviter; a wrong
    # phrase is refused first, exactly as in the ceremony.
    s_card, card_body = http("GET", NODE_URL + "/door/card/conn-lisa-01")
    card = json.loads(card_body) if s_card == 200 else {}
    s_bad, _ = http("POST", NODE_URL + "/door/confirm",
                    {"connection_id": "conn-lisa-01",
                     "check_phrase": "a phrase nobody said",
                     "inviter": "Margaret"})
    check("a wrong check phrase is refused at the deployed node's door",
          s_card == 200 and s_bad == 403, f"card={s_card} bad={s_bad}")
    s_ok1, _ = http("POST", NODE_URL + "/door/confirm",
                    {"connection_id": "conn-lisa-01",
                     "check_phrase": card.get("check_phrase"),
                     "inviter": "Margaret"})
    s_card2, card2_body = http("GET", NODE_URL + "/door/card/conn-daniel-01")
    card2 = json.loads(card2_body) if s_card2 == 200 else {}
    s_ok2, _ = http("POST", NODE_URL + "/door/confirm",
                     {"connection_id": "conn-daniel-01",
                      "check_phrase": card2.get("check_phrase"),
                      "inviter": "Margaret"})

    def joined(url):
        st, body = http("GET", url + "/status")
        return st == 200 and json.loads(body).get("joined") is True
    deadline = time.time() + 180
    while time.time() < deadline and not (joined(LISA_URL) and joined(DANIEL_URL)):
        time.sleep(3)
    conns = sql(node_db, "select ConnectionId, Standing from HouseConnections order by ConnectionId")
    check("inviter confirms both knocks and both stand-ins complete the join through the real door on their own (invites disposed)",
          s_ok1 == 200 and s_ok2 == 200 and joined(LISA_URL) and joined(DANIEL_URL)
          and len(conns) == 2 and all(s == "Active" for _, s in conns)
          and not (lisa_store / "pending-invite.json").exists()
          and not (daniel_store / "pending-invite.json").exists(),
          f"confirm=({s_ok1},{s_ok2}) conns={conns}")

    # ---- Operator valve step: the standing Consent state the
    # demo household runs with, composed through the node's
    # valve endpoint exactly as the baked demo's seed ceremony
    # composes it (Lisa OneLiner, Daniel OneLiner, Daniel
    # ProposalResponse). This step is part of the documented
    # operator sequence in DEPLOY.md, and it is load-bearing:
    # by design the node rejects a member's packets whole
    # while that member's valve is closed, and a fresh
    # deployment holds no Consent in force until the operator
    # opens it here. Without it no telling can land and no
    # proposal acknowledgement can dispose.
    v1, _ = http("POST", NODE_URL + "/admin/valve",
                 {"connection_id": "conn-lisa-01", "material_class": "OneLiner", "state": "open"})
    v2, _ = http("POST", NODE_URL + "/admin/valve",
                 {"connection_id": "conn-daniel-01", "material_class": "OneLiner", "state": "open"})
    v3, _ = http("POST", NODE_URL + "/admin/valve",
                 {"connection_id": "conn-daniel-01", "material_class": "ProposalResponse", "state": "open"})
    valves = sql(node_db, "select PersonId, MaterialClass from ConsentRecords where RevokedAtUtc is null")
    check("operator opens the standing Consent valves the demo household runs with (Lisa OneLiner, Daniel OneLiner, Daniel ProposalResponse)",
          (v1, v2, v3) == (200, 200, 200)
          and set(valves) >= {(3, "OneLiner"), (2, "OneLiner"), (2, "ProposalResponse")},
          f"valve_status=({v1},{v2},{v3}) consents={valves}")

    # ---- Auto-session on the fresh connections, warmed the
    # same way the baked-seed service journey warms it: the
    # recorded session time in the RUNTIME stores (never in any
    # seed artifact) is zeroed and both stand-ins restart, so
    # the auto-session keepalive runs the real open and answer
    # exchange at boot with no deliveries in flight. Warm
    # sessions are the operating state the rest of the journey,
    # like the real deployment, runs in. (The session answer
    # exchange no longer endangers a delivery that lands
    # mid-exchange: packets it takes that are not the challenge
    # are held and acknowledged through the poll path once the
    # session is open; the stand-in protocol checks cover that
    # behavior.)
    for store in (lisa_store, daniel_store):
        st = json.loads((store / "state.json").read_text())
        st["session_open_at"] = 0
        (store / "state.json").write_text(json.dumps(st, indent=2) + "\n")
    stop(lisa_proc)
    stop(daniel_proc)
    lisa_proc = start("lisa3", ["sh", "entrypoint.sh"], lisa_app,
                      {**standin_env, "ASPNETCORE_URLS": LISA_URL, "SELF_URL": LISA_URL,
                       "STANDIN_STORE": str(lisa_store),
                       "STANDIN_SEED_DIR": str(lisa_app / "demo-seed"),
                       "STANDIN_INVITE_CODE": lisa_code}, "lisa3")
    daniel_proc = start("daniel3", ["sh", "entrypoint.sh"], daniel_app,
                        {**standin_env, "ASPNETCORE_URLS": DANIEL_URL, "SELF_URL": DANIEL_URL,
                         "STANDIN_STORE": str(daniel_store),
                         "STANDIN_SEED_DIR": str(daniel_app / "demo-seed"),
                         "STANDIN_INVITE_CODE": daniel_code}, "daniel3")
    wait_http(LISA_URL + "/status", 60)
    wait_http(DANIEL_URL + "/status", 60)

    from datetime import datetime, timezone, timedelta
    boot_mark = (datetime.now(timezone.utc) - timedelta(seconds=30)).strftime("%Y-%m-%d %H:%M:%S")

    def sessions_verified():
        rows = sql(node_db, "select ConnectionId, count(*) from SessionChallenges "
                            "where Verified=1 and CreatedAtUtc >= ? group by ConnectionId",
                   (boot_mark,))
        return {r[0] for r in rows} >= {"conn-lisa-01", "conn-daniel-01"}
    deadline = time.time() + 120
    while time.time() < deadline and not sessions_verified():
        time.sleep(2)
    check("auto-session: both stand-ins ran the real open and answer exchange on the fresh connections and the node verified both",
          sessions_verified())

    # ---- Website session helpers (antiforgery).
    cj = CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cj))

    def web_get(path):
        with opener.open(NODE_URL + path, timeout=30) as resp:
            return resp.read().decode("utf-8", "replace")

    def web_post(path, fields):
        page = web_get("/")
        m = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page)
        data = dict(fields)
        data["__RequestVerificationToken"] = m.group(1)
        req = urllib.request.Request(NODE_URL + path,
                                     data=urllib.parse.urlencode(data).encode(),
                                     method="POST")
        try:
            with opener.open(req, timeout=60) as resp:
                return resp.status, resp.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8", "replace")

    def event_count_like(fragment):
        return sql(node_db, "select count(*) from Events where Body like ?",
                   (f"%{fragment}%",))[0][0]

    # ---- Judge journey on the fresh values.
    before = sql(node_db, "select count(*) from Events")[0][0]
    status, _ = web_post("/?handler=Tell", {"speakerId": "3",
                               "telling": "Daniel, June has a dentist appointment on Friday at 10 in the morning."})
    check("direct telling from the website front door lands on the fresh record",
          status == 200 and event_count_like("dentist") >= 1
          and sql(node_db, "select count(*) from Events")[0][0] > before,
          f"status={status}")

    origin_col = "ProvenanceConnection"
    prov_before = sql(node_db, f"select count(*) from Events where {origin_col}='conn-lisa-01'")[0][0]
    status, page = web_post("/?handler=TestSend", {"standInName": "LisaStandIn"})
    deadline = time.time() + 90
    while time.time() < deadline and \
            sql(node_db, f"select count(*) from Events where {origin_col}='conn-lisa-01'")[0][0] <= prov_before:
        time.sleep(1)
    lisa_state = json.loads((lisa_store / "state.json").read_text())
    note = re.search(r"Test Send from Agent \(LisaStandIn\): ([a-z-]+)", page)
    check("website Test Send fires Lisa's stand-in through the fresh relay; telling lands with provenance",
          status == 200 and note and note.group(1) == "sent"
          and sql(node_db, f"select count(*) from Events where {origin_col}='conn-lisa-01'")[0][0] > prov_before
          and lisa_state.get("session_open_at") is not None,
          f"status={status} note={note.group(0) if note else None}")

    # The health leg comes first: a front door telling, Margaret's
    # ankle, extracting to a Proposed event whose NotifyPerson action
    # addresses Daniel. No connection level the house offers may hear
    # health class material and Daniel's connection is Standard, so
    # proposing that health event to Daniel is refused at the door
    # and queues nothing. The proposal round trip
    # itself runs on a class clean event: a stated-turn Task telling
    # (Margaret, Daniel's turn to take the bins out) shaped by the
    # stated-turn pre-pass into a Proposed Task with a NotifyPerson
    # action for Daniel, disposed below by Daniel's acknowledgement.
    status, _ = web_post("/?handler=Tell", {"speakerId": "1",
                               "telling": "My ankle has swollen up again."})
    prop = sql(node_db, "select e.Id from Events e join EventActions a on a.EventId=e.Id "
                        "where e.Body='My ankle has swollen up again.' "
                        "and e.ConfirmationState='Proposed' and a.ActionType='NotifyPerson' "
                        "and a.TargetPersonId=2 order by e.Id desc limit 1")
    health_id = prop[0][0] if prop else -1
    status_fence, body_fence = http("POST", NODE_URL + "/admin/propose", {"event_id": health_id})
    fence_rows = sql(node_db, "select Status from SealedOutbox where SourceEventId=? "
                              "and SourceKind='Proposal'", (health_id,))
    fence_state = sql(node_db, "select ConfirmationState from Events where Id=?", (health_id,))
    check("house proposal of a health entry to a Standard connection is refused at the door",
          status == 200 and bool(prop) and status_fence == 409
          and "addressee cannot receive that delivery on their connection" in body_fence
          and not fence_rows and fence_state and fence_state[0][0] == "Proposed",
          f"tell={status} prop={prop} fence={status_fence} {body_fence} rows={fence_rows} state={fence_state}")
    status, _ = web_post("/?handler=Tell", {"speakerId": "1",
                               "telling": "Daniel's turn to take the bins out, the truck comes at six."})
    prop = sql(node_db, "select e.Id from Events e join EventActions a on a.EventId=e.Id "
                        "where e.Body=? "
                        "and e.ConfirmationState='Proposed' and a.ActionType='NotifyPerson' "
                        "and a.TargetPersonId=2 order by e.Id desc limit 1",
               ("Daniel's turn to take the bins out, the truck comes at six.",))
    prop_id = prop[0][0] if prop else -1
    status2, body2 = http("POST", NODE_URL + "/admin/propose", {"event_id": prop_id})
    sent = []
    deadline = time.time() + 90
    while time.time() < deadline:
        sent = sql(node_db, "select Status from SealedOutbox where SourceEventId=? "
                            "and SourceKind='Proposal'", (prop_id,))
        if sent and sent[0][0] in ("Sent", "Acknowledged"):
            break
        time.sleep(1)
    status3, body3 = http("POST", DANIEL_URL + "/trigger/test-send", {"act": "daniel-receive"})
    rows, acked = [], []
    deadline = time.time() + 90
    while time.time() < deadline:
        rows = sql(node_db, "select ConfirmationState from Events where Id=?", (prop_id,))
        acked = sql(node_db, "select Status from SealedOutbox where SourceEventId=? "
                             "and SourceKind='Proposal'", (prop_id,))
        if rows and rows[0][0] == "Confirmed" and acked and acked[0][0] == "Acknowledged":
            break
        time.sleep(1)
    check("house proposal of the bins turn task reaches Daniel's fresh service sealed and is disposed by his acknowledgement",
          status == 200 and bool(prop) and status2 == 200 and sent
          and status3 == 200 and rows and rows[0][0] == "Confirmed"
          and acked and acked[0][0] == "Acknowledged",
          f"tell={status} prop={prop} propose={status2} {body2} trigger={status3} {body3} rows={rows} acked={acked}")

    events_before_outage = sql(node_db, "select count(*) from Events")[0][0]
    stop(relay_proc)
    time.sleep(1)
    status, page = web_post("/?handler=TestSend", {"standInName": "LisaStandIn"})
    outbox = json.loads((lisa_store / "outbox.json").read_text()) \
        if (lisa_store / "outbox.json").exists() else []
    check("with the relay down the Test Send queues unrecorded in Lisa's outbox and nothing lands",
          status == 200 and "queued" in page
          and any(e.get("record_state") == "unrecorded" for e in outbox)
          and sql(node_db, "select count(*) from Events")[0][0] == events_before_outage,
          f"status={status} outbox={len(outbox)}")

    # ---- Reset with THIS deployment's own snapshot and phrase.
    base_persons = sql(node_db, "select count(*) from Persons")[0][0]
    # Stopped first, and the journal sidecars go with the database
    # file, as in the service journey reset.
    stop(node_proc)
    node_db.unlink()
    for suffix in ("-wal", "-shm", "-journal"):
        node_db.with_name(node_db.name + suffix).unlink(missing_ok=True)
    r = subprocess.run([dotnet, str(node_app / "backup" / "TellhouseBackup.dll"),
                        "demo-reset", "--backup", str(snap_path),
                        "--phrase", fresh_phrase, "--dest-db", str(node_db)],
                       capture_output=True, text=True)
    node_proc = start("node2", ["sh", "entrypoint.sh"], node_app, node_env, "node2")
    ok = wait_http(NODE_URL + "/", 120)
    after_persons = sql(node_db, "select count(*) from Persons")[0][0] if ok else -1
    conns2 = sql(node_db, "select Standing from HouseConnections") if ok else []
    check("reset with the deployment's own snapshot and fresh phrase returns the node to its determined state (journey traffic gone, connections Active)",
          r.returncode == 0 and ok and after_persons == base_persons
          and all(s == "Active" for (s,) in conns2)
          and event_count_like("dentist appointment") == 0,
          f"reset_rc={r.returncode} persons={after_persons} conns={conns2}")

    # ---- Distinctness sweep: no published demo credential value
    # in any runtime file this deployment produced.
    toy_values = sorted({toy_phrase} | set(toy_node_key_lines) |
                        set(toy_tokens.values()) | set(toy_registry_hashes) |
                        set(toy_store_values))
    sweep_roots = [node_keys, relay_state, lisa_store, daniel_store]
    hits = []
    for base in sweep_roots:
        for p in base.rglob("*"):
            if p.is_file():
                data = p.read_bytes()
                for v in toy_values:
                    if v.encode() in data:
                        hits.append(f"{p}: published value present")
    db_bytes = node_db.read_bytes()
    for v in toy_values:
        if v.encode() in db_bytes:
            hits.append("node database: published value present")
    check("distinctness sweep: no published demo credential value appears in any runtime file of the fresh deployment (keystore, database, relay registry, stand-in stores)",
          not hits, f"hits={hits}")

    passed = sum(1 for r_ in results if r_["ok"])
    print(f"CHECKS {passed} passed, {len(results) - passed} failed, of {len(results)}")
    EVIDENCE.write_text(json.dumps(
        {"checks": results, "passed": passed, "total": len(results)}, indent=2) + "\n")
    print(f"evidence: {EVIDENCE}")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    finally:
        for _, proc in procs:
            stop(proc)
