#!/usr/bin/env python3
"""Silent session recovery checks.

Boots Lisa's stand-in in self-operating (auto session) mode
against the real relay (from this tree) and the stand-in node
double, joins her through the door, and then exercises the
silent recovery rule:

  1. A presented send lands through the exchange on a fresh node.
  2. The node's session state is wiped (sessions.json deleted and
     the node restarted: the post-reset state, in which the node
     has forgotten the session while Lisa's recorded proof is
     still minutes young). A presented send recovers invisibly:
     it lands exactly once, the trigger reports the send's real
     result (not a dead-session refusal, not a queued answer), the
     node shows the session open again, and the recovery is in
     Lisa's session-recoveries audit log.
  3. With the relay down, the send is doubly refused (the
     recovery exchange cannot run and the send is not attempted
     on the doubtful proof): the error surfaces honestly as a
     queued, unrecorded telling, and nothing lands.

The node double is harness code, not a home node deliverable;
the live leg runs the stand-ins against the real node as
integration evidence. This driver is evidence about the
stand-in's recovery behavior specifically. Spend: $0.
"""
import hashlib
import hmac as hmac_mod
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request
import urllib.error

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RELAY_SCRIPT = os.path.join(
    os.path.dirname(ROOT), "relay", "src", "sealed_relay.py")
RUN = "/tmp/session-recovery-run"
DOTNET = os.path.expanduser("~/.dotnet/dotnet")
RELAY = "http://127.0.0.1:7431"
NODE = "http://127.0.0.1:7432"
LISA = "http://127.0.0.1:7434"
ENDPOINT = "hh-demo-01"

checks = []


def check(name, ok, detail=""):
    checks.append({"name": name, "passed": bool(ok), "detail": str(detail)[:400]})
    print(("PASS " if ok else "FAIL ") + name + ((" :: " + str(detail)[:200]) if detail and not ok else ""))


def req(method, url, body=None, headers=None, timeout=25):
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
        with urllib.request.urlopen(r, timeout=timeout) as resp:
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
        s, _ = req("GET", url, timeout=5)
        if s and s < 500:
            return True
        time.sleep(0.5)
    return False


