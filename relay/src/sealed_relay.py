"""Tellhouse sealed relay: a dumb store-and-forward service.

Behaviour, and nothing more:
  accept a sealed packet, queue it for a stated short window,
  forward it to the polling destination, drop it.

The wire packet and envelope shapes are the protocol contract as
found in progress (see CONTRACT.md for the reconciliation
notes): a packet is exactly the keys ciphertext, envelope and
signature; the envelope is exactly the eight relay-visible
fields. The relay carries those bytes without interpreting the
signature and without any ability to open the ciphertext. It
holds no key material of any kind, it imports no routine that
could open a sealed body, it keeps no record database, and it
logs no content.

Its queue entries, queue snapshot and log lines carry only the
envelope fields, the relay timing fields (received_at and
expires_at, the relay timing fields from the relay side, which
the packet view used by the regression checks asks for as relay
received and delivered times) and the carried packet bytes
themselves.

It may check that a connection exists and is in good standing,
enforce size and rate limits, and expire its queue on a stated
timer. It may not confirm a join, judge an audience, answer a
question, or perform any part of the door. A knock packet is
queued like any other packet; queuing it changes no standing
and confirms nothing.

Standard library only. No third party imports.
"""

import base64
import hashlib
import json
import os
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# ------------------------------------------------------------------
# Stated limits and windows (public, in README.md and CONTRACT.md)
# ------------------------------------------------------------------
SESSION_TTL_SECONDS = 60            # delivery_mode "session"
STORE_AND_FORWARD_TTL_SECONDS = 300  # delivery_mode "store_and_forward"
MAX_CIPHERTEXT_BYTES = 65536        # protocol maximum ciphertext length
RATE_LIMIT_PER_MINUTE = 30          # accepted packets per connection
SEEN_ID_RETENTION_SECONDS = 600     # replay window for packet ids
SIGNATURE_BYTES = 64                # protocol signature wire length

# The exact protocol envelope fields: the relay-visible list.
ENVELOPE_FIELDS = (
    "packet_id",
    "household_endpoint",
    "connection_id",
    "envelope_class",
    "delivery_mode",
    "ciphertext_length",
    "issued_at",
    "ciphertext_hash",
)

# Relay timing: when the relay received a packet and when its
# queue window ends. No other relay generated field exists.
TIMING_FIELDS = ("received_at", "expires_at")

ALLOWED_METADATA_FIELDS = ENVELOPE_FIELDS + TIMING_FIELDS

ENVELOPE_CLASSES = ("knock", "session", "store_and_forward")
DELIVERY_MODES = ("session", "store_and_forward")

# Keys that must never appear in a submitted envelope. Their
# presence is a rejection, not a silent strip.
FORBIDDEN_ENVELOPE_KEYS = (
    "material_class",
    "materialclass",
    "packet_type",
    "sender",
    "sender_name",
    "sender_id",
    "recipient_id",
    "plaintext",
    "text",
    "body",
    "check_phrase",
    "audience",
    "proposal",
)

PACKET_KEYS = ("ciphertext", "envelope", "signature")


def _now():
    return int(time.time())


def sha256_hex(data):
    return hashlib.sha256(data).hexdigest()


