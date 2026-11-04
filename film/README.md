# The Tellhouse film

The 3-minute film for judging. It cuts together animated story scenes and real
footage of the live app, carried by a narration you record. Everything here
builds from source: nothing large is committed (`out/`, `footage/` and `audio/`
are git-ignored). Upload the finished `out/tellhouse.mp4`.

```
film/
  script.md            the narration, in 10 sections, with what's on screen
  shotlist.md          the live footage, as plain screen-recording steps
  edl.json             the cut: segments in order, per section, with durations
  scenes/*.html        the story scenes (1920x1080, every frame a function of time)
  render-scenes.mjs    scenes -> out/scenes/*.mp4, frame by frame
  render-cards.mjs     placeholder and title cards -> PNG
  assemble.py          the cut: footage, narration, captions -> out/tellhouse.mp4
  build.sh             render what's stale, then assemble
  capture/record.mjs   records the live app, one clip per shot -> footage/
```

## Prerequisites

Node 18+ (Playwright 1.63 with Chromium), Python 3.8+, and ffmpeg/ffprobe 6+ on
PATH. Captions use the Inter font from the system (`fc-match "Inter Medium"`);
the scenes bring their own fonts.

## Build

```bash
./build.sh            # renders stale scenes, assembles out/tellhouse.mp4
./build.sh --dry      # just print the timeline: sections, takes found, lengths
```

Output, all in `out/`:
- `tellhouse.mp4` — 1920x1080, 30 fps, H.264 + AAC, captions burned in.
- `captions.srt` — the same captions as a file, to upload alongside the video.
- `contact-sheet.png` — one frame every 6 seconds, time-stamped, for review.

The build always works: a missing narration take leaves its section silent at its
scripted length, and a missing footage clip shows a placeholder card naming the
clip and what goes there. It fails loudly if the cut runs past 3:00.

## Narration (you)

Record each numbered section of `script.md` as its own take and drop the files
into `film/audio/` (create it if needed) named by section: `01.m4a`, `02.m4a`, … `10.m4a` (any common
audio format). Leading and trailing silence is trimmed, rumble is filtered, and
the whole narration is normalized to -16 LUFS. Each take starts just after its
section begins; if a take runs longer than its section, the section's last shot
holds until the take ends, so the narration sets the pace. Optional: a
royalty-free music track as `audio/music.<ext>` lies under the narration, ducked
whenever you speak.

## Live footage

The app footage must come from a live instance (Muse Spark reading). Either:

**Run the recorder** (about 5 minutes, unattended):

```bash
cd film/capture
npm install
npx playwright install chromium
BASE_URL=https://tellhouse.onrender.com SHELF_PHOTO=/path/to/pantry.jpg npm run capture
```

On Windows PowerShell, set the variables first:

```powershell
cd film/capture
npm install
npx playwright install chromium
$env:BASE_URL = "https://tellhouse.onrender.com"; $env:SHELF_PHOTO = "C:\path\to\pantry.jpg"; npm run capture
```

Clips land in `film/footage/` (or in `FOOTAGE_DIR`, for a trial run whose
clips must not reach the cut). `node record.mjs --list` shows the shots;
`--only 05,08a` records some of them; `--headed` lets you watch. The shelf shot
needs `SHELF_PHOTO` (a photo of a pantry shelf: coffee, paper towels, dishwasher tablets, dish soap) and is
skipped without it. Long model waits are cut out with a jump cut. A shot that
fails is reported and the rest carry on; re-run it with `--only`.

**Or record by hand** from `shotlist.md`, and save each clip under the file name
it gives into `film/footage/`.

## Changing things

- **Timing:** edit durations in `edl.json` (`min` lets a segment shrink if the
  cut runs long). Picture-in-picture inserts in section 08 have their own `at` and
  `duration`, and `anchor: end` uses the end of a clip.
- **A scene:** edit `scenes/<name>.html`, preview frames with
  `node render-scenes.mjs --still <name> 2 8.5` (PNGs in `out/stills/`), then
  `./build.sh` re-renders only what changed. Each scene exposes `window.DURATION`
  and `window.seek(t)`; open the HTML in a browser with `?t=7.5` to see a frame.
- **Trying other takes or clips:** `python3 assemble.py --audio DIR --footage DIR`
  builds from other folders without touching `audio/` and `footage/`.
- **Captions** come from the `>` lines of `script.md`, split at the ` / ` pauses.
  Sections 03 and 10 show their words on screen, so they carry no captions.

Rendering all scenes takes several minutes (every frame is a full screenshot);
after that, re-building the cut takes two to three minutes.
