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

The view is the relay operator's surface, not one of the house's surfaces: it shows envelope metadata only, never content, and in the deployed demo the traffic it shows is the fictional household's.

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

## As a service

The relay also ships as a single Docker service for a free
tier host. The build context is this `relay` directory. The
`Dockerfile` uses `python:3.12-slim`, sets `WORKDIR /app`,
copies `src/` to `/app/src/`, `demo-seed/` to
`/app/demo-seed/`, and `entrypoint.sh` to `/app/entrypoint.sh`,
exposes port 8790, and runs `/app/entrypoint.sh` as the
command. The relay uses the Python standard library only, so
the image installs no packages.

`entrypoint.sh` resolves three environment variables:

* `PORT`: the port the platform injects. The relay binds
  `0.0.0.0` on this port, defaulting to 8790 when `PORT` is
  unset.
* `RELAY_CONFIG`: path to the standing registry file,
  default `/tmp/relay/config.json`.
* `RELAY_RUNTIME_DIR`: path for the queue snapshot and the
  JSON lines log, default `/tmp/relay/runtime`.

Reseed rule: on an ephemeral disk the registry file vanishes
with the instance, so a fresh boot re-seeds the demo registry
from the image at `demo-seed/relay-config.json`. If a config
file is already present at the resolved path, the entrypoint
uses it as-is and never overwrites it. Operator state wins
over reseeding.

Single-instance free-tier note: this shape assumes one
instance, with the file registry on that instance disk.
Ephemeral restarts reseed deterministically, so the demo
household returns in the same state each time. A restart does
clear the relay queue snapshot handling and its in-memory
duplicate window, but duplicate rejection also lives in the
node's own registry, so a relay restart loses no replay
protection that matters.
