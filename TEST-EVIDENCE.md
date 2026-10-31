# Test evidence summary

What was run against the code in this repository, and what
it showed. Three public commits are covered, each verified on
its own exact tree: the first public commit (75b6ed3,
October 29, 2026), the deployment wiring follow-up
commit built on top of it (October 29, 2026), and the
third public commit built on top of that (October 29,
2026), covered in the final sections below.

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
both on dereferences of one queued delivery value.
They were remedied with null-forgiving markers at three
dereference sites in that block (the third site had not
itself warned; each marked value was verified non-null,
by the queue code's own chain and by the suite passing
on the unmarked build) before this commit was
finalized, together with a one-line fix letting the
protocol checks driver create its own evidence
directory. The figure stated here is the from-scratch
result on this exact tree, measured at its final
verification.

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
  runs at the batch verification, two further
  consecutive runs after the second review round, and
  two consecutive runs on this exact tree at its final
  verification.
- Fresh deployment journey (the same four services
  booted in fresh deploy mode, driven through the full
  operator sequence of DEPLOY.md on fresh values, then
  the demonstration run against them): 21 of 21 checks,
  in three consecutive runs at the batch verification,
  two further consecutive runs after the second review
  round, and two consecutive runs on this exact tree at
  its final verification.
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
timing: across the verification sessions recorded for
this follow-up it recorded 36 of 36 once and 33 of 36
in seven further runs across every tree tested,
including trees that predate these fixes and this
exact tree at its final verification, the three
failures always the same session exchange timing
checks, with the house's own record showing the
packets delivered and the rejections logged. The two service journeys above, which exercise
the same session exchange in the deployed shape, are
green in every run recorded here. One further blemish,
stated rather than smoothed over: the home node demo
suite threw an exception once at its tail during one
working run; it never reproduced, and the suite
recorded 191 of 191 in every standalone and merged
rerun. The figures above
are from the verification runs made after those fixes,
on the tree this commit carries.

## Third public commit: build

Every project in the tree builds clean from scratch (a
fresh export of the exact tree, in both Release and
Debug), 0 warnings and 0 errors (.NET 10), measured at
this commit's final verification.

## Third public commit: suites

All passing at this commit's final verification
(October 29, 2026; totals grew again as checks were
added, and no earlier check was removed):

- Home node demo suite: 205
- Home node suite: 147
- Single-process build demo suite: 131
- Protocol checks: 36
- Sealed relay tests: 49
- Lisa stand-in self-test: 49
- Daniel stand-in self-test: 49
- Backup and restore self-test: 43
- Stand-in protocol checks: 38 of 38 at this commit's
  final verification (see the honest note below)
- Stand-in session recovery checks: 10 of 10

## Third public commit: end-to-end exercises

Verified on this exact tree at its final verification:

- Four-process clean-boot journey: 15 of 15 checks, in
  two consecutive runs on this exact tree.
- Fresh deployment journey: 22 of 22 checks (the set
  grew by one exercise in this commit), in two
  consecutive runs on this exact tree.
- Live door ceremony: 30 of 30 checks.
- Live leg: 30 of 30 checks.
- Canary batch: 19 of 19 checks, in both of its shapes.

What this commit carries, beyond the follow-up: reset
protection is now a mechanism, not only an instruction.
The home node keeps a liveness lock beside its database,
renewed while it runs; the reset and restore tooling
refuses to swap a database that a live node holds, an
operator override is possible and is written to an audit
log when used, a lock left behind by a stopped node does
not block, and an unreadable lock never blocks. Session
recovery on the stand-ins: when a send is refused
because the session is gone, the stand-in re-opens its
session through the real exchange and retries the send
exactly once, then reports the result as it stands; a
second refusal surfaces as an error, and nothing is
recorded by a refused attempt. A tunable bound on packet
age (one named setting, off when unset, seven days as
shipped) lets the house refuse packets issued long
before they arrived. And an adversarial round over the
same ground as the earlier reviews found and fixed,
each with a standing check added: the timeline and the
brief, opened with no reader named, opened as the first
member of the house instead of the house-only view, so a
restricted entry was visible to an unnamed visitor; a
delivery could be queued to a member outside the
audience stored for its source entry, on the sealed
road and on the plan landing road alike; the plans page
showed plan element words and its forms to readers
outside the audience of the entries the words came
from; a damaged row in the sending queue was retried
forever instead of being refused for good; and several
credential files (a backup key store opened for reading,
the node double's key file, and the stand-ins' state
files, which hold a live connection credential) were
left readable by other users instead of being tightened
to owner only. The two limits stated for the follow-up
stand, narrowed by one: the reset guard now acts
wherever the reset tool can see the node's own files,
including a volume shared between containers, and the
written stop-first instruction remains the protection
only where no files are shared at all. The stand-in
protocol checks driver, whose session timing checks
were not reliably green in the follow-up's record, is
green in full at this commit's final verification, 38
of 38, the set having grown by two credential file
checks; the same session exchange stays green in both
service journeys on this tree, as recorded above.

