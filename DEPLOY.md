# Deploying the Tellhouse demo environment

This document is the deployment package for the live demo
environment (judging window, Nov 5 to 8). The deployable
product is the full demonstration, four services that wire
together by environment alone:

- **tellhouse**, the home node (src/HomeNode): the home base.
  The website a visitor uses, and the household record.
- **tellhouse-relay** (relay/): the sealed relay. It carries
  ciphertext and routing metadata only and keeps no memory.
- **tellhouse-lisa** (standins/LisaAssistant) and
  **tellhouse-daniel** (standins/DanielAssistant): the two
  stand-in assistants. Each holds its own credentials, speaks
  only the packet protocol through the relay, and is a service
  in its own right.

The older src/HouseholdCoordinator project is the frozen
monolith: it is not published, not started, and not part of
this package.

The demo environment is a demo: no account signup anywhere,
pre-seeded with the fictional Ellis household, visitors look
around and try tellings. Every page of the node carries a
plain standing label with the words "DEMO ENVIRONMENT" and
the demo-scope line: "Demo: the people in this house are
invented, it runs on one computer, and no real family's data
is in it." Both live in the shared layout
(src/HomeNode/Pages/Shared/_Layout.cshtml), so they render
on the front door and on every other house surface that uses
that layout, and they travel with the environment wherever
it is linked.

## Demo data and this public release, stated plainly

This repository is populated with demo data for demo
purposes. The household in it, the Ellis household, its
people, and its records are invented, and no real family's
data is in it. The keys, recovery phrase, and tokens in the
demo seed folders are toy material, published on purpose for
the local demo. The live deployed demo does not run on any
of them: it runs in fresh deploy mode on fresh values set at
deployment time (see "Deploying fresh" below), which are
never in this repository. Some functionality is limited or
removed in this public release version: it is a demonstration
of the household coordinator, not the finished product.

## Built on open source

Tellhouse stands on open-source projects, and on one model:

- .NET and ASP.NET Core (MIT): the home node, the two
  stand-in assistants, the backup tool, and the protocol
  library are C# on .NET 10, and the front door is ASP.NET
  Core Razor Pages.
- Entity Framework Core (MIT) with its SQLite provider:
  the house record lives in a SQLite database (SQLite is
  public domain).
- Python 3 (PSF), standard library only: the sealed relay.
- Bootstrap 5.3.3 and jQuery 3.7.1 (both MIT), with the
  jQuery Validation plugins, vendored under wwwroot/lib:
  the front door's styling and client scripts.
- Muse Spark, Meta's model, through the Meta Model API:
  the model call at the center of the design. It reads a
  telling in a person's own words and returns the
  structured events the house then works from.

What we built on top of them is the house itself: the
shared record and its front door, the confirmation step a
person takes before anything is settled on the record,
the audience rules that decide who may see and be told
what, the sealed member protocol and the relay that
carries messages it cannot read, the door ceremony that
connects a member's own assistant, and the sealed backup
and restore that also powers the demo reset. The model
proposes structured events; the house's own machinery
confirms, routes, and records them.

Standalone: this repository runs on its own. Nothing in
it depends on any pre-existing Tellhouse infrastructure;
the only outside service it calls is the Meta Model API,
with the key supplied as the MODEL_API_KEY environment
variable (see .env.example and DEPLOY.md).

## The one rule that is not negotiable

The live instance runs extraction in Live mode on the STANDARD
model, muse-spark-1.3. Anything a judge types into this
environment is their own real words, and those words never
run on the Contributor (training) tier.
Contributor is for tests, rehearsals, and film takes with
fictional data only. The configuration below sets Standard
explicitly; do not inherit a local appsettings that says
otherwise.

## How each service boots

There are two boot modes, and the difference matters.

**Local demo mode (the default)** is for running the demo
from this tree on one machine. No service needs a setup
ceremony at run time. Each image carries demo seed state
produced beforehand by a real door ceremony run locally
against this tree (deploy/run_demo_seed_ceremony.py; 30
checks, all passing): Lisa and Daniel knocked, a named
inviter confirmed with the check phrase, both connections
stand Active, and the resulting record was sealed with the
product's own backup tool. Each entrypoint applies the same
rule: seed only what is absent, never overwrite what exists.
Everything in this mode is the published toy material, which
is exactly what a local demo should run on.

**Fresh deploy mode** is what the render.yaml blueprint
deploys, and it is described under "Deploying fresh" below.
In fresh mode no baked demo material is used at all, so
nothing published in this repository can operate the
deployed instance.

