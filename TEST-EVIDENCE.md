# Test evidence summary

What was run against the code in this repository, and what
it showed. Every public commit is covered in its own
sections below, each verified on its own exact tree, in
the order the commits landed.

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
measured and set deliberately, with the focus state
shown visibly on every interactive element. Alongside the
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

## Sixth public commit: build

Every project in the tree builds clean from scratch (a
fresh export of the exact tree, in both Release and
Debug), 0 warnings and 0 errors (.NET 10), measured at
this commit's final verification.

## Sixth public commit: suites

All passing at this commit's final verification
(October 30, 2026; no suite grew or shrank in this
commit, because the change is wording on the surface
and the checks that pin surface wording were updated
to the new words):

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

## Sixth public commit: end-to-end exercises

Verified on this exact tree at its final verification:

- Four-process clean-boot journey: 15 of 15 checks, in
  two consecutive runs on this exact tree.
- Fresh deployment journey: 22 of 22 checks, in two
  consecutive runs on this exact tree.
- Live door ceremony: 30 of 30 checks.
- Live leg: 30 of 30 checks.
- Canary batch: 19 of 19 checks, in both of its shapes.

What this commit carries: the surface's word for what
a person takes on has changed, everywhere a reader
meets it. The list in the sidebar is now My Claims.
Its table reads Claimed by, Claim, For whom, Due,
Status, and Settle; a settled claim reads Done, a
claim that did not happen reads Still unclaimed, and
a claim its person let go reads Released. Nothing on
the surface calls an undone claim broken, and nothing
frames it as a failure of the person: a claim cannot
be handed to someone, so what it asks of them was
theirs from the start, and letting one go is
logistics, not a verdict. The daily summary's open
section is now Claims still open, and its lines begin
Claimed by. The notes the surface answers with when
a claim cannot be settled speak of claims in the same
words. The single-process build's matching page and
notes carry the same wording. The stored record is
unchanged, and so are the values the settle form
posts; only the words a reader sees moved.

## Seventh public commit: build

Every project in the tree builds clean from scratch (a
fresh export of the exact tree, in both Release and
Debug), 0 warnings and 0 errors (.NET 10), measured at
this commit's final verification.

## Seventh public commit: suites

All passing at this commit's final verification
(October 31, 2026; no suite grew or shrank in this
commit, because the change is presentation only:
navigation layout and its client script, with no
change to what any page does):

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

## Seventh public commit: end-to-end exercises

Verified on this exact tree at its final verification:

- Four-process clean-boot journey: 15 of 15 checks, in
  two consecutive runs on this exact tree.
- Fresh deployment journey: 22 of 22 checks, in two
  consecutive runs on this exact tree.
- Live door ceremony: 30 of 30 checks.
- Live leg: 30 of 30 checks.
- Canary batch: 19 of 19 checks, in both of its shapes.

What this commit carries: on narrow screens the
sidebar leaves the page and becomes a modal navigation
drawer. The slim top bar carries a house button;
tapping it slides the full list of the house's places
in from the left over a dimmed backdrop, and following
a place closes it. A bottom navigation bar pins the
four most-visited places (Front door, Family timeline,
Daily Summary, Private Inbox) as icons only, each one
named for assistive technology instead of shown as a
text label. Above the breakpoint nothing changes: the
fixed sidebar is the only navigation, exactly as
before, and the desktop rendering is unchanged.

## Eighth public commit: build

Every project in the tree builds clean from scratch (a
fresh export of the exact tree, in both Release and
Debug), 0 warnings and 0 errors (.NET 10), measured at
this commit's final verification.

## Eighth public commit: suites

All passing at this commit's final verification
(October 31, 2026; no suite grew or shrank in this
commit, because the change is presentation only: brand
assets and the layout references that show them):

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

## Eighth public commit: end-to-end exercises

Verified on this exact tree at its final verification:

- Four-process clean-boot journey: 15 of 15 checks, in
  two consecutive runs on this exact tree.
- Fresh deployment journey: 22 of 22 checks, in two
  consecutive runs on this exact tree.
- Live door ceremony: 30 of 30 checks.
- Live leg: 30 of 30 checks.
- Canary batch: 19 of 19 checks, in both of its shapes.

