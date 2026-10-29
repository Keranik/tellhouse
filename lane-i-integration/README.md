# Integration check drivers

Drivers that run the parts of this tree together, end to end,
against the real components: the home node (src/HomeNode), the
sealed relay (relay/), and the two stand-in assistants
(standins/). Nothing here is a separate deliverable; these are
the check harnesses for the assembled demo.

## run_live_leg.py

The two real stand-ins against the real home node through the
real relay: door joins for both stand-ins through the node's
HTTP door adapter, session opens, Lisa's tellings landing on
the node record with provenance populated by the node intake,
Daniel's deliveries and proposal disposal by acknowledgement,
the Consent valve exercise against the node's standing Consent
state, the relay-stopped case queuing in Lisa's outbox marked
unrecorded, and the Test Send trigger firing through this real
path. Assertions about the record read the node's own SQLite
database. Mock extraction throughout; spend $0. Writes its
results to live-leg-evidence.json in this folder when run.

## run_canary_batch.py

One batch, one sitting, fresh seed, against the integrated
node. A canary telling carrying a unique phrase is sent through
Lisa's real stand-in over the real relay into the real home
node; afterwards the relay's files and views are searched for
the canary phrase, its fragments, and any event body text that
crossed, and two tampered-packet forms are run to show each is
rejected. Mock extraction; spend $0.

## run_beat4_live.py

The stated-turn batch in Live extraction mode: two front door
tellings in one node process, the trash turn (shaped by the
deterministic stated-turn pre-pass, so no API call is spent on
it) and one plain note telling (extracted by the live model,
the batch's one counted live API call). The turn is then
confirmed and its delivery checked: exactly one delivery, to
Daniel only. This is the one driver here that can spend live
API calls; it runs on the Contributor tier model with fictional
data only.

## What the assembled tree is

One home node application (src/HomeNode) holding the record,
the audit, extraction, the door, sealed intake, and the sealed
delivery outbox; the protocol library as its own project
(src/Tellhouse.Protocol) and vendored into the node; the relay
as its own Python service (relay/); two stand-in assistants as
their own projects (standins/); the sealed backup tool as its
own project (src/TellhouseBackup); and the earlier
single-process build (src/HouseholdCoordinator), kept as the
fallback. A fresh home node seeds 6 persons, 16 events, 23
audits, and 1 promise.
