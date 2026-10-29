# Integration tree

Assembled by integrating the separate parts of the project
into one tree. Base: the home node application, because the
node is the record home. Merged in, in order, with no merge
conflicts: the protocol (final), the relay, the stand-ins,
the backup, restore and reset tool, the monolith fixes, and
the demo beats work. One further set of changes, and the
provenance shapes, were already ported into the node and are
not merged a second time. The observability work stays
separate; this tree's evidence work is this folder.

Composition, in plain terms: one home node application
(src/HomeNode) holding the record, the audit, extraction, the
door, sealed intake, and the sealed delivery outbox; the protocol
library as its own project (src/Tellhouse.Protocol) and vendored
into the node; the relay as its own Python service (relay/); two
stand-in assistants as their own projects (standins/); the sealed
backup tool as its own project (src/TellhouseBackup); and the
frozen monolith (src/HouseholdCoordinator) untouched in substance,
carrying the merged seeder and provenance work on its own line.

Node side integration work in this tree, beyond the merges:

- The seeder fixes and the six dinner provenance
  tellings ported into the node's DbSeeder (the monolith merges
  alone do not touch the node's seed). Integrated seed, audited
  index by index after the port: 16 events, 6 persons, 23 audits,
  1 promise, 3 event actions. Gauze quantity on hand 0, the
  RecordPending flag on the toilet paper event, the relay action
  on the tap note, the supply adjustment on the gauze event, and
  the promise closed by Daniel's home event all verified against
  a freshly seeded database.
- The node's household endpoint adopts the frozen contract's
  demo endpoint hh-demo-01 (the node's earlier household-{id}
  form matched no stand-in and no vector).
- Connection ids for the frozen scope parties are the contract's
  scope ids (member-lisa to conn-lisa-01, member-daniel to
  conn-daniel-01), assigned by DoorService.
- An HTTP door adapter over DoorService (the stand-ins' frozen
  surface: invite, knock, card, confirm, decline, status), a
  house proposal surface (queueing a proposal delivery is a named
  node operation), and a valve admin surface for the Consent
  exercise. The adapter adds no door logic; the ceremony, the
  check phrase, and standing enforcement are DoorService's.
- A relay loop (RelayLoopService) wiring the node's relay client,
  sealed intake, and sealed outbox to the real relay, using a
  plain IPv4, proxy-free HttpClient per the stand-ins' measured
  finding about this network.
- Beat 4: a deterministic stated-turn pre-pass in the node's
  extraction pipeline (the mock extraction shape, running before
  the extraction client on every mode), a stated-turn rule added to
  the live prompt, and the stated-turn audience case ported
  into the node's AudienceDecisionService (Named rule carrying
  RoutePersonId for the assigned person), which the earlier port
  had not carried.
- The backup tool's restore announcement insert adapts to the node
  Events columns (ReportedNotObserved, AudienceAskPending).

Suite counts on this tree (all run 2026-10-28 evening): node
regression harness 133 of 133; node suite 54 of 54; monolith demo
suite 131 of 131; protocol checks 36 of 36; relay tests 40 of 40;
stand-in vector selftests 35 of 35 each; backup tool selftest 21 of
21; live leg (real stand-ins, real node, real relay) 29 of 29;
Beat 4 live batch 4 of 4 with one counted live API call per run.

Demo reset counts, computed from this tree only: a fresh
integrated node seeds 6 persons, 16 events, 23 audits, 1 promise.
The retaken demo snapshot was sealed from the live leg's node
state (6 persons, 23 events, 33 audits, 2 connections, 3
consents, 5 federated events with provenance) and demo-reset
restored it with verification equal across 32 tables, 189 rows,
and 23 audience rows; the restored record holds 24 events and 34
audits (the seeded state plus the restore announcement and its
restore audit row), with the federated provenance intact.
