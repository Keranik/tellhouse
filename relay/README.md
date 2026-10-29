# Tellhouse sealed relay

A dumb store-and-forward service for sealed Tellhouse packets:
accept, queue with a stated short window, forward, drop. It is a
separate project, written against the protocol contract
(see CONTRACT.md). It does not reference, import or share a
store with the monolith in `src/`, and the monolith does not
reference it.

The relay knows that households talk, when, and roughly how
much. It does not know what anyone said, asked, shared or
decided. Its queue, its snapshot file and its logs carry only
the envelope fields in CONTRACT.md, the relay timing fields,
and the carried packet bytes themselves.

## Run

```
python3 src/sealed_relay.py --config config.json --runtime-dir runtime --port 8790
```

`config.json` follows `config.example.json`: a standing registry
of connection ids, household endpoints, a role (`node` or
`member`, used only for the forwarding rule in CONTRACT.md) and
SHA-256 hashes of connection tokens. Raw tokens are never
stored. No key material of any kind belongs in the config, the
environment or this directory, and none is used: this process
contains no routine that could open a sealed body, and its
checks prove that by scanning its own imports, definitions and
files.

Runtime output, both inspectable and both metadata only:

* `runtime/queue_snapshot.json`: the live queue, written on
  every change. It is the queue itself, not a record database.
  Each entry is the wire packet plus received_at and
  expires_at. Packets leave it on forward or on expiry.
* `runtime/relay.log.jsonl`: one JSON line per event, carrying
  only the envelope fields, the relay timing fields, an event
  word and a generic reason code. Ciphertext and signature
  values never appear in it.

## HTTP surface (complete)

| Method and path | Behaviour |
| --- | --- |
| POST /packets | Accept a protocol wire packet (sender standing checked). Returns 202 with packet_id and expires_at. |
| GET /inbox?household_endpoint=H | Destination poll (destination standing checked). Returns the packets the forwarding rule in CONTRACT.md assigns to the poller, and drops them. |
| GET /relay/view | Queued packets as metadata only: envelope fields plus relay timing, no carried bytes, no signature. |
| GET /health | Liveness plus the stated windows and limits from CONTRACT.md. |

There is no other endpoint. In particular there is no join,
confirm, answer, audience or door endpoint, and a knock packet
queued here confirms nothing and changes no standing.

## Checks

```
python3 -m unittest discover -s tests -v
```

The suite covers accept, forward and drop in both directions
(member telling to the node, node delivery to its member);
expiry on the stated windows; size, rate, hash, length,
signature shape, allowlist, standing, household, duplicate and
destination checks; the absence of any door behaviour; and the
blindness proofs: canary text, sender names, check phrases, raw
tokens, ciphertext values and signature values appear nowhere
in the queue snapshot or the logs, every stored envelope equals
the relay-visible field set exactly, every log line key set sits inside
the allowlist, no database file is created, no key material
exists in the project files or config, and the service source
imports no routine that could open a sealed body (an AST scan
of its own imports and definitions, not a comment claim).