- Node: on a fresh disk the entrypoint copies the demo
  keystore in if absent, then restores the sealed demo
  snapshot (src/HomeNode/demo-seed/demo-snapshot.thb) through
  the product's own reset path, TellhouseBackup demo-reset.
  If the restore fails, the entrypoint exits without starting
  the app rather than serve a half-seeded node.
- Relay: the registry is a file. On boot with no registry
  file, the entrypoint loads the seeded demo registry
  (relay/demo-seed/relay-config.json, token SHA-256 hashes
  only). A present registry is used as-is.
- Stand-ins: on a fresh boot the entrypoint copies the seeded
  store (keys and joined state from the ceremony) into the
  runtime store directory, only if no store exists there.
  The join path in the programs is unchanged and remains the
  real one for any non-demo use.

All seed material is demo material for a fictional household,
published on purpose; each demo-seed directory carries a
README saying exactly what its files are. A real household's
keys, snapshot, and recovery phrase would never ship anywhere.

Sessions: a node's session window is 30 minutes, and a demo
that runs for hours cannot rely on a ceremony held at boot.
The stand-ins therefore run an auto-session mode when
STANDIN_AUTO_SESSION=1 (the blueprint sets it): before any
send, and on a 20 minute keepalive, the stand-in runs the
real session open and challenge proof exchange whenever its
recorded proof is stale. There is no bypass; a failed exchange
leaves the send to queue unrecorded, exactly as it does when
the relay is stopped.

## The blueprint

render.yaml, at the repository root, deploys all four
services on Render's free plan, each from its Dockerfile,
with the environment wiring between them (each service's
address passed by environment; no localhost assumptions
between services) and a health check per service (node /,
relay /health, stand-ins /status).

The operator's hands-on steps are: sign in to Render,
deploy the blueprint, set MODEL_API_KEY on the tellhouse
service, and then work the fresh-values sequence under
"Deploying fresh" (the blueprint marks every credential
value sync: false, so none of them is ever in the
repository). The node's other environment is set by the
blueprint, including Extraction Live on the Standard model
and the Test Send control's wiring to Lisa's service.

## Deploying fresh: the operator sequence

Fresh deploy mode (TELLHOUSE_DEPLOY_MODE=fresh, set on all
four services by the blueprint) exists so the deployed
instance never runs on the published demo material. What
each service does with it:

- Node: the entrypoint copies no demo key and restores no
  demo snapshot. On the first boot the app generates its
  own node keypair into its keystore and seeds the fictional
  day into a new database; the entrypoint then stops it and
  takes this deployment's first sealed backup with the
  product's own backup tool. The fresh recovery phrase that
  tool generates is stored only in the instance's keystore
  (/tmp/node-keys/deployment-recovery-phrase.txt, next to
  deployment-snapshot.thb). It is not the phrase in this
  repository, and it is never printed to the service log.
- Relay: the entrypoint does not load the baked registry.
  It builds the registry from operator-supplied credential
  hashes (RELAY_NODE_TOKEN_SHA256, RELAY_LISA_TOKEN_SHA256,
  RELAY_DANIEL_TOKEN_SHA256). A connection whose hash is not
  set yet is simply absent from the registry and cannot use
  the relay until the operator sets it and the service
  restarts.
- Stand-ins: the entrypoint copies no baked store. Each
  stand-in starts unjoined with the keypair it generated
  itself, and with STANDIN_AUTO_JOIN=1 it knocks through the
  real door as soon as the operator supplies its invite code
  by environment, then waits for the inviter's confirmation
  like any other client.

The operator sequence, after the blueprint is deployed and
MODEL_API_KEY is set:

