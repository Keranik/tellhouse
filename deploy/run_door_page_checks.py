#!/usr/bin/env python3
"""Door page checks: the Connections page is
not a gate-free path to the door ceremony.

Boots the real HomeNode with a house secret configured and walks
the ceremony the way an unauthenticated web client would:

  1. The HTTP door refuses an invite without the secret (control).
  2. Anonymous page POSTs (invite, confirm, decline, revoke) are
     refused with 403 and change nothing.
  3. An anonymous GET of /Connections never renders a pending
     request's check phrase.
  4. A wrong secret in the page form is refused the same way.
  5. The house side still works: the header presents the secret
     for reading the card, the page form presents it for the
     ceremony posts, and confirm/revoke take effect.
  6. A node with no house secret configured fails closed on the
     page too: no ceremony form is offered and posts refuse.

Writes door-page-checks.json into the run directory and exits
nonzero on any failure. Spend: $0.
"""
import base64
import hashlib
import http.cookiejar
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUN = os.environ.get("TELLHOUSE_DOOR_PAGE_RUN", "/tmp/lane-doorpage-run")
DOTNET = os.path.expanduser("~/.dotnet/dotnet")
NODE = "http://127.0.0.1:7451"
NODE_NOSECRET = "http://127.0.0.1:7452"
SECRET = "door-page-checks-house-secret"

checks = []


def check(name, ok, detail=""):
    checks.append({"name": name, "passed": bool(ok), "detail": str(detail)[:400]})
    print(("PASS " if ok else "FAIL ") + name + ((" :: " + str(detail)[:200]) if detail and not ok else ""))


def b64url(b):
    return base64.urlsafe_b64encode(b).decode().rstrip("=")


def raw(base, method, path, body=None, headers=None):
    data = None
    h = {}
    if headers:
        h.update(headers)
    if body is not None:
        data = json.dumps(body).encode()
        h.setdefault("Content-Type", "application/json")
    r = urllib.request.Request(base + path, data=data, headers=h, method=method)
    try:
        with urllib.request.urlopen(r, timeout=25) as resp:
            return resp.status, resp.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()
    except Exception as e:
        return 0, str(e)


class Page:
    def __init__(self, base):
        self.base = base
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(self.jar))

    def get(self, path, headers=None):
        r = urllib.request.Request(self.base + path, headers=headers or {})
        with self.opener.open(r, timeout=25) as resp:
            return resp.status, resp.read().decode()

    def token(self, html):
        m = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html)
        return m.group(1) if m else ""

    def any_token(self):
        # A token any web client can obtain: the front door renders
        # a post form to every visitor, so its token is what an
        # unauthenticated caller would present alongside a forged
        # ceremony post. The ceremony handler's own authorization
        # is what must answer it.
        _, html = self.get("/")
        return self.token(html)

    def post_form(self, path, fields):
        data = urllib.parse.urlencode(fields).encode()
        r = urllib.request.Request(self.base + path, data=data, method="POST")
        try:
            with self.opener.open(r, timeout=25) as resp:
                return resp.status, resp.read().decode()
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode()


def wait_up(base, tries=90):
    for _ in range(tries):
        s, _ = raw(base, "GET", "/")
        if s and s < 500:
            return True
        time.sleep(0.5)
    return False


def start_node(name, port_base, secret):
    appdir = os.path.join(RUN, name)
    shutil.copytree(os.path.join(ROOT, "src/HomeNode/bin/Debug/net10.0"), appdir)
    env = dict(os.environ)
    if secret is not None:
        env["HOME_NODE_HOUSE_SECRET"] = secret
    env["ASPNETCORE_URLS"] = port_base
    env["ASPNETCORE_ENVIRONMENT"] = "Development"
    log = open(os.path.join(RUN, name + ".log"), "w")
    proc = subprocess.Popen([DOTNET, os.path.join(appdir, "HomeNode.dll")],
                            cwd=appdir, env=env, stdout=log, stderr=subprocess.STDOUT)
    return proc, os.path.join(appdir, "home-node.db")