What this commit carries: the Tellhouse logo. The
mark (a wide roof over bold T and H letters, navy and
coral) now sits in the sidebar brand beside the
Tellhouse wordmark, which is also the identity header
of the mobile navigation drawer, and in the mobile
top bar's house spot, where tapping it opens that
drawer. On this shell's dark ground the mark is shown
in its white and coral variant so it stays legible;
the site icons are the brand's own favicon set (an
SVG icon and an ICO icon) and its apple touch icon.
The assets are the brand kit's files, used as
delivered.

## Ninth public commit: build

Every project in the tree builds clean from scratch (a
fresh export of the exact tree, in both Release and
Debug), 0 warnings and 0 errors (.NET 10), measured at
this commit's final verification.

## Ninth public commit: suites

All passing at this commit's final verification
(October 31, 2026; the home node demo suite grew by
four checks and the home node suite by five, pinning
the double-submit answers and the screen-wording rules
this commit ships):

- Home node demo suite: 211
- Home node suite: 160
- Single-process build demo suite: 138
- Protocol checks: 36
- Sealed relay tests: 49
- Lisa stand-in self-test: 49
- Daniel stand-in self-test: 49
- Backup and restore self-test: 43
- Stand-in protocol checks: 38 of 38
- Stand-in session recovery checks: 10 of 10
- Door page checks: 15 of 15

## Ninth public commit: end-to-end exercises

Verified on this exact tree at its final verification:

- Four-process clean-boot journey: 15 of 15 checks, in
  two consecutive runs on this exact tree.
- Fresh deployment journey: 22 of 22 checks, in two
  consecutive runs on this exact tree.
- Live door ceremony: 30 of 30 checks.
- Live leg: 30 of 30 checks.
- Canary batch: 19 of 19 checks, in both of its shapes.

What this commit carries: plainer screens across the
house. The daily page is the Briefing and the delivery
page is the Inbox; a claim's status line carries the
closing words as its proof, with the closer's name and
the household time; every time on the screens is the
household's local time in one format; a second click
on a settled entry answers "Already confirmed."
instead of erroring; an out-of-stock supply reads as
out and orders now, with the run-out date fixed at its
root; plan pages and error pages speak plainly and raw
error text never reaches a visitor; and the
Connections page unlocks with a posted passcode that
sets a short-lived cookie, so the house secret never
appears in an address, a redirect, or a hidden field.

## Tenth public commit: build

Every project in the tree builds clean from scratch (a
fresh export of the exact tree, in both Release and
Debug), 0 warnings and 0 errors (.NET 10), measured at
this commit's final verification.

## Tenth public commit: suites