1. Let the node finish its first boot (its log says the
   deployment's sealed backup was taken). The relay and the
   stand-ins can be up or still starting; nothing can use
   the relay yet, which is correct at this point.
2. Choose a fresh node token (any long random string, for
   example the output of `openssl rand -hex 24`). Set it as
   RELAY_NODE_TOKEN on the tellhouse service. Compute its
   registry hash with `python3 deploy/fresh/
   credential_hash.py --token "<token>"` and set the output
   as RELAY_NODE_TOKEN_SHA256 on the tellhouse-relay
   service. Also choose a fresh house secret the same way
   and set it as HOME_NODE_HOUSE_SECRET on the tellhouse
   service: every house side door act below (issuing an
   invite, reading a card, confirming or declining) and
   every /admin call must present it as the X-House-Secret
   header, and until it is set those endpoints refuse every
   call.
3. Issue the two invites from the deployed node's door:
   `curl -X POST https://tellhouse.onrender.com/door/invite
   -H 'Content-Type: application/json' -H 'X-House-Secret:
   <the house secret>' -d '{"inviter":
   "Margaret", "intended": "Lisa", "level": "Standard"}'`
   and the same with "Daniel". Each response carries the
   invite code for that stand-in.
4. Set STANDIN_INVITE_CODE on each stand-in service to the
   code the node issued for it (optionally also
   STANDIN_INVITE_CHECK_PHRASE, a phrase of your choosing
   that the stand-in presents with its knock). Setting the
   value restarts the stand-in onto the keypair it keeps for
   this deployment; it writes the invite into its own store
   and knocks at the node's door on its own, retrying while
   the node cold starts.
5. Read each stand-in's fingerprint from its /status page
   (read it now, from the restarted instances: these are the
   deployment keypairs), compute its registry hash with
   `python3 deploy/fresh/credential_hash.py --code "<invite
   code>" --status-url
   "https://tellhouse-lisa.onrender.com/status"` (and the
   Daniel URL), and set the outputs as
   RELAY_LISA_TOKEN_SHA256 and RELAY_DANIEL_TOKEN_SHA256 on
   the relay service. Setting environment values restarts
   the relay with the full fresh registry, which is what the
   stand-ins' tellings and session proofs travel through.
6. Confirm each knock as the inviter: read the check phrase
   the node shows for the pending connection at
   `GET https://tellhouse.onrender.com/door/card/
   conn-lisa-01` (and conn-daniel-01, presenting the
   X-House-Secret header on the read as on every house side
   act), then
   `curl -X POST https://tellhouse.onrender.com/door/
   confirm -H 'Content-Type: application/json' -H
   'X-House-Secret: <the house secret>' -d
   '{"connection_id": "conn-lisa-01", "check_phrase":
   "<phrase from the card>", "inviter": "Margaret"}'`.
   Each stand-in notices the confirmation on its own within
   seconds and completes the join; its /status then reports
   joined true and its invite disposed.
7. Open the standing Consent valves the demo household
   runs with: Lisa's OneLiner, Daniel's OneLiner, and
   Daniel's ProposalResponse, through the node's valve
   endpoint: `curl -X POST https://tellhouse.onrender.com/
   admin/valve -H 'Content-Type: application/json' -d
   '{"connection_id": "conn-lisa-01", "material_class":
   "OneLiner", "state": "open"}'`, and the same for
   conn-daniel-01 with "OneLiner" and again with
   "ProposalResponse". This is the same standing state the
   baked demo's seed ceremony composes through the same
   endpoint. It is load-bearing, not ceremony: by design
   the node rejects a member's packets whole while that
   member's valve is closed, and a fresh deployment holds
   no Consent in force until this step, so without it no
   telling can land and no proposal can be disposed. A
   product reset restores the deployment's first-boot
   snapshot, which predates this step; repeat it after
   any reset.
8. Open the website and use the Test Send control once.
   Lisa's telling should land on the record with her
   assistant's provenance. The deployment is now running
   entirely on values created during these steps.

A restart of all four services on the free plan's ephemeral
disks starts this over: fresh mode boots unseeded again, and
the sequence above is the deployment's own reset story at
that tier. The product's backup and restore reset (next
section) is the path wherever the operator has the node's
database and keystore at hand.

Service URLs in the blueprint follow Render's naming (a
service named X is reached at https://X.onrender.com). If
Render assigns a different hostname because a name was
already taken, update the URL environment values (Relay__
BaseUrl on the node, RELAY_URL and NODE_URL on the stand-ins,
the Test Send TriggerUrl on the node) to the assigned
hostnames. Nothing else changes.

### Free tier, stated plainly

Free web services spin down after about 15 minutes idle, and
the account's free instance hours are shared across services
(750 hours a month at the time of writing). Four free
services are therefore a demonstration shape, not an
always-on deployment: they cannot all stay awake all month
inside the free hours, and the first visit after an idle
stretch pays a cold start (commonly around a minute) on each
service it touches. During the judging window the practical
mitigations are to open the demo shortly before a judging
session so the services are warm, and to accept the spin
down between sessions.

