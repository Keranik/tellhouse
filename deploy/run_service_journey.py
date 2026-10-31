#!/usr/bin/env python3
"""Four-process clean-boot service journey for the deployment wiring.

Stages the four deployable services the way their Dockerfiles lay
them out (published apps plus entrypoint plus baked demo seed),
in fresh directories with environment-only configuration, and
runs the judge journey against them:

  relay boots from its entrypoint with an empty state dir and
  seeds its registry from the baked demo config;
  node boots from its entrypoint with an empty keystore and no
  database, restoring the sealed demo snapshot through the
  product's own demo-reset path;
  both stand-ins boot from their entrypoints with empty stores,
  seeded from the baked demo stores, and open their own sessions
  through the real challenge proof (auto-session mode);
  a direct telling from the website lands on the record;
  the website Test Send control fires Lisa's stand-in through
  the real relay and the telling lands with provenance;
  the house proposal of a health entry to Daniel is refused at
  the door, and a house proposal of a bins turn
  task to Daniel is disposed by his acknowledgement through his
  service;
  with the relay killed, a second Test Send queues unrecorded in
  Lisa's outbox and nothing lands;
  the relay restarts and reseeds deterministically;
  a product reset (TellhouseBackup demo-reset) returns the node
  to the determined state.

Extraction runs in Mock mode locally: $0 spend. The deployed
node uses the Standard model; that is a configuration value,
not a code path difference.

Evidence is written to deploy/service-journey-evidence.json.
This driver is build tooling, not product code.
"""
import json
import os
import re
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
STAGE = Path("/tmp/service-journey")
EVIDENCE = ROOT / "deploy" / "service-journey-evidence.json"
DOTNET_DIR = Path.home() / ".dotnet"