def wait_for(fn, timeout=40.0, interval=0.4):
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
    # The stand-in and the node double run from their Debug builds.
    env_build = dict(os.environ, DOTNET_ROOT=os.path.expanduser("~/.dotnet"),
                     DOTNET_CLI_TELEMETRY_OPTOUT="1",
                     PATH=os.path.expanduser("~/.dotnet") + ":" + os.environ["PATH"])
    for proj in ("LisaAssistant/LisaAssistant.csproj", "MockNode/MockNode.csproj"):
        r = subprocess.run([DOTNET, "build", os.path.join(ROOT, proj), "-c", "Debug",
                            "-v", "q", "--nologo"], capture_output=True, text=True, env=env_build)
        if r.returncode != 0:
            print(r.stdout[-2000:], r.stderr[-2000:])
            print("BUILD FAILED for " + proj)
            return 1

    shutil.rmtree(RUN, ignore_errors=True)
    os.makedirs(RUN)
    stores = {k: os.path.join(RUN, k) for k in ("lisa", "relay", "node")}
    for v in stores.values():
        os.makedirs(v)

    invite = {"code": "invite-lisa-7f2", "person": "Lisa",
              "check_phrase": "maple river stone", "used": False}
    with open(os.path.join(stores["node"], "invites.json"), "w") as f:
        json.dump([invite], f)
    with open(os.path.join(stores["lisa"], "pending-invite.json"), "w") as f:
        json.dump({"code": invite["code"], "check_phrase": invite["check_phrase"]}, f)

    base_env = dict(os.environ, DOTNET_ROOT=os.path.expanduser("~/.dotnet"),
                    DOTNET_CLI_TELEMETRY_OPTOUT="1")
    procs = {}
    node_argv = [DOTNET, os.path.join(ROOT, "MockNode/bin/Debug/net10.0/MockNode.dll")]
    node_env = dict(base_env, NODE_STORE=stores["node"], RELAY_URL=RELAY,
                    ASPNETCORE_URLS=NODE)
    try:
        procs["lisa"] = start("lisa", [DOTNET, os.path.join(ROOT, "LisaAssistant/bin/Debug/net10.0/LisaAssistant.dll")],
                              dict(base_env, STANDIN_STORE=stores["lisa"], RELAY_URL=RELAY, NODE_URL=NODE,
                                   ASPNETCORE_URLS=LISA, STANDIN_AUTO_SESSION="1"))
        assert wait_up(LISA + "/status"), "lisa did not start"
        _, lisa_status = req("GET", LISA + "/status")

        def credential(inv, fingerprint):
            return hkdf_hex(inv["code"].encode(),
                            ("invite:" + fingerprint).encode(),
                            b"tellhouse-door-v1")

        lisa_token = credential(invite, lisa_status["fingerprint"])
        import secrets as secrets_mod
        node_token = secrets_mod.token_hex(16)
        node_env["NODE_RELAY_TOKEN"] = node_token
        relay_config = {"connections": [
            {"connection_id": "node-conn-01", "household_endpoint": ENDPOINT, "standing": "good",
             "role": "node", "token_sha256": hashlib.sha256(node_token.encode()).hexdigest()},
            {"connection_id": "conn-lisa-01", "household_endpoint": ENDPOINT, "standing": "good",
             "role": "member", "token_sha256": hashlib.sha256(lisa_token.encode()).hexdigest()},
        ]}
        config_path = os.path.join(RUN, "relay-config.json")
        with open(config_path, "w") as f:
            json.dump(relay_config, f)
        procs["relay"] = start("relay", ["python3", RELAY_SCRIPT, "--config", config_path,
                                         "--runtime-dir", stores["relay"], "--port", "7431"],
                               dict(os.environ))
        procs["node"] = start("node", node_argv, node_env)
        assert wait_up(RELAY + "/health"), "relay did not start"
        assert wait_up(NODE + "/harness/trail"), "node did not start"

        # Door ceremony (driven; the session is never opened by hand).
        s, b = req("POST", LISA + "/act/join-begin")
        conn = b["connection_id"]
        s, ok = req("POST", NODE + "/door/confirm",
                    {"connection_id": conn, "check_phrase": "maple river stone", "inviter": "Margaret"})
        s, done = req("POST", LISA + "/act/join-complete")
        check("Lisa joins through the door in auto session mode", done.get("joined") is True, done)

        recoveries_path = os.path.join(stores["lisa"], "session-recoveries.json")

        def recoveries():
            if not os.path.exists(recoveries_path):
                return []
            return json.load(open(recoveries_path))

        # Leg 1: a presented send on a fresh node lands through the
        # exchange, reported as sent.
        s, sent1 = req("POST", LISA + "/trigger/test-send", {"act": "lisa-telling-1"}, timeout=90)
        got1 = wait_for(lambda: len(record()) == 1, timeout=40)
        check("presented send lands on a fresh node through the exchange and is reported as sent",
              s == 200 and bool(got1) and sent1.get("relay_status") == 202, (s, sent1))
        check("the session confirmation is in the recovery audit log",
              any("session confirmed" in e.get("event", "") for e in recoveries()), recoveries())

        # Leg 2: the post-reset state. The node forgets every
        # session (its session state file is wiped and it restarts)
        # while Lisa's recorded proof is still minutes young.
        state = json.load(open(os.path.join(stores["lisa"], "state.json")))
        proof_age = int(time.time()) - int(state.get("session_open_at") or 0)
        procs["node"].terminate()
        procs["node"].wait(timeout=10)
        sessions_file = os.path.join(stores["node"], "sessions.json")
        if os.path.exists(sessions_file):
            os.unlink(sessions_file)
        procs["node"] = start("node2", node_argv, node_env)
        assert wait_up(NODE + "/harness/trail"), "node did not restart"
        s, sessions_now = req("GET", NODE + "/harness/sessions")
        check("post-reset setup holds: the node has forgotten the session while Lisa's proof still looks fresh",
              not (sessions_now.get(conn) or {}).get("open") and proof_age < 20 * 60,
              {"sessions": sessions_now, "proof_age_seconds": proof_age})
        recovery_count_before = len(recoveries())
        s, sent2 = req("POST", LISA + "/trigger/test-send", {"act": "lisa-telling-1"}, timeout=90)
        got2 = wait_for(lambda: len(record()) == 2, timeout=60)
        events2 = [e for e in record() if e.get("packet_id") == sent2.get("packet_id")]
        rejected2 = [t for t in trail() if t.get("packet_id") == sent2.get("packet_id")
                     and t.get("result") == "rejected"]
        s, sessions_after = req("GET", NODE + "/harness/sessions")
        check("a send after the node forgot the session recovers invisibly and lands exactly once",
              s == 200 and sent2.get("relay_status") == 202 and bool(got2) and len(events2) == 1
              and not rejected2, (s, sent2, events2, rejected2))
        check("the recovery re-opened the session on the node's current record",
              (sessions_after.get(conn) or {}).get("open") is True, sessions_after)
        check("the recovery is written to the audit log, invisible in the presented result",
              len(recoveries()) > recovery_count_before
              and "queued" not in json.dumps(sent2), recoveries()[recovery_count_before:])

        # Leg 3: double refusal. With the relay down, the recovery
        # exchange cannot run and the send is never attempted on
        # the doubtful proof; the error surfaces honestly and
        # nothing lands.
        procs["relay"].terminate()
        procs["relay"].wait(timeout=10)
        before = len(record())
        s, b = req("POST", LISA + "/trigger/test-send", {"act": "lisa-telling-1"}, timeout=90)
        time.sleep(1.0)
        check("with the relay down the recovery cannot run: the telling queues unrecorded and the answer says so",
              s == 202 and "unrecorded" in json.dumps(b), (s, b))
        check("the doubly refused send records nothing at the node",
              len(record()) == before, record())
        check("the failed recovery is also in the audit log",
              any("exchange failed" in e.get("event", "") for e in recoveries()), recoveries())
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
    out = os.path.join(ROOT, "evidence", "session-recovery-checks-latest.json")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w") as f:
        json.dump({"checks": checks, "passed": passed, "total": len(checks),
                   "api_spend_usd": 0}, f, indent=2)
    print(f"CHECKS {passed} passed, {len(checks) - passed} failed, of {len(checks)}")
    print("evidence:", out)
    return 0 if passed == len(checks) else 1


if __name__ == "__main__":
    sys.exit(main())