def main():
    subprocess.run([DOTNET, "build", os.path.join(ROOT, "src/HomeNode/HomeNode.csproj"),
                    "-c", "Debug", "--nologo", "-v", "q"],
                   check=True, capture_output=True)
    shutil.rmtree(RUN, ignore_errors=True)
    os.makedirs(RUN)
    proc, db = start_node("node-app", NODE, SECRET)
    proc2 = None
    try:
        if not wait_up(NODE):
            check("node boots", False)
            return 1

        def sql(q):
            con = sqlite3.connect(db)
            rows = con.execute(q).fetchall()
            con.close()
            return rows

        people = {name.split()[0]: pid for pid, name in
                  sql("select Id, DisplayName from Persons")}
        margaret, lisa = people["Margaret"], people["Lisa"]

        s, _ = raw(NODE, "POST", "/door/invite",
                   {"inviter": "Margaret", "intended": "Lisa", "level": "Standard"})
        check("control: the HTTP door refuses an invite without the house secret", s == 403, s)

        s, body = raw(NODE, "POST", "/door/invite",
                      {"inviter": "Margaret", "intended": "Lisa", "level": "Standard"},
                      {"X-House-Secret": SECRET})
        code = json.loads(body).get("invite_code") or json.loads(body).get("code")
        check("setup: the house side mints an invite through the gated door", s == 200 and bool(code), body[:200])

        # Knock with a fresh keypair (the joiner side needs the code).
        subprocess.run(["openssl", "ecparam", "-name", "prime256v1", "-genkey",
                        "-noout", "-out", os.path.join(RUN, "joiner.pem")], check=True)
        der = subprocess.run(["openssl", "ec", "-in", os.path.join(RUN, "joiner.pem"),
                              "-pubout", "-outform", "DER"],
                             capture_output=True, check=True).stdout
        point = der[-65:]
        fp = hashlib.sha256(point).hexdigest()
        s, body = raw(NODE, "POST", "/door/knock", {
            "person": "Lisa", "invite_code": code,
            "public_key": b64url(point), "fingerprint": fp,
            "party_id": "member-lisa", "provider_claim": "DoorPageChecks",
            "credential_verifier": "door-page-checks-verifier"})
        conn_id = json.loads(body).get("connection_id") if s == 200 else None
        check("setup: the joiner knocks and a request is pending", s == 200 and conn_id is not None, body[:200])
        phrase_row = sql("select CheckPhrase from JoinRequests where Status='Pending'")
        phrase = phrase_row[0][0] if phrase_row else None

        page = Page(NODE)
        _, html = page.get("/Connections")
        check("an anonymous page view never renders the pending check phrase",
              phrase is not None and phrase not in html)
        check("an anonymous page view offers no ceremony form",
              'asp-page-handler="Confirm"' not in html and 'handler=Confirm' not in html
              and "Confirm this join" not in html)

        invites_before = sql("select count(*) from DoorInvites")[0][0]
        _, html = page.get("/Connections")
        s, _ = page.post_form("/Connections?handler=Invite", {
            "inviterPersonId": margaret, "intendedPersonId": lisa, "level": "Standard",
            "__RequestVerificationToken": page.any_token()})
        check("an anonymous invite post is refused and creates no invite",
              s == 403 and sql("select count(*) from DoorInvites")[0][0] == invites_before, s)

        jr = sql("select Id from JoinRequests where Status='Pending'")[0][0]
        s, _ = page.post_form("/Connections?handler=Confirm", {
            "joinRequestId": jr, "inviterPersonId": margaret, "personId": lisa,
            "checkPhrase": phrase or "", "__RequestVerificationToken": page.any_token()})
        confirm_status = s
        con = sqlite3.connect(db)
        status = con.execute("select Status from JoinRequests where Id=?", (jr,)).fetchone()[0]
        conns = con.execute("select count(*) from HouseConnections where PersonId=?", (lisa,)).fetchone()[0]
        con.close()
        check("an anonymous confirm post is refused (403); the request is still Pending and Lisa has no connection",
              confirm_status == 403 and status == "Pending" and conns == 0,
              f"{confirm_status}/{status}/{conns}")

        s, _ = page.post_form("/Connections?handler=Decline", {
            "joinRequestId": jr, "inviterPersonId": margaret,
            "__RequestVerificationToken": page.any_token()})
        con = sqlite3.connect(db)
        status = con.execute("select Status from JoinRequests where Id=?", (jr,)).fetchone()[0]
        con.close()
        check("an anonymous decline post is refused; the request stays pending",
              s == 403 and status == "Pending", f"{s}/{status}")

        s, _ = page.post_form("/Connections?handler=Confirm", {
            "joinRequestId": jr, "inviterPersonId": margaret, "personId": lisa,
            "checkPhrase": phrase or "", "houseSecret": "a wrong secret",
            "__RequestVerificationToken": page.any_token()})
        con = sqlite3.connect(db)
        status = con.execute("select Status from JoinRequests where Id=?", (jr,)).fetchone()[0]
        con.close()
        check("a confirm post carrying a wrong house secret is refused the same way",
              s == 403 and status == "Pending", f"{s}/{status}")

        _, html = page.get("/Connections", {"X-House-Secret": SECRET})
        check("the house side, presenting the secret in the header, is shown the check phrase",
              phrase is not None and phrase in html)

        s, html = page.post_form("/Connections?handler=Confirm", {
            "joinRequestId": jr, "inviterPersonId": margaret, "personId": lisa,
            "checkPhrase": phrase or "", "houseSecret": SECRET,
            "__RequestVerificationToken": page.token(html)})
        con = sqlite3.connect(db)
        row = con.execute("select Standing, Fingerprint from HouseConnections where PersonId=?",
                          (lisa,)).fetchone()
        con.close()
        check("the house side confirms through the page form; the connection is Active under the joiner's key",
              row is not None and row[0] == "Active" and row[1] == fp, f"{s}/{row}")

        _, html = page.get("/Connections")
        s, _ = page.post_form("/Connections?handler=Revoke", {
            "connectionId": conn_id, "__RequestVerificationToken": page.any_token()})
        con = sqlite3.connect(db)
        standing = con.execute("select Standing from HouseConnections where ConnectionId=?",
                               (conn_id,)).fetchone()[0]
        con.close()
        check("an anonymous revoke post is refused; the connection keeps its standing",
              s == 403 and standing == "Active", f"{s}/{standing}")

        s, html = page.post_form("/Connections?handler=Revoke", {
            "connectionId": conn_id, "houseSecret": SECRET,
            "__RequestVerificationToken": page.any_token()})
        con = sqlite3.connect(db)
        standing = con.execute("select Standing from HouseConnections where ConnectionId=?",
                               (conn_id,)).fetchone()[0]
        con.close()
        check("the house side revokes through the page form; the standing is Revoked",
              standing == "Revoked", f"{s}/{standing}")

        # A node with no house secret configured fails closed.
        proc2, _db2 = start_node("node-nosecret-app", NODE_NOSECRET, None)
        if wait_up(NODE_NOSECRET):
            page2 = Page(NODE_NOSECRET)
            _, html = page2.get("/Connections")
            check("a node with no house secret configured offers no ceremony form on the page",
                  "Open the house side" in html and "Confirm this join" not in html)
            s, _ = page2.post_form("/Connections?handler=Invite", {
                "inviterPersonId": margaret, "intendedPersonId": lisa, "level": "Standard",
                "__RequestVerificationToken": page2.any_token()})
            check("a node with no house secret configured refuses page ceremony posts",
                  s == 403, s)
        else:
            check("the no-secret node boots", False)
    finally:
        for p in (proc, proc2):
            if p is not None:
                p.terminate()
                try:
                    p.wait(timeout=10)
                except Exception:
                    p.kill()
    with open(os.path.join(RUN, "door-page-checks.json"), "w") as f:
        json.dump({"checks": checks,
                   "passed": sum(1 for c in checks if c["passed"]),
                   "total": len(checks)}, f, indent=2)
    failed = [c for c in checks if not c["passed"]]
    print(f"\nDoor page checks: {len(checks) - len(failed)} of {len(checks)} passed.")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
