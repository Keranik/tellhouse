# Deploying the Tellhouse demo environment

This document is the deployment package for the live demo
environment (judging window, Nov 5 to 8). It brings the
environment to the deployment-only point: everything below is
prepared and verified locally. The deployment step itself
(putting this on a host, and choosing which host) is out of
scope here and follows this document. The product that ships
is the home node, src/HomeNode. The older
src/HouseholdCoordinator project is the frozen monolith: it
is not published, not started, and not part of this package.

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

## The one rule that is not negotiable

The live instance runs extraction in Live mode on the STANDARD
model, muse-spark-1.3. Anything a judge types into this
environment is their own real words, and those words never
run on the Contributor (training) tier.
Contributor is for tests, rehearsals, and film takes with
fictional data only. The configuration below sets Standard
explicitly; do not inherit a local appsettings that says
otherwise.

## Build

    dotnet publish src/HomeNode -c Release -o publish

## Environment variables

| Variable | Value for the demo environment | Why |
|---|---|---|
| ASPNETCORE_URLS | http://127.0.0.1:8080 (behind the host's proxy) | The listen address. |
| ASPNETCORE_ENVIRONMENT | Production | Turns on the exception handler and HSTS. |
| ConnectionStrings__HomeNode | Data Source=/path/to/demo.db | The record's file. One file per instance; see pristine state below. |
| Extraction__Mode | Live | Real Meta Model API extraction. |
| Extraction__Model | muse-spark-1.3 | The STANDARD tier model. See the rule above. |
| MODEL_API_KEY | (the operator's key, set in the host's secret store) | The live client's credential, read from the environment at runtime. Never commit this value; it lives only in the host environment. |

The node's own default, in src/HomeNode/appsettings.json, is
the same key with "Data Source=home-node.db"; the environment
variable above replaces that path for the deployed instance.

## Start command

    cd publish
    ASPNETCORE_URLS=http://127.0.0.1:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__HomeNode="Data Source=/path/to/demo.db" \
    Extraction__Mode=Live \
    Extraction__Model=muse-spark-1.3 \
    MODEL_API_KEY=<from the host secret store> \
    dotnet HomeNode.dll

On first start against an absent database file, the app
creates the database and seeds the demo household (6 persons,
16 events, 23 audits, 1 promise). That seeded state is the
pristine starting state. The counts were measured against a
fresh seed during packaging, not carried over from any
earlier document.

## Pristine starting state per judge

Determinism covers the starting state and the seeded data:
every judge should meet the same composed demo day, not
whatever a previous visitor left behind. Two mechanisms are
packaged; they are not equal, and the honest recommendation
follows.

1. Per-session pristine instances (preferred, where the
   hosting makes it practical). The database path is an
   environment variable, so one app process per visitor
   session, each with its own ConnectionStrings__HomeNode
   pointing at a fresh temporary file, gives every session a
   pristine house with no reset window at all. This needs a
   host that can run a process per session behind a router.

2. Sealed backup and restore reset (the floor). The product's
   own reset machinery is the sealed backup and restore built
   for the node (src/TellhouseBackup): demo-snapshot seals
   the seeded demonstration state, and demo-reset restores
   that sealed snapshot onto the instance, writing the
   restore announcement and its audit row into the restored
   record. Where per-session instances are not practical,
   run this reset through the judging window (for example
   hourly, and after any known heavy use) so the floor
   guarantees the composed starting state on a schedule.

Recommendation for the deployment: use per-session pristine
instances where the chosen hosting makes them practical, and
keep the sealed backup and restore reset as the floor
underneath either way.

reset-demo.sh, in this repository, is neither of those
mechanisms. It is the local packaging lifecycle tool: it
starts, stops, verifies, and wipes and reseeds a local
scratch instance so the package can be exercised end to end
on a development machine. Do not present it as the deployed
product's reset mechanism.

Once deployed in the configuration above, a visitor's own
telling gets a live model response on the Standard tier. We
control the stage, not the performance.

## What was verified locally

The packaged configuration was run end to end on this
machine with the mock extraction client (no live calls spent
on packaging): the published HomeNode app started under the
deployment environment profile against a scratch database,
served every house page with HTTP 200, and every one of
those pages carried the "DEMO ENVIRONMENT" label.
reset-demo.sh start, verify, reset, verify, and stop were
exercised against a scratch database, each verify confirming
the pristine seeded day (HTTP 200, 6 persons, 16 events,
23 audits, 1 promise). The node's own suites stayed green
after the packaging changes (demo suite and node suite; the
exact counts are recorded with the packaging work). The Live
and Standard settings above are configuration values,
verified as read by the app (a full rehearsal run exercised
the same Live path on the Contributor model with fictional
data; the demo environment differs only in the model
variable, which is the rule at the top of this document
applied).
