"""the relay checks for the sealed relay.

Run: python3 -m unittest discover -s tests -v
Standard library only. No live calls: the server under test is
started in process on an ephemeral local port.

Packet fixtures follow the protocol wire shape as found in
progress in this tree: a packet is exactly ciphertext,
envelope and signature; the envelope is exactly the eight relay-visible
fields; ciphertext and signature are base64url without padding;
the ciphertext hash is the base64url SHA-256 of the ciphertext.
The sealed blobs here are derived bytes that stand in for the protocol
sealing in relay tests only; the relay never sees a plaintext.
"""

import ast
import hashlib
import json
import os
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
RELAY_DIR = os.path.dirname(HERE)
SRC = os.path.join(RELAY_DIR, "src", "sealed_relay.py")
sys.path.insert(0, os.path.join(RELAY_DIR, "src"))
import sealed_relay as relay  # noqa: E402

CANARY = "zxqvbn-canary-phrase-9182"
SENDER_NAME = "Canary Sender Name"
CHECK_PHRASE = "canary check phrase 4815"

NODE = "node-inbox-1"
LISA = "member-lisa-1"
DANIEL = "member-daniel-1"
TOKENS = {NODE: "node-token-test", LISA: "lisa-token-test",
          DANIEL: "daniel-token-test"}
ROLES = {NODE: "node", LISA: "member", DANIEL: "member"}
HOUSEHOLD = "household-demo"
ISSUED_AT = 1761700000


def sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def sealed_blob(seed_text, length=96):
    """Stand-in sealed bytes for relay tests: derived bytes that
    do not contain the seed text anywhere."""
    out = b""
    counter = 0
    while len(out) < length:
        out += hashlib.sha256(
            (seed_text + ":" + str(counter)).encode("utf-8")).digest()
        counter += 1
    return out[:length]


def make_packet(packet_id="pkt-1", connection_id=LISA, blob=None,
                envelope_class="session", delivery_mode="session"):
    if blob is None:
        blob = sealed_blob(CANARY + packet_id)
    signature = sealed_blob("sig:" + packet_id, length=64)
    envelope = {
        "packet_id": packet_id,
        "household_endpoint": HOUSEHOLD,
        "connection_id": connection_id,
        "envelope_class": envelope_class,
        "delivery_mode": delivery_mode,
        "ciphertext_length": len(blob),
        "issued_at": ISSUED_AT,
        "ciphertext_hash": relay.b64url_encode(
            hashlib.sha256(blob).digest()),
    }
    return {
        "ciphertext": relay.b64url_encode(blob),
        "envelope": envelope,
        "signature": relay.b64url_encode(signature),
    }


def write_config(path, revoked=()):
    connections = []
    for cid in (NODE, LISA, DANIEL):
        connections.append({
            "connection_id": cid,
            "household_endpoint": HOUSEHOLD,
            "token_sha256": sha(TOKENS[cid]),
            "standing": "revoked" if cid in revoked else "good",
            "role": ROLES[cid],
        })
    with open(path, "w", encoding="utf-8") as fh:
        json.dump({"connections": connections}, fh)