## Fourth public commit: build

Every project in the tree builds clean from scratch (a
fresh export of the exact tree, in both Release and
Debug), 0 warnings and 0 errors (.NET 10), measured at
this commit's final verification.

## Fourth public commit: suites

All passing at this commit's final verification
(October 29, 2026; totals grew again as checks were
added, and no earlier check was removed):

- Home node demo suite: 205
- Home node suite: 155
- Single-process build demo suite: 138
- Protocol checks: 36
- Sealed relay tests: 49
- Lisa stand-in self-test: 49
- Daniel stand-in self-test: 49
- Backup and restore self-test: 43
- Stand-in protocol checks: 38 of 38
- Stand-in session recovery checks: 10 of 10
- Door page checks: 15 of 15

## Fourth public commit: end-to-end exercises

Verified on this exact tree at its final verification:

- Four-process clean-boot journey: 15 of 15 checks, in
  two consecutive runs on this exact tree.
- Fresh deployment journey: 22 of 22 checks, in two
  consecutive runs on this exact tree.
- Live door ceremony: 30 of 30 checks.
- Live leg: 30 of 30 checks.
- Canary batch: 19 of 19 checks, in both of its shapes.

What this commit carries: the door ceremony's house
side now stands behind one authorization wherever it
can be performed. The house secret check lives in a
single shared service used by the HTTP door and admin
adapter and by the Connections page alike, so the two
surfaces cannot drift apart: the page's ceremony
handlers (invite, confirm, decline, revoke) refuse
with 403 before acting for any caller who has not
presented the house secret, a pending request's check
phrase is rendered only to a viewer who has presented
it, and a node with no house secret configured fails
closed on the page exactly as it does on the adapter.
The authorized ceremony still works from the page end
to end. A new driver (deploy/run_door_page_checks.py)
walks the page the way an unauthenticated web client
would, 15 of 15, and eight standing checks join the
home node suite beside the page model. The
single-process build's two remaining read surfaces now
follow the stored audience as its brief and timeline
already did: its promises list shows each promise to
the readers its source telling names (and, with no
reader named, only house-audience promises), and its
plans page fences its open decisions, its provenance
list, and its element text by the stored audience of
the entries they were built from, with the dinner
form's posted sources rechecked against the reader the
form carries; seven standing checks join its demo
suite. And the deployment guide's step 7 valve
examples now present the X-House-Secret header the
endpoint requires, as step 2 of the same guide states
of every house-side call, so the fresh deployment
sequence runs as written.

## Fifth public commit: build

Every project in the tree builds clean from scratch (a
fresh export of the exact tree, in both Release and
Debug), 0 warnings and 0 errors (.NET 10), measured at
this commit's final verification.

## Fifth public commit: suites

All passing at this commit's final verification
(October 30, 2026; the home node demo suite grew by
two checks, and no earlier check was removed):

- Home node demo suite: 207
- Home node suite: 155
- Single-process build demo suite: 138
- Protocol checks: 36
- Sealed relay tests: 49
- Lisa stand-in self-test: 49
- Daniel stand-in self-test: 49
- Backup and restore self-test: 43
- Stand-in protocol checks: 38 of 38
- Stand-in session recovery checks: 10 of 10
- Door page checks: 15 of 15

## Fifth public commit: end-to-end exercises

Verified on this exact tree at its final verification:

- Four-process clean-boot journey: 15 of 15 checks, in
  two consecutive runs on this exact tree.
- Fresh deployment journey: 22 of 22 checks, in two
  consecutive runs on this exact tree.
- Live door ceremony: 30 of 30 checks.
- Live leg: 30 of 30 checks.
- Canary batch: 19 of 19 checks, in both of its shapes.

What this commit carries: the home node's face is
reshelled. Every house surface now sits in one app
shell, a fixed sidebar carrying the house's places, a
top bar, and a roomy content canvas, in a single dark
gray theme whose colors, type, and spacing were
measured and set deliberately, with keyboard focus
visible on every interactive element. Alongside the
shell, every public page's wording passed a full
plain-words review: labels a first-time reader can
understand cold (the deliveries page is now the
Private Inbox, the brief is now the Daily Summary),
insider terms replaced or explained where they stood,
the scaffold error page replaced with the product's
own words, the seeded people's descriptions reworded
so children are spoken for by their parents and never
speak to the record themselves, and the relay's readme
now names its view as the relay operator's surface.
One behavior changed with the wording: the plan forms
now refuse a blank name and create nothing, answering
that words are needed because the record holds only
what a person wrote, where before a blank field was
quietly filled with a stock label. Two standing checks
in the home node demo suite pin that refusal.
