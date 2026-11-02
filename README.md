# Tellhouse — the house that remembers

One shared record for any household: tell it once, it writes it down, and it asks before it tells anyone.

**It's your time, spend it doing what you want to do.**

People tell Tellhouse what is happening, who is doing what, who is available, what is running low, and what needs picking up. The house turns each message into an entry in a shared record. When something is meant for one person, Tellhouse tells that person and no one else, and only after someone confirms it.

Claims stay visible as claims, so a person who is waiting can see who has taken something on. Entries are shown to the people their stored audience allows. Not everyone hears everything.

Tellhouse coordinates a household: it records, reminds, and relays between the people in it. It does not give medical advice, diagnoses, or treatment recommendations. Anything health related is written down in the person's own words and handed to a person.

In many households, the person who knows first what is needed may also be the person who needs support. Tellhouse gives them a way to speak into the system in their own words. Margaret can tell the house what she needs; the record is updated once a person confirms it, and what she asked to pass on reaches the person it names. The everyday coordination around that moment, including whereabouts, groceries, and supplies running low, belongs in the same record.

Tellhouse is for any household and for the people that household chooses to include: family near or far, friends, neighbors, sitters, and helpers, whether or not they live under the same roof. It is personal: one household, its own shared record.

An agent proposes, a human confirms, and there is a written record of both.

## Try the live demo

**Live demo:** https://tellhouse.onrender.com

The first load may take a minute while the services wake.

1. Choose who you are at the top of the page (Margaret is the person the house is built around), then on Home type or say a short message, such as “Ask Daniel to pick up the milk on his way home.” The mic uses Muse Voice Transcribe.
2. Watch what happens to it, step by step: what Muse Spark read in your words, the record entry it made, who the house's rules let see it, and the action waiting for a yes. Say yes, then switch to Daniel: it is in his Inbox. (A plain statement, such as “We're out of milk,” is simply written down; there is nothing to confirm.)
3. Send something about health, such as “My knee hurts when I stand up.” The house keeps it private: no model reads it, and the trace says so.
4. Open Briefing and ask Muse Wonder what needs attention, then open The models to see every model's job, what each one never sees, and what they have done so far.