class HttpCase(unittest.TestCase):
    revoked = ()

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.runtime = os.path.join(cls.tmp.name, "runtime")
        os.makedirs(cls.runtime)
        config = os.path.join(cls.tmp.name, "config.json")
        write_config(config, revoked=cls.revoked)
        cls.service = relay.build_service(config, cls.runtime)
        cls.server = ThreadingHTTPServer(
            ("127.0.0.1", 0), relay.make_handler(cls.service))
        cls.port = cls.server.server_address[1]
        cls.thread = threading.Thread(target=cls.server.serve_forever,
                                      daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.tmp.cleanup()

    def call(self, method, path, conn=None, token=None, body=None):
        url = "http://127.0.0.1:%d%s" % (self.port, path)
        data = None
        headers = {"Content-Type": "application/json"}
        if conn:
            headers["X-Connection-Id"] = conn
        if token:
            headers["X-Connection-Token"] = token
        if body is not None:
            data = json.dumps(body).encode("utf-8")
        req = urllib.request.Request(url, data=data, headers=headers,
                                     method=method)
        try:
            with urllib.request.urlopen(req, timeout=5) as resp:
                return resp.status, json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            raw = e.read().decode("utf-8")
            try:
                return e.code, json.loads(raw)
            except ValueError:
                return e.code, {"raw": raw}

    def post_packet(self, packet, conn=LISA, token=None):
        return self.call("POST", "/packets", conn=conn,
                         token=TOKENS[conn] if token is None else token,
                         body=packet)

    def poll(self, conn=NODE, token=None):
        path = "/inbox?household_endpoint=%s" % HOUSEHOLD
        return self.call("GET", path, conn=conn,
                         token=TOKENS[conn] if token is None else token)


class TestAcceptForwardDrop(HttpCase):
    def test_accept_returns_202_with_expiry(self):
        status, body = self.post_packet(make_packet("acc-1"))
        self.assertEqual(status, 202)
        self.assertEqual(body["packet_id"], "acc-1")
        self.assertIn("expires_at", body)
        self.poll()  # drain

    def test_telling_forwarded_to_node_then_dropped(self):
        packet = make_packet("fwd-1")
        self.assertEqual(self.post_packet(packet)[0], 202)
        status, body = self.poll()
        self.assertEqual(status, 200)
        got = [p for p in body["packets"] if p["packet_id"] == "fwd-1"]
        self.assertEqual(len(got), 1)
        self.assertEqual(got[0]["ciphertext"], packet["ciphertext"])
        self.assertEqual(got[0]["signature"], packet["signature"])
        status, body = self.poll()
        self.assertEqual([p for p in body["packets"]
                          if p["packet_id"] == "fwd-1"], [])

    def test_forwarded_packet_metadata_is_exactly_f3_plus_carried(self):
        packet = make_packet("fwd-2")
        self.post_packet(packet)
        _, body = self.poll()
        got = [p for p in body["packets"] if p["packet_id"] == "fwd-2"][0]
        self.assertEqual(set(got.keys()),
                         set(relay.ALLOWED_METADATA_FIELDS) |
                         {"ciphertext", "signature"})
        self.assertEqual(got["ciphertext_hash"],
                         packet["envelope"]["ciphertext_hash"])

    def test_delivery_forwarded_to_its_member_only(self):
        packet = make_packet("del-1", connection_id=DANIEL)
        self.assertEqual(self.post_packet(packet, conn=NODE)[0], 202)
        # Other member and the node itself collect nothing.
        self.assertEqual(self.poll(conn=LISA)[1]["packets"], [])
        self.assertEqual(self.poll(conn=NODE)[1]["packets"], [])
        _, body = self.poll(conn=DANIEL)
        got = [p for p in body["packets"] if p["packet_id"] == "del-1"]
        self.assertEqual(len(got), 1)
        self.assertEqual(got[0]["ciphertext"], packet["ciphertext"])

    def test_member_telling_not_forwarded_to_other_members(self):
        self.post_packet(make_packet("fwd-3"))
        self.assertEqual(self.poll(conn=DANIEL)[1]["packets"], [])
        self.assertEqual(self.poll(conn=LISA)[1]["packets"], [])
        self.poll()  # drain as node

    def test_relay_view_shows_metadata_only(self):
        self.post_packet(make_packet("view-1"))
        status, body = self.call("GET", "/relay/view")
        self.assertEqual(status, 200)
        got = [p for p in body["packets"] if p["packet_id"] == "view-1"]
        self.assertEqual(len(got), 1)
        self.assertEqual(set(got[0].keys()),
                         set(relay.ALLOWED_METADATA_FIELDS))
        self.poll()  # drain

    def test_health_states_the_windows(self):
        status, body = self.call("GET", "/health")
        self.assertEqual(status, 200)
        self.assertEqual(body["session_ttl_seconds"], 60)
        self.assertEqual(body["store_and_forward_ttl_seconds"], 300)
        self.assertEqual(body["max_ciphertext_bytes"], 65536)


class TestRejections(HttpCase):
    def test_unknown_connection_rejected(self):
        status, _ = self.call("POST", "/packets", conn="nobody",
                              token="x", body=make_packet("rej-1"))
        self.assertEqual(status, 403)

    def test_wrong_token_rejected(self):
        status, _ = self.post_packet(make_packet("rej-2"),
                                     token="not-the-token")
        self.assertEqual(status, 403)

    def test_missing_auth_rejected(self):
        status, _ = self.call("POST", "/packets",
                              body=make_packet("rej-3"))
        self.assertEqual(status, 403)

    def test_extra_packet_key_rejected(self):
        packet = make_packet("rej-4")
        packet["note"] = "hello"
        status, body = self.post_packet(packet)
        self.assertEqual(status, 400)

    def test_forbidden_envelope_fields_rejected_whole(self):
        for extra in ("material_class", "packet_type", "sender_id",
                      "recipient_id", "check_phrase", "audience"):
            packet = make_packet("rej-5-" + extra)
            packet["envelope"][extra] = CANARY
            status, body = self.post_packet(packet)
            self.assertEqual(status, 400, extra)
            self.assertEqual(body["error"], "forbidden_field", extra)

    def test_missing_envelope_field_rejected(self):
        packet = make_packet("rej-6")
        del packet["envelope"]["ciphertext_hash"]
        status, _ = self.post_packet(packet)
        self.assertEqual(status, 400)

    def test_bad_envelope_class_rejected(self):
        packet = make_packet("rej-7", envelope_class="telling")
        status, _ = self.post_packet(packet)
        self.assertEqual(status, 400)

    def test_lane_k_envelope_classes_accepted(self):
        for cls_name in ("knock", "session", "store_and_forward"):
            packet = make_packet("cls-" + cls_name,
                                 envelope_class=cls_name)
            status, _ = self.post_packet(packet)
            self.assertEqual(status, 202, cls_name)
            self.poll()  # drain

    def test_hash_mismatch_rejected(self):
        packet = make_packet("rej-8")
        packet["envelope"]["ciphertext_hash"] = relay.b64url_encode(
            b"\x00" * 32)
        status, body = self.post_packet(packet)
        self.assertEqual(status, 400)
        self.assertEqual(body["error"], "hash_mismatch")

    def test_hex_hash_rejected_lane_k_uses_base64url(self):
        packet = make_packet("rej-9")
        blob = relay.b64url_decode(packet["ciphertext"])
        packet["envelope"]["ciphertext_hash"] = \
            hashlib.sha256(blob).hexdigest()
        status, _ = self.post_packet(packet)
        self.assertEqual(status, 400)

    def test_length_mismatch_rejected(self):
        packet = make_packet("rej-10")
        packet["envelope"]["ciphertext_length"] += 1
        status, body = self.post_packet(packet)
        self.assertEqual(status, 400)
        self.assertEqual(body["error"], "length_mismatch")

    def test_signature_wrong_length_rejected_on_shape(self):
        packet = make_packet("rej-11")
        packet["signature"] = relay.b64url_encode(b"\x01" * 32)
        status, body = self.post_packet(packet)
        self.assertEqual(status, 400)
        self.assertEqual(body["error"], "bad_signature_length")

    def test_oversize_ciphertext_rejected(self):
        packet = make_packet("rej-12",
                             blob=b"\x07" * (relay.MAX_CIPHERTEXT_BYTES + 1))
        status, _ = self.post_packet(packet)
        self.assertEqual(status, 413)

    def test_duplicate_packet_id_rejected_as_replay(self):
        packet = make_packet("rej-13")
        self.assertEqual(self.post_packet(packet)[0], 202)
        status, body = self.post_packet(packet)
        self.assertEqual(status, 409)
        self.assertEqual(body["error"], "duplicate_packet_id")
        self.poll()  # drain
        # Still rejected after forwarding, inside the replay window.
        status, _ = self.post_packet(packet)
        self.assertEqual(status, 409)

    def test_poll_with_wrong_token_rejected(self):
        status, _ = self.poll(conn=NODE, token="not-the-token")
        self.assertEqual(status, 403)

    def test_household_mismatch_rejected(self):
        packet = make_packet("rej-14")
        packet["envelope"]["household_endpoint"] = "other-household"
        status, body = self.post_packet(packet)
        self.assertEqual(status, 403)
        self.assertEqual(body["error"], "household_mismatch")


class TestRevokedStanding(HttpCase):
    revoked = (LISA,)

    def test_revoked_connection_cannot_send(self):
        status, _ = self.post_packet(make_packet("rev-1"))
        self.assertEqual(status, 403)


class TestNoDoorBehaviour(HttpCase):
    def test_door_and_answer_endpoints_do_not_exist(self):
        for path in ("/join", "/join/confirm", "/confirm", "/answer",
                     "/audience", "/door", "/proposals"):
            status, _ = self.call("POST", path, conn=LISA,
                                  token=TOKENS[LISA], body={})
            self.assertEqual(status, 404, path)
            status, _ = self.call("GET", path, conn=LISA,
                                  token=TOKENS[LISA])
            self.assertEqual(status, 404, path)

    def test_knock_is_queued_and_confirms_nothing(self):
        before = self.service.registry.standing_snapshot()
        packet = make_packet("knock-1", envelope_class="knock")
        status, body = self.post_packet(packet)
        self.assertEqual(status, 202)
        self.assertEqual(set(body.keys()), {"packet_id", "expires_at"})
        self.assertEqual(self.service.registry.standing_snapshot(),
                         before)
        self.poll()  # drain


class TestExpiryAndLimits(unittest.TestCase):
    def make_service(self, tmp):
        runtime = os.path.join(tmp, "runtime")
        os.makedirs(runtime)
        config = os.path.join(tmp, "config.json")
        write_config(config)
        return relay.build_service(config, runtime), runtime

    def test_store_and_forward_expires_on_its_window(self):
        with tempfile.TemporaryDirectory() as tmp:
            service, runtime = self.make_service(tmp)
            old = relay.STORE_AND_FORWARD_TTL_SECONDS
            relay.STORE_AND_FORWARD_TTL_SECONDS = 1
            try:
                packet = make_packet("exp-1",
                                     delivery_mode="store_and_forward")
                service.accept(LISA, TOKENS[LISA], packet)
            finally:
                relay.STORE_AND_FORWARD_TTL_SECONDS = old
            time.sleep(1.2)
            taken = service.poll(NODE, TOKENS[NODE], HOUSEHOLD)
            self.assertEqual(taken, [])
            with open(os.path.join(runtime, "relay.log.jsonl"),
                      encoding="utf-8") as fh:
                events = [json.loads(l)["event"] for l in fh if l.strip()]
            self.assertIn("dropped_expired", events)

    def test_rate_limit_rejects_beyond_the_stated_rate(self):
        with tempfile.TemporaryDirectory() as tmp:
            service, _runtime = self.make_service(tmp)
            old = relay.RATE_LIMIT_PER_MINUTE
            relay.RATE_LIMIT_PER_MINUTE = 3
            try:
                for i in range(3):
                    service.accept(LISA, TOKENS[LISA],
                                   make_packet("rate-%d" % i))
                with self.assertRaises(relay.Reject) as ctx:
                    service.accept(LISA, TOKENS[LISA],
                                   make_packet("rate-3"))
                self.assertEqual(ctx.exception.status, 429)
            finally:
                relay.RATE_LIMIT_PER_MINUTE = old

    def test_session_and_store_windows_differ_as_stated(self):
        self.assertEqual(relay.SESSION_TTL_SECONDS, 60)
        self.assertEqual(relay.STORE_AND_FORWARD_TTL_SECONDS, 300)


class TestBlindness(HttpCase):
    """The regression net blindness proof, run against the live
    service above plus the files it actually wrote."""

    def runtime_texts(self):
        texts = {}
        for name in os.listdir(self.runtime):
            path = os.path.join(self.runtime, name)
            if os.path.isfile(path):
                with open(path, "rb") as fh:
                    texts[name] = fh.read()
        return texts

    def test_canary_and_private_strings_absent_from_store_and_logs(self):
        packet = make_packet("blind-1", blob=sealed_blob(CANARY))
        self.post_packet(packet)
        texts = self.runtime_texts()
        self.assertIn("queue_snapshot.json", texts)
        self.assertIn("relay.log.jsonl", texts)
        for name, data in texts.items():
            for secret in (CANARY, SENDER_NAME, CHECK_PHRASE,
                           TOKENS[LISA], TOKENS[NODE]):
                self.assertNotIn(secret.encode(), data,
                                 "%s leaked into %s" % (secret, name))
        self.poll()  # drain

    def test_snapshot_entries_carry_only_the_wire_packet_plus_timing(self):
        self.post_packet(make_packet("blind-2"))
        with open(os.path.join(self.runtime, "queue_snapshot.json"),
                  encoding="utf-8") as fh:
            snapshot = json.load(fh)
        self.assertTrue(snapshot["entries"])
        for entry in snapshot["entries"]:
            self.assertEqual(set(entry.keys()),
                             {"ciphertext", "envelope", "signature",
                              "received_at", "expires_at"})
            self.assertEqual(set(entry["envelope"].keys()),
                             set(relay.ENVELOPE_FIELDS))
        self.poll()  # drain

    def test_log_lines_carry_only_allowlisted_keys_and_no_signature(self):
        packet = make_packet("blind-3")
        self.post_packet(packet)
        log_path = os.path.join(self.runtime, "relay.log.jsonl")
        with open(log_path, encoding="utf-8") as fh:
            raw_text = fh.read()
        lines = [json.loads(l) for l in raw_text.splitlines() if l.strip()]
        self.assertTrue(lines)
        allowed = set(relay.RelayLog.LOG_FIELDS)
        for line in lines:
            self.assertTrue(set(line.keys()) <= allowed, line)
        self.assertNotIn(packet["signature"], raw_text)
        self.assertNotIn(packet["ciphertext"], raw_text)
        self.poll()  # drain

    def test_no_database_file_is_created(self):
        self.post_packet(make_packet("blind-4"))
        for root, _dirs, files in os.walk(RELAY_DIR):
            if "tests" in root or "__pycache__" in root:
                continue
            for name in files:
                self.assertFalse(
                    name.endswith((".db", ".sqlite", ".sqlite3")),
                    "database file present: %s" % name)
        for name in os.listdir(self.runtime):
            self.assertFalse(name.endswith((".db", ".sqlite",
                                            ".sqlite3")), name)
        self.poll()  # drain

    def test_indistinguishable_session_packets(self):
        for i in range(3):
            blob = sealed_blob("purpose-seed-%d" % i, length=128)
            status, _ = self.post_packet(
                make_packet("ind-%d" % i, blob=blob))
            self.assertEqual(status, 202)
        _, body = self.call("GET", "/relay/view")
        got = [p for p in body["packets"]
               if p["packet_id"].startswith("ind-")]
        self.assertEqual(len(got), 3)
        shapes = {(p["envelope_class"], p["delivery_mode"],
                   p["ciphertext_length"]) for p in got}
        self.assertEqual(shapes, {("session", "session", 128)})
        key_sets = {tuple(sorted(p.keys())) for p in got}
        self.assertEqual(len(key_sets), 1)
        self.poll()  # drain

    def test_distinct_sealed_blobs_yield_distinct_stored_hashes(self):
        hashes = set()
        for i in range(2):
            packet = make_packet("nonce-%d" % i,
                                 blob=sealed_blob("nonce-check-%d" % i))
            hashes.add(packet["envelope"]["ciphertext_hash"])
            self.post_packet(packet)
        self.assertEqual(len(hashes), 2)
        self.poll()  # drain


class TestLaneKVectors(unittest.TestCase):
    """Real the protocol vector packets, copied into tests/fixtures
    from the protocol vectors file, must cross this relay unchanged:
    accepted, forwarded to the right side, byte identical."""

    @classmethod
    def setUpClass(cls):
        fixture = os.path.join(HERE, "fixtures", "lane_k_vectors.json")
        with open(fixture, encoding="utf-8") as fh:
            cls.data = json.load(fh)
        cls.tmp = tempfile.TemporaryDirectory()
        cls.runtime = os.path.join(cls.tmp.name, "runtime")
        os.makedirs(cls.runtime)
        cls.node_conn = "conn-node-test"
        cls.tokens = {cls.node_conn: "node-vec-token",
                      "conn-lisa-01": "lisa-vec-token",
                      "conn-daniel-01": "daniel-vec-token"}
        household = cls.data["household_endpoint"]
        connections = [{
            "connection_id": cls.node_conn,
            "household_endpoint": household,
            "token_sha256": sha(cls.tokens[cls.node_conn]),
            "standing": "good", "role": "node",
        }]
        for cid in ("conn-lisa-01", "conn-daniel-01"):
            connections.append({
                "connection_id": cid,
                "household_endpoint": household,
                "token_sha256": sha(cls.tokens[cid]),
                "standing": "good", "role": "member",
            })
        config = os.path.join(cls.tmp.name, "config.json")
        with open(config, "w", encoding="utf-8") as fh:
            json.dump({"connections": connections}, fh)
        cls.service = relay.build_service(config, cls.runtime)
        cls.household = household

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_all_four_vector_packets_cross_the_relay(self):
        packets = {p["name"]: p["packet"]
                   for p in self.data["packets"]}
        self.assertEqual(len(packets), 4)
        posters = {
            "telling_lisa_to_node": "conn-lisa-01",
            "acknowledgement_daniel_to_node": "conn-daniel-01",
            "delivery_node_to_daniel": self.node_conn,
            "delivery_node_to_lisa": self.node_conn,
        }
        for name, packet in packets.items():
            poster = posters[name]
            result = self.service.accept(poster, self.tokens[poster],
                                         packet)
            self.assertEqual(result["packet_id"],
                             packet["envelope"]["packet_id"], name)
        # Node collects the two member posted packets, unchanged.
        node_got = self.service.poll(self.node_conn,
                                     self.tokens[self.node_conn],
                                     self.household)
        node_ids = {p["packet_id"] for p in node_got}
        self.assertEqual(node_ids, {
            packets["telling_lisa_to_node"]["envelope"]["packet_id"],
            packets["acknowledgement_daniel_to_node"]
            ["envelope"]["packet_id"]})
        for p in node_got:
            original = [q["packet"] for q in self.data["packets"]
                        if q["packet"]["envelope"]["packet_id"]
                        == p["packet_id"]][0]
            self.assertEqual(p["ciphertext"], original["ciphertext"])
            self.assertEqual(p["signature"], original["signature"])
        # Each member collects exactly its own delivery, unchanged.
        for member, name in (("conn-daniel-01",
                              "delivery_node_to_daniel"),
                             ("conn-lisa-01", "delivery_node_to_lisa")):
            got = self.service.poll(member, self.tokens[member],
                                    self.household)
            self.assertEqual(len(got), 1, name)
            self.assertEqual(got[0]["ciphertext"],
                             packets[name]["ciphertext"])
            self.assertEqual(got[0]["signature"],
                             packets[name]["signature"])
        # Queue is now empty: everything was forwarded and dropped.
        self.assertEqual(self.service.queue.view(), [])


class TestStaticBlindness(unittest.TestCase):
    def test_source_imports_no_opening_routine(self):
        with open(SRC, encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        banned_modules = {"cryptography", "Crypto", "Cryptodome",
                          "nacl", "OpenSSL", "ssl"}
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                for alias in node.names:
                    self.assertNotIn(alias.name.split(".")[0],
                                     banned_modules)
            if isinstance(node, ast.ImportFrom):
                self.assertNotIn((node.module or "").split(".")[0],
                                 banned_modules)

    def test_source_defines_no_opening_function(self):
        with open(SRC, encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        banned_words = ("decrypt", "unseal", "open_packet",
                        "open_sealed", "unwrap_key", "private_key")
        for node in ast.walk(tree):
            if isinstance(node, (ast.FunctionDef, ast.ClassDef)):
                for word in banned_words:
                    self.assertNotIn(word, node.name.lower(),
                                     node.name)

    def test_no_key_material_in_project_files(self):
        markers = (b"BEGIN PRIVATE KEY", b"BEGIN RSA", b"PRIVATE KEY",
                   b"household_content_key", b"member_private")
        for name in ("src/sealed_relay.py", "config.example.json",
                     "README.md", "CONTRACT.md"):
            with open(os.path.join(RELAY_DIR, name), "rb") as fh:
                data = fh.read()
            for marker in markers:
                self.assertNotIn(marker, data, "%s in %s"
                                 % (marker, name))

    def test_relay_does_not_reference_the_monolith(self):
        with open(SRC, encoding="utf-8") as fh:
            source = fh.read()
        self.assertNotIn("HouseholdCoordinator", source)
        self.assertNotIn("household-coordinator.db", source)


if __name__ == "__main__":
    unittest.main()