def b64url_encode(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def b64url_decode(text):
    if not isinstance(text, str):
        raise ValueError("not text")
    raw = text.encode("ascii")
    for ch in raw:
        ok = (65 <= ch <= 90) or (97 <= ch <= 122) or \
            (48 <= ch <= 57) or ch in (45, 95)
        if not ok:
            raise ValueError("bad base64url character")
    pad = (-len(raw)) % 4
    return base64.urlsafe_b64decode(raw + b"=" * pad)


class Reject(Exception):
    def __init__(self, status, reason):
        super().__init__(reason)
        self.status = status
        self.reason = reason


class ConnectionRegistry:
    """Standing only: connection id, household, role, token hash,
    standing. Raw tokens are never stored; only their SHA-256
    hash is loaded from the operator supplied config file. Roles
    are node or member and exist only so the relay can forward a
    household bucket to the right side (see CONTRACT.md)."""

    def __init__(self, connections):
        self._by_id = {}
        for c in connections:
            self._by_id[c["connection_id"]] = {
                "household_endpoint": c["household_endpoint"],
                "token_sha256": c["token_sha256"],
                "standing": c.get("standing", "good"),
                "role": c.get("role", "member"),
            }

    def check(self, connection_id, token):
        entry = self._by_id.get(connection_id)
        if entry is None or token is None:
            return False
        if entry["standing"] != "good":
            return False
        return sha256_hex(token.encode("utf-8")) == entry["token_sha256"]

    def entry(self, connection_id):
        return self._by_id.get(connection_id)

    def node_for(self, household_endpoint):
        for cid, entry in self._by_id.items():
            if entry["household_endpoint"] == household_endpoint and \
                    entry["role"] == "node":
                return cid
        return None

    def standing_snapshot(self):
        return {cid: dict(e) for cid, e in self._by_id.items()}


class RelayLog:
    """JSON lines log. Only envelope fields, relay timing, an
    event word and a reason code are ever written. Carried bytes,
    signatures and tokens never appear."""

    LOG_FIELDS = ("event", "reason") + ALLOWED_METADATA_FIELDS

    def __init__(self, path):
        self._path = path
        self._lock = threading.Lock()
        if path:
            os.makedirs(os.path.dirname(os.path.abspath(path)),
                        exist_ok=True)

    def write(self, event, reason=None, entry=None):
        record = {"event": event, "reason": reason}
        if entry is not None:
            for field in ENVELOPE_FIELDS:
                record[field] = entry["envelope"][field]
            for field in TIMING_FIELDS:
                record[field] = entry[field]
        line = json.dumps(record, sort_keys=True)
        with self._lock:
            if self._path:
                with open(self._path, "a", encoding="utf-8") as fh:
                    fh.write(line + "\n")
            else:
                print(line, flush=True)


class RelayQueue:
    """In memory queue with an inspectable snapshot file.

    The snapshot is the queue itself written out for inspection;
    it is not a record database. Entries leave it exactly three
    ways: forwarded on a destination poll, expired on the stated
    timer, or never admitted at all (rejections never enter it).

    Which connection posted a packet is kept only in process
    memory (the _posters map), only to forward each packet to
    the other side of its household. It is never written to the
    snapshot, the log or the relay view. Packets reloaded from a
    snapshot after a restart are treated as member posted.
    """

    def __init__(self, snapshot_path, log):
        self._entries = {}
        self._posters = {}
        self._seen = {}
        self._lock = threading.Lock()
        self._snapshot_path = snapshot_path
        self._log = log
        self._load_snapshot()

    def _load_snapshot(self):
        if not self._snapshot_path or \
                not os.path.exists(self._snapshot_path):
            return
        try:
            with open(self._snapshot_path, "r", encoding="utf-8") as fh:
                data = json.load(fh)
        except (ValueError, OSError):
            return
        now = _now()
        for entry in data.get("entries", []):
            if entry.get("expires_at", 0) > now:
                pid = entry["envelope"]["packet_id"]
                self._entries[pid] = entry
                self._seen[pid] = now + SEEN_ID_RETENTION_SECONDS

    def _write_snapshot(self):
        if not self._snapshot_path:
            return
        os.makedirs(os.path.dirname(
            os.path.abspath(self._snapshot_path)), exist_ok=True)
        tmp = self._snapshot_path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump({"entries": list(self._entries.values())}, fh,
                      sort_keys=True)
        os.replace(tmp, self._snapshot_path)

    def expire(self):
        now = _now()
        dropped = []
        with self._lock:
            for pid in list(self._entries):
                if self._entries[pid]["expires_at"] <= now:
                    dropped.append(self._entries.pop(pid))
                    self._posters.pop(pid, None)
            for pid in list(self._seen):
                if self._seen[pid] <= now:
                    del self._seen[pid]
            if dropped:
                self._write_snapshot()
        for entry in dropped:
            self._log.write("dropped_expired", entry=entry)
        return len(dropped)

    def add(self, entry, poster_connection_id):
        pid = entry["envelope"]["packet_id"]
        with self._lock:
            if pid in self._entries or pid in self._seen:
                raise Reject(409, "duplicate_packet_id")
            self._entries[pid] = entry
            self._posters[pid] = poster_connection_id
            self._seen[pid] = _now() + SEEN_ID_RETENTION_SECONDS
            self._write_snapshot()
        self._log.write("accepted", entry=entry)

    def take_matching(self, predicate):
        """Forward: return matching entries and drop them."""
        self.expire()
        taken = []
        with self._lock:
            for pid in list(self._entries):
                entry = self._entries[pid]
                if predicate(entry, self._posters.get(pid)):
                    taken.append(self._entries.pop(pid))
                    self._posters.pop(pid, None)
            if taken:
                self._write_snapshot()
        for entry in taken:
            self._log.write("forwarded", entry=entry)
        return taken

    def view(self):
        """Relay view: metadata only, never carried bytes and
        never the signature."""
        self.expire()
        with self._lock:
            out = []
            for e in self._entries.values():
                row = dict(e["envelope"])
                for field in TIMING_FIELDS:
                    row[field] = e[field]
                out.append(row)
            return out

    def entries(self):
        with self._lock:
            return list(self._entries.values())


def validate_and_build(raw):
    """Validate a submitted wire packet against the protocol
    packet shape and build a queue entry. The entry is the wire
    packet itself plus the two relay timing fields."""
    if not isinstance(raw, dict):
        raise Reject(400, "not_an_object")
    if set(raw.keys()) != set(PACKET_KEYS):
        raise Reject(400, "packet_keys_must_be_exactly_ciphertext_"
                          "envelope_signature")
    env = raw["envelope"]
    if not isinstance(env, dict):
        raise Reject(400, "envelope_not_an_object")
    for bad in FORBIDDEN_ENVELOPE_KEYS:
        if bad in env:
            raise Reject(400, "forbidden_field")
    if set(env.keys()) != set(ENVELOPE_FIELDS):
        raise Reject(400, "envelope_fields_must_be_exactly_the_relay_visible_list")
    # Exact JSON types for every envelope field. Python equality
    # and isinstance alone are not enough here: True is an int
    # instance and equals 1, and 96.0 equals 96, so a boolean or
    # float field would otherwise pass the numeric checks below.
    # type(...) is int excludes bool, whose type is
    # bool, and excludes float; the text fields require type str.
    for field in ("packet_id", "household_endpoint", "connection_id"):
        value = env[field]
        if type(value) is not str or not value or \
                len(value) > 128:
            raise Reject(400, "bad_id_field")
    if type(env["envelope_class"]) is not str or \
            env["envelope_class"] not in ENVELOPE_CLASSES:
        raise Reject(400, "bad_envelope_class")
    if type(env["delivery_mode"]) is not str or \
            env["delivery_mode"] not in DELIVERY_MODES:
        raise Reject(400, "bad_delivery_mode")
    if type(env["issued_at"]) is not int or env["issued_at"] <= 0:
        raise Reject(400, "bad_issued_at")
    if type(env["ciphertext_length"]) is not int:
        raise Reject(400, "bad_ciphertext_length")
    if type(env["ciphertext_hash"]) is not str:
        raise Reject(400, "bad_hash_encoding")
    if type(raw["ciphertext"]) is not str:
        raise Reject(400, "bad_ciphertext_encoding")
    if type(raw["signature"]) is not str:
        raise Reject(400, "bad_signature_encoding")
    try:
        blob = b64url_decode(raw["ciphertext"])
    except Exception:
        raise Reject(400, "bad_ciphertext_encoding")
    if len(blob) == 0 or len(blob) > MAX_CIPHERTEXT_BYTES:
        raise Reject(413, "ciphertext_size_out_of_bounds")
    if env["ciphertext_length"] != len(blob):
        raise Reject(400, "length_mismatch")
    try:
        stated_hash = b64url_decode(env["ciphertext_hash"])
    except Exception:
        raise Reject(400, "bad_hash_encoding")
    if len(stated_hash) != 32:
        raise Reject(400, "bad_hash_length")
    if b64url_encode(hashlib.sha256(blob).digest()) != \
            env["ciphertext_hash"]:
        raise Reject(400, "hash_mismatch")
    try:
        signature = b64url_decode(raw["signature"])
    except Exception:
        raise Reject(400, "bad_signature_encoding")
    if len(signature) != SIGNATURE_BYTES:
        # Shape only. The relay never verifies a signature; that
        # check belongs to the endpoints, which hold the keys.
        raise Reject(400, "bad_signature_length")
    now = _now()
    ttl = SESSION_TTL_SECONDS if env["delivery_mode"] == "session" \
        else STORE_AND_FORWARD_TTL_SECONDS
    return {
        "envelope": {f: env[f] for f in ENVELOPE_FIELDS},
        "ciphertext": raw["ciphertext"],
        "signature": raw["signature"],
        "received_at": now,
        "expires_at": now + ttl,
    }


def public_entry(entry):
    out = dict(entry["envelope"])
    for field in TIMING_FIELDS:
        out[field] = entry[field]
    out["ciphertext"] = entry["ciphertext"]
    out["signature"] = entry["signature"]
    return out


class RelayService:
    def __init__(self, registry, queue, log):
        self.registry = registry
        self.queue = queue
        self.log = log
        self._rate = {}
        self._rate_lock = threading.Lock()

    def rate_ok(self, connection_id):
        now = _now()
        with self._rate_lock:
            stamps = [t for t in self._rate.get(connection_id, [])
                      if now - t < 60]
            if len(stamps) >= RATE_LIMIT_PER_MINUTE:
                self._rate[connection_id] = stamps
                return False
            stamps.append(now)
            self._rate[connection_id] = stamps
            return True

    def accept(self, auth_connection, auth_token, raw):
        if not self.registry.check(auth_connection, auth_token):
            self.log.write("rejected",
                           reason="connection_not_in_standing")
            raise Reject(403, "connection_not_in_standing")
        if not self.rate_ok(auth_connection):
            self.log.write("rejected", reason="rate_limited")
            raise Reject(429, "rate_limited")
        entry = validate_and_build(raw)
        poster = self.registry.entry(auth_connection)
        if poster["household_endpoint"] != \
                entry["envelope"]["household_endpoint"]:
            self.log.write("rejected", reason="household_mismatch")
            raise Reject(403, "household_mismatch")
        self.queue.add(entry, auth_connection)
        return {"packet_id": entry["envelope"]["packet_id"],
                "expires_at": entry["expires_at"]}

    def poll(self, auth_connection, auth_token, household_endpoint):
        """Forwarding rule (CONTRACT.md):
        the household node collects every queued packet for its
        household that it did not post itself; a member collects
        the queued packets posted by the node whose envelope
        connection_id is the member's own connection. Anything
        else stays queued until it expires. Forwarded packets
        are dropped from the queue."""
        if not self.registry.check(auth_connection, auth_token):
            self.log.write("rejected", reason="poll_not_authorized")
            raise Reject(403, "poll_not_authorized")
        poller = self.registry.entry(auth_connection)
        if poller["household_endpoint"] != household_endpoint:
            self.log.write("rejected", reason="poll_not_authorized")
            raise Reject(403, "poll_not_authorized")
        node_id = self.registry.node_for(household_endpoint)
        if poller["role"] == "node":
            def wanted(entry, poster):
                return (entry["envelope"]["household_endpoint"] ==
                        household_endpoint and poster != auth_connection)
        else:
            def wanted(entry, poster):
                env = entry["envelope"]
                return (env["household_endpoint"] == household_endpoint
                        and poster == node_id
                        and env["connection_id"] == auth_connection)
        taken = self.queue.take_matching(wanted)
        return [public_entry(e) for e in taken]


def make_handler(service):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):
            # The default server log would record request paths
            # and could record query strings. It is silenced; the
            # only log is RelayLog, with its allowlisted fields.
            pass

        def _send(self, status, obj):
            self.close_connection = True
            body = json.dumps(obj, sort_keys=True).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _auth(self):
            return (self.headers.get("X-Connection-Id"),
                    self.headers.get("X-Connection-Token"))

        def do_POST(self):
            if self.path != "/packets":
                # No join confirmation, no answer, no audience or
                # door endpoint exists on this service at all.
                self._send(404, {"error": "not_found"})
                return
            try:
                length = int(self.headers.get("Content-Length") or 0)
            except ValueError:
                length = 0
            if length > MAX_CIPHERTEXT_BYTES * 3 + 8192:
                self._send(413, {"error": "too_large"})
                return
            raw_body = self.rfile.read(length) if length else b""
            try:
                raw = json.loads(raw_body.decode("utf-8"))
            except Exception:
                self._send(400, {"error": "bad_json"})
                return
            conn, token = self._auth()
            try:
                result = service.accept(conn, token, raw)
            except Reject as r:
                self._send(r.status, {"error": r.reason})
                return
            self._send(202, result)

        def do_GET(self):
            if self.path == "/health":
                self._send(200, {
                    "status": "ok",
                    "session_ttl_seconds": SESSION_TTL_SECONDS,
                    "store_and_forward_ttl_seconds":
                        STORE_AND_FORWARD_TTL_SECONDS,
                    "max_ciphertext_bytes": MAX_CIPHERTEXT_BYTES,
                    "rate_limit_per_minute": RATE_LIMIT_PER_MINUTE,
                })
                return
            if self.path == "/relay/view":
                self._send(200, {"packets": service.queue.view()})
                return
            if self.path.startswith("/inbox"):
                from urllib.parse import urlparse, parse_qs
                qs = parse_qs(urlparse(self.path).query)
                household = (qs.get("household_endpoint") or [""])[0]
                conn, token = self._auth()
                try:
                    packets = service.poll(conn, token, household)
                except Reject as r:
                    self._send(r.status, {"error": r.reason})
                    return
                self._send(200, {"packets": packets})
                return
            self._send(404, {"error": "not_found"})

    return Handler


def build_service(config_path, runtime_dir):
    with open(config_path, "r", encoding="utf-8") as fh:
        config = json.load(fh)
    registry = ConnectionRegistry(config.get("connections", []))
    log = RelayLog(os.path.join(runtime_dir, "relay.log.jsonl")
                   if runtime_dir else None)
    queue = RelayQueue(os.path.join(runtime_dir, "queue_snapshot.json")
                       if runtime_dir else None, log)
    return RelayService(registry, queue, log)


def main():
    import argparse
    parser = argparse.ArgumentParser(description="Tellhouse sealed relay")
    parser.add_argument("--config", required=True)
    parser.add_argument("--runtime-dir", default="runtime")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8790)
    args = parser.parse_args()
    service = build_service(args.config, args.runtime_dir)

    def sweeper():
        while True:
            time.sleep(5)
            service.queue.expire()

    threading.Thread(target=sweeper, daemon=True).start()
    server = ThreadingHTTPServer((args.host, args.port),
                                 make_handler(service))
    print("sealed relay listening on %s:%d" % (args.host, args.port),
          flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
