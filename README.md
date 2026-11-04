# Tellhouse — the house that remembers

Tell the house once, in your own words. It writes it down, asks before it tells anyone, and remembers who said they'd do what.

**[Try the live demo](https://tellhouse.onrender.com)** · built with Meta's Muse Spark, Muse Wonder, Muse Voice Transcribe, SAM 3.1 and Muse Image

> Households don't fail on forgotten facts. They fail on forgotten promises: when someone said they'd handle it, and the person waiting quietly stops believing anyone is coming.

In a lot of families, the person who knows first what's needed is the person who needs the help, and most tools give her no way in. Tellhouse gives her the front door. Margaret, 78, says what she needs in her own words, or taps one big button. The house writes it down, asks before it passes anything on, and keeps anything about her health away from every model. Everyone else in the household tells the house what's happening the same way: where they are, what's running low, what they'll pick up. The house remembers it, so the person who is waiting can see that someone is coming.

An agent proposes, a person confirms, and there is a written record of both.

**It's your time. Spend it doing what you want to do.**

## Try it in two minutes

Open **https://tellhouse.onrender.com** and follow the path on Home, or:

1. **Start as Margaret.** Tap *Ask Daniel to pick up milk*. Muse Spark reads it; the house asks before it tells Daniel. Say yes.
2. **Be Daniel.** It's in his Inbox, in her words.
3. **Back to Margaret:** *"My knee hurts when I stand up."* No model reads it. The house writes it down in her words and asks whether to tell Daniel.
4. **Be Daniel:** *"I'll get Mia at 3."* The house keeps it on Claims, next to Daniel's *home by three* from the demo's starting Tuesday, kept at 2:55 PM.
5. **As Margaret, ask Timmy to come down for dinner at 6:30**, then open Timmy's Inbox: Muse Image has drawn him a picture card.
6. **How it works:** five Meta models, one job each, and what none of them ever sees. Under every message, *See every step* shows exactly what each one did.

The demo is shared and open on purpose, because everyone in it is invented. It puts itself back to its starting day after 20 quiet minutes, and *Start over* in the demo bar does the same at once.

![Margaret's front door in Tellhouse: "Good morning, Margaret." Her words, "Ask Daniel to pick up milk on his way home", the house's reply asking whether to tell Daniel, big Yes and No buttons, a folded summary of how the house handled it, and four big choices: Where's Daniel?, Tell Daniel I'm all right, Ask Daniel to pick up milk, Put my program on.](docs/images/tellhouse-margaret.png)

*Margaret's front door, from the build in this repository, here reading with the local stand-in (no API key). On the live demo the same summary names Muse Spark, the model id, and how long the reading took.*

## This demo

Demo: the people in this house are invented, and no real family's data is in it.

The demo is open on purpose: anyone with its link can look around and try messages, because everyone in it is invented. That openness belongs to the demo. In the product, a house's record is not public: it is visible only to the household's own members, each through the door, under the audience stored on every entry.

Demo data and public release, stated plainly: this repository is populated with demo data for demo purposes. The household, its people, and its records are invented. The keys, phrases, and tokens in the demo seed are toy material published for the demo; the live deployed demo runs on fresh values set at deployment time, not on anything in this repository. In this public release, the two member assistants are scripted stand-ins that send set lines, and the message understanding runs on a deterministic local stand-in unless a Meta Model API key is supplied for the live model path.

There is no Tellhouse app to install. Home is where a person sends the house a message in their own words, and the shared record is what the household can read. In the live configuration, a message is understood by Meta's Muse Spark model, through the Meta Model API, into record entries. A person confirms what needs confirming, and the confirmed record is what the house works from. The model proposes structured events; the house's own machinery confirms, routes, and records them. By default, the local demo runs the same path on a deterministic local stand-in.

The deployed demo uses separate services for the home node, the sealed relay, and the two member stand-ins. The home node holds the record and the door. The relay carries messages it cannot read. Each stand-in is a reference assistant for a fictional member, built to the house protocol.

The pages follow one loop, seen through one person's eyes at a time (the person switcher at the top):

- **Home:** tell the house. Margaret gets her own front door: big type, big choices, and the house's reply as the main thing on the page. Everyone else gets the message box. Mark a message Private and no model reads it. After you send, the house answers, asks for the one yes it needs, and folds every step underneath.
- **Inbox:** what is waiting on this person, and what the house has passed on to them.
- **Briefing:** the house right now. What needs attention (the house's own rules, and Muse Wonder's review), what is on the shelf (SAM 3.1), who is on what, and what is running low. Plans and Claims open from here.
- **Timeline:** everything said to the house, newest first, from the words that came in to where they went.
- **How it works:** each Meta model's job in the house, what it never sees, the exact instructions and output schema, and live counts.
- **Assistants:** who is connected through their own assistant, how one joins, and a button that asks Lisa's assistant to send the house a sealed note.

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
