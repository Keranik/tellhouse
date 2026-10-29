# Tellhouse

A household coordinator that removes the friction from daily tasks.

Tellhouse is a household coordinator, the house that remembers.
Everyone in the circle tells it things once, in passing: who is
doing what, who is available, what is running low, what needs
picking up, and what a person who needs support has asked for or
been promised. The shared record holds promises as promises, so a
person who is waiting can see that someone is coming. Tells the
person the record names for it: a confirmed telling is routed by
its stored audience, to that person and no one else. Answers a
person the entry's stored audience allows, and enrolled members
who have joined through the door. Not everyone hears everything.

Tellhouse coordinates a household: it records, reminds, and relays
between the people in the household's circle. It does not give
medical advice, diagnoses, or treatment recommendations; anything
health related is written down in the person's own words and
handed to a person.

In most households looking after one of their own, the person who
knows first what is needed is the person who needs support, and
most tools give that person no way to speak into the system.
Tellhouse hands her the front door: she tells the house in her
own words, the record is updated from her telling once a person
confirms it, and what she asked to pass on reaches the person it
names. Supporting her is the heart of the house; the everyday
coordination around it, the whereabouts, the groceries, the
supplies running low, is the same heart beating in the rest of
the house.

Tellhouse is for any household coordinating support for an adult
family member, and for the circle that household chooses: family
near or far, friends, neighbors, sitters, and helpers, whether or
not they live under the same roof. It is personal: one household,
its own shared record.

An agent that proposes, a human who confirms, and a written record
of both.

## This demo

Demo: the people in this house are invented, it runs on one
computer, and no real family's data is in it.

Demo data and public release, stated plainly: this repository
is populated with demo data for demo purposes. The household,
its people, and its records are invented, and the keys,
phrases, and tokens in the demo seed are toy material
published for the demo; the live deployed demo runs on fresh
values set at deployment time, not on anything in this
repository. Some functionality is limited or removed in this
public release version.

There is no Tellhouse app to install. What is here is the house
layer: a front door where a person tells the house something in
their own words, and the shared record the household can read. In
the live configuration, a telling is understood by Meta's Muse
Spark model, through the Meta Model API, into record entries; a
person confirms what needs confirming, and the confirmed record
is what the house works from. The model proposes structured
events; the house's own machinery confirms, routes, and
records them. By default the demo runs the same
path on a deterministic local stand-in. This demo runs one
household on one computer: the architecture at demo scale. In
this build the one computer runs three processes: the home node,
the sealed relay, and the member stand-ins.

The house layer's surfaces:

- The front door: tell the house; ask it questions.
- The timeline: the shared record.
- The brief: what the house would say this morning, filtered by
  who is asking.
- Plans and promises: the record's commitments, who agreed, and
  what is still open.
- What landed with you: each person's own view of what the house
  has routed to them.
- Connections: who is connected to the house, and through which
  assistant.

The parts of the tree, in plain words: the home node in
src/HomeNode holds the record and the door. relay/ is a sealed
relay that carries messages it cannot read. standins/ holds two
reference assistants for the fictional members, built to the
house protocol. src/TellhouseBackup is the sealed backup and
restore tool. src/HouseholdCoordinator is the earlier
single-process build, kept in the tree as the fallback.

Run it:

    dotnet run --project src/HomeNode

Then open the front door in a browser. On first start the demo
seeds a fictional household and one ordinary day. Extraction
defaults to a deterministic local stand-in, so the demo runs
with no API key and no spend; DEPLOY.md documents the live
configuration used for the judged demo environment.

The relay and the stand-ins are separate processes, started
alongside the home node when you want the full three-process
demo: the relay is a Python service (see relay/README.md for its
configuration and start command), and each stand-in is its own
project under standins/ (see the README in each stand-in's
folder for the environment it needs). src/HomeNode/README.md
describes how the node talks to both.

TEST-EVIDENCE.md records what was run against the code in
this repository and what it showed.

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
and restore that also powers the demo reset.

Standalone: this repository runs on its own. Nothing in
it depends on any pre-existing Tellhouse infrastructure;
the only outside service it calls is the Meta Model API,
with the key supplied as the MODEL_API_KEY environment
variable (see .env.example).

## Where this is going

Direction: Tellhouse is being built so the house's record lives
in the home, on a device the household controls; our servers
would carry messages between the house and its members, never
keep the memory.

Our pledge for that direction: we will not keep your data, we
do not want it, and we will never sell it. In this demo the
record stays on the demo computer; in the live configuration,
telling text is sent to the Meta Model API to be understood. We
are here to facilitate. The house coordinates between the people
in it, and, as the direction builds out, between each person's
own assistant where they have one, as much or as little as each
person chooses.

## Provenance

The idea grew out of how one household already keeps each other
informed day to day, as a practice. No working version of
Tellhouse existed before this event: the concept was created
after the hackathon was announced, and the code, the data model,
and the demo were designed and built entirely during the event.