![Tellhouse Home in the demo: the household rail on the left, the "Tell the house" message box with example messages, and below it the most recent message traced step by step: the entry the model made, who the house's rules let see it, and the decision waiting on a person.](docs/images/tellhouse-home-desktop.png)

*Screenshot: Tellhouse Home, served from the build in this repository, here reading with the local stand-in (no API key; on the live deployment the same trace names Muse Spark, the model id, and the time the reading took, with token counts when the API reports them). Every message is shown as a trace: the words, what the model made of them, what the house's rules decided, and what a person decided.*

## This demo

Demo: the people in this house are invented, and no real family's data is in it.

The demo is open on purpose: anyone with its link can look around and try messages, because everyone in it is invented. That openness belongs to the demo. In the product, a house's record is not public: it is visible only to the household's own members, each through the door, under the audience stored on every entry.

Demo data and public release, stated plainly: this repository is populated with demo data for demo purposes. The household, its people, and its records are invented. The keys, phrases, and tokens in the demo seed are toy material published for the demo; the live deployed demo runs on fresh values set at deployment time, not on anything in this repository. In this public release, the two member assistants are scripted stand-ins that send set lines, and the message understanding runs on a deterministic local stand-in unless a Meta Model API key is supplied for the live model path.

There is no Tellhouse app to install. Home is where a person sends the house a message in their own words, and the shared record is what the household can read. In the live configuration, a message is understood by Meta's Muse Spark model, through the Meta Model API, into record entries. A person confirms what needs confirming, and the confirmed record is what the house works from. The model proposes structured events; the house's own machinery confirms, routes, and records them. By default, the local demo runs the same path on a deterministic local stand-in.

The deployed demo uses separate services for the home node, the sealed relay, and the two member stand-ins. The home node holds the record and the door. The relay carries messages it cannot read. Each stand-in is a reference assistant for a fictional member, built to the house protocol.

The house layer's surfaces are:

The pages follow one loop, seen through one person's eyes at a time (the person switcher at the top):

- **Home:** say it. Type or speak a message to the house, mark it Private if it should go to no model, and watch what happens to it, step by step.
- **Inbox:** needs you. What is waiting on this person: a yes the house is asking for, a question about who should see something, plan items, and what the house has passed on.
- **Briefing:** the house now. What needs attention (the house's own rules, and Muse Wonder's review), what is on the shelf (SAM 3.1), who has taken what on, what is running low, and what is planned. Plans and Claims open from here.
- **Timeline:** the record. Every message, newest first, from the words that came in to where they went.
- **The models:** each Meta model's job in the house, what it never sees, the exact instructions and output schema, and live counts.
- **Assistants:** who is connected to the house through their own assistant, and how one joins.

## Meta's models in Tellhouse

Each model has one job at one step of the loop, all through the Meta Model API with the same key:

- **Muse Voice Transcribe** (`muse-voice-transcribe-1.0`) hears a spoken message on Home, with the household's names and supplies as keywords; the words come back to be checked before sending, and the audio is not kept.
- **Muse Spark** (`muse-spark-1.3`) reads every message into typed record entries and at most one proposed action, under a strict JSON schema.
- **Muse Wonder** (`muse-wonder-1.0`) reviews the shared record a person may see, when they ask on Briefing, and reports conflicts, slipping claims and supplies that will run out, citing the entries it relied on; the house drops any finding that cites something it was not shown.
- **SAM 3.1** (`sam-3.1`) looks at a photo of the supply shelf on Briefing: the house's own supply list becomes its prompts, one noun phrase per request, and it outlines and counts what it finds. Where it sees none, the page offers "We're out of …" for the message box; a photo writes nothing to the record.
- **Muse Image** (`muse-image-1.0`) draws a picture card when the house passes a message on to a child ("come down for dinner at 6:30" becomes a plate and a big clock), so it can be taken in without reading. The brief it gets carries no one's name.

Private things are kept away from every model: a message about health or medicine, or one marked Private, is written down by the house's own deterministic rules on the server, and if those rules cannot write it down nothing is sent anywhere. Muse Wonder is never shown a private entry, a health entry, or a medicine; SAM 3.1 is never asked to look for a medicine; Muse Image draws only from what the whole household may see, and never anything health-shaped. `tools/model-smoke.py` calls each model once with your key and reports whether it answered in the expected shape.

## Run it locally

Run the home node:

    dotnet run --project src/HomeNode

Then open Home in a browser at http://localhost:5042. On first start, the demo seeds a fictional household and one ordinary day. Extraction defaults to a deterministic local stand-in, so the demo runs with no API key and no spend.

To run the same extraction path with Meta's model, supply a Meta Model API key through the environment. The repository's `.env.example` shows the variable name, `MODEL_API_KEY`, with a redacted value.

Set the real `MODEL_API_KEY` in the host's secret store or environment, never in the repository. Set the extraction mode to Live and the model to `muse-spark-1.3` (Muse Spark) in the host configuration. DEPLOY.md documents the live configuration used for the judged demo environment, including the deployed environment-variable form.

Two folders are the build's own records rather than services: `lane-i-integration/` and `lane-o-e2e/` hold the test lanes used while building, and `src/HouseholdCoordinator/` holds the first build of the house, kept as a record. The deployed demo runs `src/HomeNode`.

The relay and the stand-ins are separate services, started alongside the home node when you want the full federated demo. The relay is a Python service (see `relay/README.md` for its configuration and start command), and each stand-in is its own project under `standins/` (see the README in each stand-in's folder for the environment it needs). `src/HomeNode/README.md` describes how the node talks to both.

TEST-EVIDENCE.md records what was run against the code in this repository and what it showed.

## Built on open source

Tellhouse stands on open-source projects, and on one model:

- .NET and ASP.NET Core (MIT): the home node, the two stand-in assistants, the backup tool, and the protocol library are C# on .NET 10, and Home is ASP.NET Core Razor Pages.
- Entity Framework Core (MIT) with its SQLite provider: the house record lives in a SQLite database (SQLite is public domain).
- Python 3 (PSF), standard library only: the sealed relay.
- Bootstrap 5.3.3 and jQuery 3.7.1 (both MIT), with the jQuery Validation plugins, vendored under `wwwroot/lib`: Home's styling and client scripts.
- Muse Spark, Meta's model, through the Meta Model API: the model call at the center of the design. It reads a message in a person's own words and returns the structured events the house then works from.

What we built on top of them is the house itself: the shared record and Home, the confirmation step a person takes before anything is settled on the record, the audience rules that decide who may see and be told what, the sealed member protocol and the relay that carries messages it cannot read, the door ceremony that connects a member's own assistant, and the sealed backup and restore that also powers the demo reset.

Standalone: this repository runs on its own. Nothing in it depends on any pre-existing Tellhouse infrastructure; the only outside service it calls is the Meta Model API, with the key supplied as the `MODEL_API_KEY` environment variable (see `.env.example`).

## Where this is going

Direction: Tellhouse is being built so the house's record lives in the home, on a device the household controls; our servers would carry messages between the house and its members, never keep the memory.

Our pledge for that direction: we will not keep your data, we do not want it, and we will never sell it. In this demo, message text is sent to the Meta Model API to be understood in the live configuration. We are here to facilitate. The house coordinates between the people in it and, as the direction builds out, between each person's own assistant where they have one, as much or as little as each person chooses.

## Provenance

The idea grew out of how one household already keeps each other informed day to day, as a practice. No working version of Tellhouse existed before this event: the concept was created after the hackathon was announced, transformed entirely once the event started, and evolved organically in a real household out of a household need. The code, the data model, the screens, and the demo were designed and built entirely during the event; no pre-existing code, screens, or footage are used.
