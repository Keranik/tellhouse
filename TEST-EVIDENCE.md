# Test evidence summary

What was run against the code in this repository, and what
it showed. Two public commits are covered, each verified on
its own exact tree: the first public commit (75b6ed3,
October 29, 2026) and the deployment wiring follow-up
commit built on top of it (October 29, 2026).

## Build

First public commit (75b6ed3): every project in the tree
builds clean, 0 warnings and 0 errors (.NET 10), verified
from a fresh tree.

Follow-up commit: every project in the tree builds clean
from scratch (a fresh export of the exact tree, in both
Release and Debug), 0 warnings and 0 errors (.NET 10).
An earlier verification record for this follow-up's
pre-final tree stated 0 warnings; that figure came from
incremental builds and was an artifact of them. A
from-scratch build of that tree showed two
nullable-reference warnings in the home node suite,
both remedied with null-forgiving markers before this
commit was finalized. The figure stated here is the
from-scratch result on this exact tree.

## Suites

First public commit (75b6ed3), all passing:

- Home node demo suite: 179
- Home node suite: 72
- Single-process build demo suite: 131
- Protocol checks: 36
- Sealed relay tests: 40
- Lisa stand-in self-test: 35
- Daniel stand-in self-test: 35
- Backup and restore self-test: 21

Deployment wiring follow-up commit, all passing (final
verification October 29, 2026, after the three fix batches
described below; several totals grew that day as checks
were added, and no earlier check was removed):

- Home node demo suite: 191
- Home node suite: 132
- Single-process build demo suite: 131
- Protocol checks: 36
- Sealed relay tests: 49
- Lisa stand-in self-test: 46
- Daniel stand-in self-test: 46
- Backup and restore self-test: 36
- Stand-in protocol checks: 36 checks in the set; see
  the honest note below for its current run record

## End-to-end exercises

Verified with the deployment wiring the follow-up commit
carries (final verification October 29, 2026):

- Four-process clean-boot journey (the home node, the
  sealed relay, and both stand-in assistants booted as
  services from empty state, then the demonstration run
  against them): 15 of 15 checks, in three consecutive
  runs at the batch verification and two further
  consecutive runs after the second review round.
- Fresh deployment journey (the same four services
  booted in fresh deploy mode, driven through the full
  operator sequence of DEPLOY.md on fresh values, then
  the demonstration run against them): 21 of 21 checks,
  in three consecutive runs at the batch verification
  and two further consecutive runs after the second
  review round.
- Live door ceremony (both stand-ins join through the
  real door, sessions open on the challenge proof, a
  house proposal is disposed by acknowledgement): 30 of
  30 checks.
- Live leg (the same door and delivery exercise driven
  against the running services): 30 of 30 checks.
- Canary batch (planted phrases that must never appear
  in relay state, plus tamper and replay forms): 19 of
  19 checks, re-verified on the follow-up commit.

An honest note on these figures: earlier runs on
October 29, 2026 recorded the clean-boot journey at 13
of 14 and the fresh deployment journey at 17 of 19.
Both were traced the same day to defects that were then
fixed: a stand-in session answer could pair with a
challenge left over from an interrupted earlier attempt
(so the house rightly rejected its proof), the same poll
could take a delivery without acknowledging it, and the
fresh operator sequence was missing its Consent valve
step. An independent read-only review of the first
public commit then found a further set of defects, each
reproduced and fixed before this follow-up was
finalized: health entries could reach a connection the
inviter was told could not hear them; promises and open
decisions were shown past the audience stored for them;
the house side of the door could be driven without the
house secret; a restored database could silently
duplicate rows its own verification could not see; one
malformed packet could lose the rest of its batch at
the relay; a brief model error could mark a telling as
a replay forever; deliveries could be signed with the
wrong key when a key store path was configured; a
health telling about oneself was routed back to the
speaker instead of the named backup; and a restore
could leave stale journal files beside the database it
swapped. A second round of the same review, run against
its full verified report, found a further set, likewise
reproduced and fixed before this follow-up was
finalized: a database reset could silently not happen
while its write-ahead log survived; a house proposal
stopped the entry's own alert from ever going out;
content a reset removed could return by replaying an
old packet; a sealed alert read as done before its
recipient had acknowledged it; assistants' times were
computed on the server clock instead of the household's;
the proposal endpoint sent an entry's text without
checking its state or its audience; an assistant could
write record entries under a permission that only
allowed questions; who said something was taken from
the model's output instead of the authenticated speaker;
private key files were created readable by other users;
rejoining a member duplicated their connection;
invitations made on the connections page could not be
used; outgoing deliveries could jam behind one rejected
post; backups taken while the house was writing usually
could not be restored; and a page of Daniel's stand-in
called it Lisa's. Two limits are stated rather than
smoothed over: the reset tool's guard against running
while the house is up can only see processes on the
same machine, so the written stop-first instruction
remains the protection when the services run in
separate containers; and one driver, the stand-in
protocol checks, is not reliably green in its session
timing: in the final verification session it recorded
36 of 36 once and 33 of 36 in five further runs across
every tree tested, including trees that predate these
fixes, the three failures always the same session
exchange timing checks, with the house's own record
showing the packets delivered and the rejections
logged. The two service journeys above, which exercise
the same session exchange in the deployed shape, are
green in every run recorded here. One further blemish,
stated rather than smoothed over: the home node demo
suite threw an exception once at its tail during one
working run; it never reproduced, and the suite
recorded 191 of 191 in every standalone and merged
rerun. The figures above
are from the verification runs made after those fixes,
on the tree this commit carries.