The smallest alternatives, if that trade-off is not wanted:
put the node alone on a paid Starter instance ($7 a month at
the time of writing) so the home base itself never sleeps,
leaving the relay and stand-ins free (a Test Send then pays
their cold start, the site does not); or run all four
processes in a single container as one service, which the
demo-scope line already discloses ("it runs on one
computer"), at the cost of the four-service shape this
package proves.

## Reset between judges

The reset is the product's own backup and restore machinery,
the same machinery the node entrypoint boots from. To return
a locally run node to its determined starting state: stop the
node service, run TellhouseBackup demo-reset with the baked
demo snapshot and its demo recovery phrase onto the node's
database path, and start the service again. The stop is not
optional and there is no live reset, hourly or otherwise:
never run demo-reset onto a database a running node has open,
because a running node keeps working on the file it opened and
cannot see a file swapped under it. The entrypoint's restore
is safe on this point by construction, since it runs before
the app starts. As part of the swap, the restore tool deletes
the destination's journal sidecar files (the -wal, -shm, and
-journal files) before moving the restored file into place:
those files belong to the stopped node's old database file,
and left behind they can be replayed against the restored
file on the next open and mix the two records. A fresh-mode
deployment resets the same way with its own material: the
deployment's snapshot and recovery phrase in its keystore
(/tmp/node-keys/deployment-snapshot.thb and
deployment-recovery-phrase.txt), which the first fresh boot
generated; the published demo phrase opens nothing there. The restored
record is the ceremony state (6 persons, both connections
Active, the stand-ins' traffic in the record with its
provenance) plus the restore announcement the reset itself
writes. Relay and stand-in state needs no reset for this:
their runtime state is a spool and two small stores, and a
restart reseeds both deterministically from the same baked
material.

reset-demo.sh, in this repository, is neither the boot path
nor the deployed reset. It is the local packaging lifecycle
tool for the frozen monolith package. Do not present it as
the deployed product's reset mechanism.

## What was verified locally

No Docker daemon exists on the build machine, so the
Dockerfiles are verified by construction plus process runs in
the exact image layout (published apps, entrypoints, baked
seed) with environment-only configuration. The evidence:

- The demo seed ceremony (deploy/run_demo_seed_ceremony.py),
  the real door ceremony: 30 of 30. The live leg against the
  programs as refactored for service use: 30 of 30.
- The four-process clean-boot journey (deploy/
  run_service_journey.py): all four services booted from
  fresh directories through their entrypoints; the node
  restored the determined state; both stand-ins, with their
  recorded session time simulated stale, ran the real open
  and answer exchange and were verified by the node; a
  direct telling from the website landed; the website's
  Test Send control fired Lisa's stand-in through the real
  relay and the telling landed with provenance; a house
  proposal was disposed by Daniel's acknowledgement; with
  the relay killed the next Test Send queued unrecorded and
  nothing landed; the relay restarted and reseeded; a
  product reset returned the node to the determined state.
  15 of 15 checks.
- The canary batch rerun against the entrypoint-launched
  relay (deploy/run_canary_service_shape.py): the canary
  phrase crossed and landed once, no canary plaintext in any
  relay file, both tamper forms rejected, both replay forms
  rejected. 19 of 19.
- The suites on this tree: node demo suite 191,
  node suite 132, monolith demo suite 131, backup and
  restore tool 36, relay 49, stand-in selftests 46 each, stand-in
  protocol checks 36. All passing, no failures. (Suite
  totals grew on October 29, 2026 as checks were added,
  first the session checks and then the review fixes; no
  earlier check was removed.)
- The sealed snapshot itself: restoring it and comparing
  table by table against the ceremony record reported every
  table equal.

- The fresh deployment journey (deploy/
  run_fresh_deploy_journey.py): all four services booted in
  fresh deploy mode with the baked demo material present in
  their image layout and provably unused; the node generated
  its own keypair, recovery phrase, and sealed backup; the
  operator sequence above was then driven over HTTP exactly
  as written (fresh token, fresh invites, fresh registry
  hashes, inviter confirmations) and both stand-ins joined
  through the real door; the judge journey then ran on the
  fresh values, and a sweep of every runtime file found none
  of the published demo credential values in use. 21 of 21
  checks, re-verified in three consecutive runs on
  October 29, 2026 (the count includes the Consent valve
  step of the operator sequence above, added the same day;
  the four-process clean-boot journey above was likewise
  re-verified at 15 of 15 in three consecutive runs that
  day, after a session-exchange defect in the stand-ins was
  found and fixed).

Local verification ran with the mock extraction client ($0
spent). The deployed node differs only in configuration:
Extraction Live on the Standard model, with the key supplied
by the operator. Once deployed in that configuration, a
visitor's own telling gets a live model response on the
Standard tier. We control the stage, not the performance.
