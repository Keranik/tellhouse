# Deployment wiring: drivers and evidence

This directory holds the build tooling that turned the relay,
the two stand-ins, and the node into deployable services, and
the evidence those runs produced. None of it is product code.
DEPLOY.md, at the repository root, is the deployment package
these runs verify.

## The demo seed ceremony

run_demo_seed_ceremony.py is an adapted copy of the
integration live leg driver
(lane-i-integration/run_live_leg.py), pointed
at a scratch run directory. It runs the real door ceremony
against the service-shaped programs: Lisa and Daniel knock, a
named inviter confirms with the check phrase (a wrong phrase
is refused first), both connections stand Active, sessions
open on the real challenge proof, a closed Consent valve
rejects a packet whole, a telling lands exactly once with
provenance, a house proposal is disposed by Daniel's
acknowledgement, and a Test Send with the relay killed queues
unrecorded. 30 checks.

The ceremony's resulting state was harvested as the demo seed
baked into the images:

- src/HomeNode/demo-seed/node-keys/ (the node's demo key),
  demo-snapshot.thb (the sealed snapshot of the record, taken
  by TellhouseBackup), and demo-recovery-phrase.txt.
- relay/demo-seed/relay-config.json (the registry, token
  SHA-256 hashes only).
- standins/LisaAssistant/demo-seed/store/ and
  standins/DanielAssistant/demo-seed/store/ (each stand-in's
  keys and joined state; its outbox, inbox, and sent files are
  runtime state and are not seeded).

The snapshot was verified at harvest: a restore into a fresh
database compared table by table against the ceremony record
reported every table equal (the one permitted difference in a
live restore, its announcement, is written by the reset path
itself).

Evidence: demo-seed-ceremony-evidence.json.

## The service journey

run_service_journey.py stages the four services the way their
Dockerfiles lay them out (published apps, entrypoint scripts,
baked demo seed) in fresh directories, configures them by
environment only, and runs the judge journey: clean boot of
all four, auto-session exchange with the recorded session
time simulated stale, a direct website telling, the website Test
Send through the real relay landing with provenance, a house
proposal disposed by Daniel's service, a relay outage queuing
unrecorded, a deterministic relay reseed, and a product reset
back to the determined state. 14 checks. Mock extraction; $0.

Evidence: service-journey-evidence.json.

## The canary batch, service shape

run_canary_service_shape.py is the integration canary batch
(lane-i-integration/run_canary_batch.py) with one change: the
relay is launched through relay/entrypoint.sh with the batch
config passed by environment, so the canary properties are
re-verified against the service-shaped relay. 19 checks.

Evidence: canary-service-shape-evidence.json.