RELAY_PORT = 7631
NODE_PORT = 7632
LISA_PORT = 7633
DANIEL_PORT = 7634
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

    # ---- Boot: relay, node, stand-ins, all through their entrypoints.
    relay_env = {"RELAY_CONFIG": str(relay_state / "relay-config.json"),
                 "RELAY_RUNTIME_DIR": str(relay_state / "runtime"),
                 "PORT": str(RELAY_PORT)}
    relay_proc = start("relay", ["sh", "entrypoint.sh"], relay_app,
                       relay_env, "relay")
    check("relay service boots through its entrypoint and answers health",
          wait_http(RELAY_URL + "/health", 30))
    seeded_cfg = relay_state / "relay-config.json"
    check("relay registry seeded from the baked demo config on an empty state dir",
          seeded_cfg.exists() and
          seeded_cfg.read_bytes() == (relay_app / "demo-seed" / "relay-config.json").read_bytes())

    ceremony_src = (ROOT / "deploy" / "run_demo_seed_ceremony.py").read_text()
    m = re.search(r'^NODE_TOKEN\s*=\s*"([^"]+)"', ceremony_src, re.M)
    assert m, "demo node token not found in the ceremony driver"
    node_token = m.group(1)

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
        "RELAY_NODE_TOKEN": node_token,
        "HOME_NODE_HOUSE_SECRET": HOUSE_SECRET,
        "Extraction__Mode": "Mock",
        "Demo__StandinEndpoints__0__Name": "LisaStandIn",
        "Demo__StandinEndpoints__0__PersonName": "Lisa",
        "Demo__StandinEndpoints__0__TriggerUrl": LISA_URL + "/trigger/test-send",
        "Demo__StandinEndpoints__0__ActId": "lisa-telling-1",
    }
    node_proc = start("node", ["sh", "entrypoint.sh"], node_app, node_env, "node")
    check("node service boots through its entrypoint (snapshot restored on a fresh disk)",
          wait_http(NODE_URL + "/", 120) and node_db.exists())
    _, home_html = http("GET", NODE_URL + "/")
    check("deployed-shaped node carries the DEMO ENVIRONMENT label and the Test Send control",
          "DEMO ENVIRONMENT" in home_html and "TestSend" in home_html)

    base_persons = sql(node_db, "select count(*) from Persons")[0][0]
    base_events = sql(node_db, "select count(*) from Events")[0][0]
    conns = sql(node_db, "select ConnectionId, Standing from HouseConnections order by ConnectionId")
    origin_col = "ProvenanceConnection"
    check("restored determined state: 6 persons, both connections Active, record carries agent provenance",
          base_persons == 6 and len(conns) == 2 and all(s == "Active" for _, s in conns)
          and origin_col is not None
          and sql(node_db, f"select count(*) from Events where {origin_col} is not null")[0][0] >= 1,
          f"persons={base_persons} conns={conns}")

    from datetime import datetime, timezone, timedelta
    boot_mark = (datetime.now(timezone.utc) - timedelta(seconds=30)).strftime("%Y-%m-%d %H:%M:%S")
    standin_env = {"STANDIN_AUTO_SESSION": "1", "RELAY_URL": RELAY_URL, "NODE_URL": NODE_URL}
    lisa_proc = start("lisa", ["sh", "entrypoint.sh"], lisa_app,
                      {**standin_env, "ASPNETCORE_URLS": LISA_URL,
                       "STANDIN_STORE": str(lisa_store),
                       "STANDIN_SEED_DIR": str(lisa_app / "demo-seed")}, "lisa")
    daniel_proc = start("daniel", ["sh", "entrypoint.sh"], daniel_app,
                        {**standin_env, "ASPNETCORE_URLS": DANIEL_URL,
                         "STANDIN_STORE": str(daniel_store),
                         "SELF_URL": DANIEL_URL,
                         "STANDIN_SEED_DIR": str(daniel_app / "demo-seed")}, "daniel")
    ok_lisa = wait_http(LISA_URL + "/status", 60)
    ok_daniel = wait_http(DANIEL_URL + "/status", 60)
    _, lisa_status = http("GET", LISA_URL + "/status")
    _, daniel_status = http("GET", DANIEL_URL + "/status")
    check("both stand-in services boot through their entrypoints, stores seeded, joined",
          ok_lisa and ok_daniel
          and json.loads(lisa_status).get("joined") is True
          and json.loads(daniel_status).get("joined") is True
          and (lisa_store / "state.json").exists() and (daniel_store / "state.json").exists())

    # Auto-session: both stand-ins open their own sessions after boot.
    # Only challenges created during this run count; the restored
    # record also carries the ceremony's own verified challenges.
    # The seeded stores carry the ceremony's session timestamp,
    # still inside the 20 minute freshness window; a deployed
    # instance crosses that window by sitting, so the journey
    # simulates the elapsed time honestly: it zeroes the recorded
    # session time in the RUNTIME stores (never in the seed
    # artifacts) and restarts both stand-ins, whose keepalive then
    # runs the real open and answer exchange at boot.
    for store in (lisa_store, daniel_store):
        st = json.loads((store / "state.json").read_text())
        st["session_open_at"] = 0
        (store / "state.json").write_text(json.dumps(st, indent=2) + "\n")
    stop(lisa_proc)
    stop(daniel_proc)
    lisa_proc = start("lisa2", ["sh", "entrypoint.sh"], lisa_app,
                      {**standin_env, "ASPNETCORE_URLS": LISA_URL,
                       "STANDIN_STORE": str(lisa_store),
                       "STANDIN_SEED_DIR": str(lisa_app / "demo-seed")}, "lisa2")
    daniel_proc = start("daniel2", ["sh", "entrypoint.sh"], daniel_app,
                        {**standin_env, "ASPNETCORE_URLS": DANIEL_URL,
                         "STANDIN_STORE": str(daniel_store),
                         "SELF_URL": DANIEL_URL,
                         "STANDIN_SEED_DIR": str(daniel_app / "demo-seed")}, "daniel2")
    wait_http(LISA_URL + "/status", 60)
    wait_http(DANIEL_URL + "/status", 60)

    def sessions_verified():
        rows = sql(node_db, "select ConnectionId, count(*) from SessionChallenges "
                            "where Verified=1 and CreatedAtUtc >= ? group by ConnectionId",
                   (boot_mark,))
        return {r[0] for r in rows} >= {"conn-lisa-01", "conn-daniel-01"}
    deadline = time.time() + 90
    while time.time() < deadline and not sessions_verified():
        time.sleep(2)
    check("auto-session: with the recorded session time simulated stale, both stand-ins ran the real open and answer exchange on their own after boot and the node verified both",
          sessions_verified())

    # ---- Website session helpers (antiforgery), as the ceremony used.
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

    # ---- Judge journey step 1: direct telling from the website.
    before = sql(node_db, "select count(*) from Events")[0][0]
    status, _ = web_post("/?handler=Tell", {"speakerId": "3",
                               "telling": "Daniel, June has a dentist appointment on Friday at 10 in the morning."})
    check("direct telling from the website front door lands on the record",
          status == 200 and event_count_like("dentist") >= 1
          and sql(node_db, "select count(*) from Events")[0][0] > before,
          f"status={status}")

    # ---- Judge journey step 2: Test Send fires Lisa through the real relay.
    prov_before = sql(node_db, f"select count(*) from Events where {origin_col}='conn-lisa-01'")[0][0]
    status, page = web_post("/?handler=TestSend", {"standInName": "LisaStandIn"})
    deadline = time.time() + 30
    while time.time() < deadline and \
            sql(node_db, f"select count(*) from Events where {origin_col}='conn-lisa-01'")[0][0] <= prov_before:
        time.sleep(1)
    lisa_state = json.loads((lisa_store / "state.json").read_text())
    note = re.search(r"Test Send from Agent \(LisaStandIn\): ([a-z-]+)", page)
    sent_tail = ""
    if (lisa_store / "sent.json").exists():
        sent_arr = json.loads((lisa_store / "sent.json").read_text())
        sent_tail = str(sent_arr[-1])[:200] if sent_arr else "empty"
    check("website Test Send control fires Lisa's stand-in through the real relay; telling lands with provenance",
          status == 200 and note and note.group(1) == "done"
          and sql(node_db, f"select count(*) from Events where {origin_col}='conn-lisa-01'")[0][0] > prov_before
          and lisa_state.get("session_open_at") is not None,
          f"status={status} note={note.group(0) if note else None} sent={sent_tail} "
          f"bodies={sql(node_db, 'select Body from Events where Body like ' + chr(39) + '%ppointment%' + chr(39))}")

    # ---- Judge journey step 3: house proposal disposed by Daniel's service.
    # The health leg comes first: a front door telling, Margaret's
    # ankle, extracting to a Proposed event whose NotifyPerson action
    # addresses Daniel. No connection level the house offers may hear
    # health class material and Daniel's connection is Standard, so
    # proposing that health event to Daniel is refused at the door
    # and queues nothing. The proposal round trip
    # itself runs on a class clean event: a stated-turn Task telling
    # (Margaret, Daniel's turn to take the bins out) shaped by the
    # stated-turn pre-pass into a Proposed Task with a NotifyPerson
    # action for Daniel. The node queues it as a sealed proposal
    # delivery; Daniel's service receives it and his acknowledgement
    # disposes it. Same roads as the ceremony, driven here against
    # the service-shaped processes.
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
    # The node's relay loop posts the queued proposal to the relay
    # on its own pass; wait for Sent before ringing Daniel, as the
    # ceremony driver does.
    sent = []
    deadline = time.time() + 25
    while time.time() < deadline:
        sent = sql(node_db, "select Status from SealedOutbox where SourceEventId=? "
                            "and SourceKind='Proposal'", (prop_id,))
        if sent and sent[0][0] in ("Sent", "Acknowledged"):
            break
        time.sleep(1)
    status3, body3 = http("POST", DANIEL_URL + "/trigger/test-send", {"act": "daniel-receive"})
    rows, acked = [], []
    deadline = time.time() + 25
    while time.time() < deadline:
        rows = sql(node_db, "select ConfirmationState from Events where Id=?", (prop_id,))
        acked = sql(node_db, "select Status from SealedOutbox where SourceEventId=? "
                             "and SourceKind='Proposal'", (prop_id,))
        if rows and rows[0][0] == "Confirmed" and acked and acked[0][0] == "Acknowledged":
            break
        time.sleep(1)
    check("house proposal of the bins turn task reaches Daniel's service sealed and is disposed by his acknowledgement",
          status == 200 and bool(prop) and status2 == 200 and sent
          and status3 == 200 and rows and rows[0][0] == "Confirmed"
          and acked and acked[0][0] == "Acknowledged",
          f"tell={status} prop={prop} propose={status2} {body2} trigger={status3} {body3} rows={rows} acked={acked}")

    # ---- Judge journey step 4: relay killed, Test Send queues unrecorded.
    events_before_outage = sql(node_db, "select count(*) from Events")[0][0]
    stop(relay_proc)
    time.sleep(1)
    status, page = web_post("/?handler=TestSend", {"standInName": "LisaStandIn"})
    outbox = json.loads((lisa_store / "outbox.json").read_text()) \
        if (lisa_store / "outbox.json").exists() else []
    check("with the relay down the Test Send queues unrecorded in Lisa's outbox and nothing lands",
          status == 200 and "cannot reach the house" in page
          and any(e.get("record_state") == "unrecorded" for e in outbox)
          and sql(node_db, "select count(*) from Events")[0][0] == events_before_outage,
          f"status={status} outbox={len(outbox)}")

    # ---- Relay blindness on the service shape: no telling plaintext anywhere.
    needle = b"Mom's appointment"
    hits = []
    for base in (relay_state, relay_app):
        for p in base.rglob("*"):
            if p.is_file() and needle in p.read_bytes():
                hits.append(str(p))
    check("relay state and files hold no telling plaintext (sealed packets and hashes only)",
          not hits, f"hits={hits}")

    # ---- Relay restart reseeds deterministically from an emptied state dir.
    shutil.rmtree(relay_state)
    relay_proc = start("relay2", ["sh", "entrypoint.sh"], relay_app,
                       relay_env, "relay2")
    check("relay restarts from an emptied state dir and reseeds the same registry",
          wait_http(RELAY_URL + "/health", 30)
          and (relay_state / "relay-config.json").read_bytes()
          == (relay_app / "demo-seed" / "relay-config.json").read_bytes())

    # ---- Reset through the product's own backup/restore machinery.
    # The node is stopped first, and its journal sidecars go with the
    # database file: a stale -wal must never sit next to a restored
    # file. (The restore tool removes destination sidecars too.)
    stop(node_proc)
    node_db.unlink()
    for suffix in ("-wal", "-shm", "-journal"):
        node_db.with_name(node_db.name + suffix).unlink(missing_ok=True)
    phrase = (node_app / "demo-seed" / "demo-recovery-phrase.txt").read_text().strip()
    r = subprocess.run([dotnet, str(node_app / "backup" / "TellhouseBackup.dll"),
                        "demo-reset", "--backup",
                        str(node_app / "demo-seed" / "demo-snapshot.thb"),
                        "--phrase", phrase, "--dest-db", str(node_db)],
                       capture_output=True, text=True)
    node_proc = start("node2", ["sh", "entrypoint.sh"], node_app, node_env, "node2")
    ok = wait_http(NODE_URL + "/", 120)
    after_persons = sql(node_db, "select count(*) from Persons")[0][0] if ok else -1
    after_events = sql(node_db, "select count(*) from Events")[0][0] if ok else -1
    conns2 = sql(node_db, "select Standing from HouseConnections") if ok else []
    check("product reset returns the node to the determined state (same counts, connections Active, journey traffic gone)",
          r.returncode == 0 and ok and after_persons == base_persons
          and after_events == base_events
          and all(s == "Active" for (s,) in conns2)
          and event_count_like("dentist appointment") == 0,
          f"reset_rc={r.returncode} persons={after_persons} events={after_events} base={base_events}")

    passed = sum(1 for r_ in results if r_["ok"])
    print(f"CHECKS {passed} passed, {len(results) - passed} failed, of {len(results)}")
    EVIDENCE.write_text(json.dumps(
        {"checks": results, "passed": passed, "total": len(results),
         "baseline": {"persons": base_persons, "events": base_events}}, indent=2) + "\n")
    print(f"evidence: {EVIDENCE}")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    finally:
        for _, proc in procs:
            stop(proc)