All passing at this commit's final verification
(November 1, 2026; the home node suite grew by one
check pinning the party-id refusal, and the
single-process build's demo suite grew by one check
pinning the brief's source-audience rule):

- Home node demo suite: 211
- Home node suite: 161
- Single-process build demo suite: 139
- Protocol checks: 36
- Sealed relay tests: 49
- Lisa stand-in self-test: 49
- Daniel stand-in self-test: 49
- Backup and restore self-test: 43
- Stand-in protocol checks: 38 of 38
- Stand-in session recovery checks: 10 of 10
- Door page checks: 15 of 15

## Tenth public commit: end-to-end exercises

Verified on this exact tree at its final verification:

- Four-process clean-boot journey: 15 of 15 checks, in
  two consecutive runs on this exact tree.
- Fresh deployment journey: 22 of 22 checks, in two
  consecutive runs on this exact tree.
- Live door ceremony: 30 of 30 checks.
- Live leg: 30 of 30 checks.
- Canary batch: 19 of 19 checks, in both of its shapes.

What this commit carries: the finished whole of the
enterprise audit program and the two fixes the
November 1 checklist scan named. The home page reads
in its final order, the Timeline and the Claims page
render as cards on a phone, the mobile bottom bar
names its four destinations, and the shell's accent
is the brand mark's own coral. The README is the
finalized front door, with a real screenshot of this
build and a three-step path for a judge, and DEPLOY.md
no longer describes the deployment as one computer.
At the door, a join confirmation can no longer enroll
a party id that already belongs to a different person:
it refuses and names the person enrolled. In the
single-process build, kept in the tree as the first
iteration, the brief now reads a claim's stored
audience from its source telling exactly as the home
node does, and that build is frozen again at this
fixed state. The README's provenance sentence, at the
author's direction, credits a real household rather
than naming one.

## Eleventh public commit: build

Every project in the tree builds clean from scratch (a
fresh export of the exact tree, in both Release and
Debug), 0 warnings and 0 errors (.NET 10), measured at
this commit's final verification.

## Eleventh public commit: suites

All passing at this commit's final verification
(November 1, 2026; no suite counts moved, because this
commit changes words and page structure only):

- Home node demo suite: 211
- Home node suite: 161
- Single-process build demo suite: 139
- Protocol checks: 36
- Sealed relay tests: 49
- Lisa stand-in self-test: 49
- Daniel stand-in self-test: 49
- Backup and restore self-test: 43
- Stand-in protocol checks: 38 of 38
- Stand-in session recovery checks: 10 of 10
- Door page checks: 15 of 15

## Eleventh public commit: end-to-end exercises

Verified on this exact tree at its final verification:

- Four-process clean-boot journey: 15 of 15 checks, in
  two consecutive runs on this exact tree.
- Fresh deployment journey: 22 of 22 checks, in two
  consecutive runs on this exact tree.
- Live door ceremony: 30 of 30 checks.
- Live leg: 30 of 30 checks.
- Canary batch: 19 of 19 checks, in both of its shapes.

What this commit carries: the fixes the live-site
re-check listed. The demo label now stands once on
every page, as one bar carrying the label and the
sentence together, in place of the chip and the banner
that used to run at the same time. The home headline
ends without a period. The viewer pickers on the
record pages offer the household view in plain words.
A briefing line for an entry with no named author
reads "Someone in the household", and the pending
phrase is the one phrase everywhere, spelled the same
way. A pending entry can be declined where it stands:
the Timeline and the home page offer Don't send beside
Confirm, including for entries waiting on the record
itself. The audience defaults list names only the
people who have chosen a default, and says once, about
the settings, when some have not chosen yet. The rail
prints the household name once, under the wordmark.

## Twelfth public commit: build

Every project in the tree builds clean from scratch (a
fresh export of the exact tree, in both Release and
Debug), 0 warnings and 0 errors (.NET 10), measured at
this commit's final verification.

## Twelfth public commit: suites

All passing at this commit's final verification
(November 2, 2026; the home node demo suite grew by the
eight standing checks this commit adds, for the claims
wording, the household claim audience, and the relayed
message landing; no other count moved):

- Home node demo suite: 219
- Home node suite: 161
- Single-process build demo suite: 139
- Protocol checks: 36
- Sealed relay tests: 49
- Lisa stand-in self-test: 49
- Daniel stand-in self-test: 49
- Backup and restore self-test: 43
- Stand-in protocol checks: 38 of 38
- Stand-in session recovery checks: 10 of 10
- Door page checks: 15 of 15

## Twelfth public commit: end-to-end exercises

Verified on this exact tree at its final verification:

- Four-process clean-boot journey: 15 of 15 checks, in
  two consecutive runs on this exact tree.
- Fresh deployment journey: 22 of 22 checks, in two
  consecutive runs on this exact tree.
- Live door ceremony: 30 of 30 checks.
- Live leg: 30 of 30 checks.
- Canary batch: 19 of 19 checks, in both of its shapes.

What this commit carries: the second enterprise audit's
fixes. The claims wording now reads the way people speak:
a claim closed undone is "Not done", a claim let go is
"No longer needed", and the page is called Claims (its
address does not change). A claim addressed to the
household is stored with the household as its audience,
so the whole household sees it, as its label always said;
a claim naming a person still reaches that person and its
maker only. A confirmed message for one person now lands
in that person's Inbox on the house, and nobody else's,
and the answer after confirming says what happened
("Sent to Daniel.", "Reminder set for Friday."). The
Timeline table fits its panel at desktop widths, so the
Confirm and Don't send buttons are on screen. The
pickers name their roles (Sending as, Confirming as,
Acting as, Answering as), the message box keeps the
speaker a person chose, entries carry their date when
they are not from today, and the Briefing lists open
claims and things to order before recent entries, newest
first. On a phone, the record cards name their parts, the
supplies table no longer clips, and the menu button
shows a menu marker. The pages for the two member
assistants and the relay now describe what they actually
do, in words that match the code. The README's
screenshot shows this build, and its steps start with
a message that produces a confirmation. No licence
file is included with the code.

## Thirteenth public commit: build

Every project in the tree builds clean from scratch (a
fresh export of the exact tree, in both Release and
Debug), 0 warnings and 0 errors (.NET 10), measured at
this commit's final verification.

## Thirteenth public commit: suites

All passing at this commit's final verification
(November 2, 2026; no count moved from the twelfth
public commit, and no check was changed or removed):

- Home node demo suite: 219
- Home node suite: 161
- Single-process build demo suite: 139
- Protocol checks: 36
- Sealed relay tests: 49
- Lisa stand-in self-test: 49
- Daniel stand-in self-test: 49
- Backup and restore self-test: 43
- Stand-in protocol checks: 38 of 38
- Stand-in session recovery checks: 10 of 10
- Door page checks: 15 of 15

## Thirteenth public commit: end-to-end exercises

Verified on this exact tree at its final verification:

- Four-process clean-boot journey: 15 of 15 checks, in
  two consecutive runs on this exact tree.
- Fresh deployment journey: 22 of 22 checks, in two
  consecutive runs on this exact tree.
- Live door ceremony: 30 of 30 checks.
- Live leg: 30 of 30 checks.
- Canary batch: 19 of 19 checks, in both of its shapes.

What this commit carries: the page redesign. Every
message is now shown as a trace of the steps it took:
the words and where they came from, what read them (the
served model on a live instance, the local stand-in on
this one, a stated-turn rule, or the demo seed for the
starting record), the typed entries it became with their
fields and any proposed action, what the house rules
checked and who can see it, what a person decided, and
where it went. A new Model page shows how the house
uses its model: this instance's engine and settings,
the path a message takes, counts from the readings on
file, and the model's instructions and output shape.
The reading record keeps a message's words and output
only when the message produced record entries; a
question or a refused reading keeps counted numbers
only. No licence file is included with the code.

## Fourteenth public commit: build

Every project in the tree builds clean from scratch (a
fresh export of the exact tree, in both Release and
Debug), 0 warnings and 0 errors (.NET 10), measured at
this commit's final verification.

## Fourteenth public commit: suites

All passing at this commit's final verification
(November 2, 2026; the home node demo suite grows by
the new checks the loop build adds, and no existing
check was changed or removed):

- Home node demo suite: 237
- Home node suite: 161
- Single-process build demo suite: 139
- Protocol checks: 36
- Sealed relay tests: 49
- Lisa stand-in self-test: 49
- Daniel stand-in self-test: 49
- Backup and restore self-test: 43
- Stand-in protocol checks: 38 of 38
- Stand-in session recovery checks: 10 of 10
- Door page checks: 15 of 15

## Fourteenth public commit: end-to-end exercises

Verified on this exact tree at its final verification:

- Four-process clean-boot journey: 15 of 15 checks, in
  two consecutive runs on this exact tree.
- Fresh deployment journey: 22 of 22 checks, in two
  consecutive runs on this exact tree.
- Live door ceremony: 30 of 30 checks.
- Live leg: 30 of 30 checks.
- Canary batch: 19 of 19 checks, in both of its shapes.

What this commit carries: the household loop, one
person at a time. The shell gains a person switcher
(a cookie on this demo; explicit links still win), and
the pages follow one person's loop: Home to say it,
the Inbox for what needs them, Briefing for the house
now, Timeline for the record. A message whose words
are health-shaped, or that the person marks Private,
is read by the house's own rules and never by a model;
there is no fallback to a model for it. On a Live
instance the other Meta models each take one step:
Muse Voice Transcribe hears a spoken message on Home,
Muse Spark reads it, Muse Wonder reviews the shared
record on Briefing when a person asks, SAM 3.1 counts
the tracked supplies in a photo of the shelf on
Briefing, and Muse Image draws a picture card for a
message passed on to a child. Off Live, hearing,
seeing and drawing are unavailable, and the review
runs on the house's own rules, labelled as such. No
licence file is included with the code.
