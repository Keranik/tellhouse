#!/usr/bin/env python3
"""Assemble the Tellhouse film from edl.json.

    python3 assemble.py          build out/tellhouse.mp4, out/captions.srt, out/contact-sheet.png
    python3 assemble.py --dry    print the timeline (sections, takes, durations) and stop
    --audio DIR / --footage DIR  take narration or footage from another folder (default audio/, footage/)

How the cut is made
- The story scenes are rendered beforehand (render-scenes.mjs) to out/scenes/*.mp4.
- Narration drives timing. audio/NN.<anything> is the take for script section NN
  (01..10). Each take is cleaned (rumble cut, silence trimmed at both ends) and
  starts `lead` seconds into its section. When a take is longer than its section,
  the section's last segment holds (freezes) until the take ends plus `tail`.
  Without a take, a section keeps its scripted duration in silence.
- If the whole cut would run past max_duration, segments that declare a `min`
  shrink (only from slack the narration does not need).
- Footage clips live in footage/<clip>. A missing clip previews as a placeholder
  card that says what goes there, so the full cut always builds.
- Segments crossfade into each other; narration is loudness-normalized
  (EBU R128, -16 LUFS integrated, -1.5 dBTP) in two passes; audio/music.* if
  present lies under the narration, ducked whenever someone is speaking.
- Captions come from the narration lines in script.md, timed across each take.
  They are burned in (styled ASS) and also written as out/captions.srt for upload.
- The finished file is probed; a cut longer than max_duration fails loudly.
"""
import glob
import json
import math
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out")
WORK = os.path.join(OUT, "work")
W, H, BG = 1920, 1080, "0x191919"
def _opt(name, default):
    if name in sys.argv:
        i = sys.argv.index(name)
        return os.path.abspath(sys.argv[i + 1])
    return os.path.join(HERE, default)


AUDIO_DIR = _opt("--audio", "audio")
FOOTAGE_DIR = _opt("--footage", "footage")
AUDIO_EXT = {".m4a", ".mp3", ".wav", ".aac", ".ogg", ".oga", ".opus", ".flac", ".webm",
             ".mp4", ".mov", ".caf", ".aif", ".aiff", ".amr", ".3gp", ".wma"}


def run(cmd, cwd=None, quiet=True):
    proc = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)
    if proc.returncode != 0:
        sys.stderr.write(proc.stderr[-6000:] + "\n")
        sys.exit(f"FAILED: {' '.join(cmd[:6])} ... (exit {proc.returncode})")
    return proc


def duration_of(path):
    out = run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path]).stdout.strip()
    return float(out)


def ffmpeg(*args, cwd=None):
    return run(["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", *args], cwd=cwd)


def x264(crf="16", preset="veryfast"):
    return ["-an", "-c:v", "libx264", "-preset", preset, "-crf", crf, "-pix_fmt", "yuv420p", "-r", "30"]


# ---------------------------------------------------------------- script.md

def read_script():
    """Section number -> narration text, from the '>' lines of script.md."""
    text = open(os.path.join(HERE, "script.md"), encoding="utf-8").read()
    sections, current = {}, None
    for line in text.splitlines():
        head = re.match(r"^##\s+(\d\d)\b", line)
        if head:
            current = head.group(1)
            sections[current] = []
            continue
        if line.startswith("### ") or line.startswith("---"):
            current = None
        if current and line.startswith(">"):
            sections[current].append(line[1:].strip())
    return {k: " ".join(v) for k, v in sections.items() if v}


def caption_chunks(text, limit=84):
    """Split narration at the script's ' / ' pauses and sentence ends into
    caption-sized chunks of at most ~limit characters."""
    phrases = []
    for p in text.split(" / "):
        # Every sentence starts its own piece ("SAM 3.1" is not a sentence end).
        phrases += [q.strip() for q in re.split(r"(?<=[.?!])\s+(?=[A-Z“\"‘'—])", p) if q.strip()]
    pieces = []
    for p in phrases:
        if len(p) <= limit:
            pieces.append(p)
            continue
        parts = re.split(r"(?<=[.?!])\s+|(?<=[,;—])\s+", p)
        cur = ""
        for part in parts:
            if cur and len(cur) + 1 + len(part) > limit:
                pieces.append(cur)
                cur = part
            else:
                cur = f"{cur} {part}".strip()
        if cur:
            pieces.append(cur)
    chunks, cur = [], ""
    for p in pieces:
        # A finished sentence ends a caption (unless it is very short), so a
        # new thought starts on a fresh card.
        ends_sentence = re.search(r"[.?!][\"”’]?$", cur) and len(cur) >= 20
        if cur and (len(cur) + 1 + len(p) > limit or ends_sentence):
            chunks.append(cur)
            cur = p
        else:
            cur = f"{cur} {p}".strip()
    if cur:
        chunks.append(cur)
    return [re.sub(r"\s+", " ", c) for c in chunks]


def two_lines(text, width=46):
    if len(text) <= width:
        return [text]
    mid = len(text) // 2
    spaces = [i for i, ch in enumerate(text) if ch == " "]
    cut = min(spaces, key=lambda i: abs(i - mid)) if spaces else mid
    return [text[:cut].strip(), text[cut:].strip()]


def ts_srt(t):
    ms = int(round(t * 1000))
    return f"{ms // 3600000:02d}:{ms // 60000 % 60:02d}:{ms // 1000 % 60:02d},{ms % 1000:03d}"


def ts_ass(t):
    cs = int(round(t * 100))
    return f"{cs // 360000}:{cs // 6000 % 60:02d}:{cs // 100 % 60:02d}.{cs % 100:02d}"


# ---------------------------------------------------------------- inputs

def find_takes():
    takes = {}
    for path in sorted(glob.glob(os.path.join(AUDIO_DIR, "*"))):
        name, ext = os.path.splitext(os.path.basename(path))
        if ext.lower() in AUDIO_EXT and re.fullmatch(r"\d\d", name):
            takes[name] = path
    music = [p for p in glob.glob(os.path.join(AUDIO_DIR, "music.*")) if os.path.splitext(p)[1].lower() in AUDIO_EXT]
    return takes, (music[0] if music else None)


def clean_take(section, src):
    dst = os.path.join(WORK, "takes", f"{section}.wav")
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    trim = "silenceremove=start_periods=1:start_threshold=-45dB:start_silence=0.12"
    ffmpeg("-i", src, "-vn", "-af", f"highpass=f=75,{trim},areverse,{trim},areverse",
           "-ar", "48000", "-ac", "1", dst)
    return dst, duration_of(dst)


# ---------------------------------------------------------------- timeline

def plan(edl, takes_len):
    segs = [dict(s) for s in edl["segments"]]
    nar = edl.get("narration", {})
    sect_cfg = edl.get("sections", {})
    order = []
    for s in segs:
        if s["section"] not in order:
            order.append(s["section"])

    def lead(sec):
        return sect_cfg.get(sec, {}).get("lead", nar.get("lead", 0.6))

    tail = nar.get("tail", 0.9)
    need = {}
    for sec in order:
        mine = [s for s in segs if s["section"] == sec]
        scripted = sum(s["duration"] for s in mine)
        need[sec] = lead(sec) + takes_len[sec] + tail if sec in takes_len else 0.0
        if need[sec] > scripted:
            mine[-1]["duration"] = round(mine[-1]["duration"] + need[sec] - scripted, 3)
            mine[-1]["held"] = True
    # Fit under the limit by shrinking segments with a 'min', from slack only.
    limit = edl.get("max_duration", 180.0) - 0.5
    total = sum(s["duration"] for s in segs)
    if total > limit:
        over = total - limit
        caps = {}
        for sec in order:
            mine = [s for s in segs if s["section"] == sec]
            sec_total = sum(s["duration"] for s in mine)
            shrinkable = sum(max(0.0, s["duration"] - s.get("min", s["duration"])) for s in mine)
            caps[sec] = max(0.0, min(shrinkable, sec_total - need[sec]))
        avail = sum(caps.values())
        take = min(over, avail)
        for sec in order:
            if avail <= 0 or caps[sec] <= 0:
                continue
            want = take * caps[sec] / avail
            mine = [s for s in segs if s["section"] == sec and s["duration"] > s.get("min", s["duration"])]
            room = sum(s["duration"] - s["min"] for s in mine)
            for s in mine:
                s["duration"] = round(s["duration"] - want * (s["duration"] - s["min"]) / room, 3)
    t = 0.0
    starts = {}
    for s in segs:
        s["start"] = round(t, 3)
        starts.setdefault(s["section"], t)
        t += s["duration"]
    return segs, order, starts, round(t, 3), lead


# ---------------------------------------------------------------- video segments

def cards_needed(segs):
    """Placeholder/title cards to render, as specs for render-cards.mjs."""
    specs = []
    for i, s in enumerate(segs):
        if s["type"] == "footage" and not footage_path(s["clip"]):
            specs.append({"out": os.path.join(WORK, f"card_{i:02d}.png"), "kind": "placeholder",
                          "title": s["clip"], "sub": s.get("describe", "")})
        if s["type"] == "card":
            specs.append({"out": os.path.join(WORK, f"card_{i:02d}.png"), "kind": "card",
                          "title": s.get("title", ""), "sub": s.get("sub", "")})
        if s["type"] == "pip":
            for j, ins in enumerate(s["inserts"]):
                if not footage_path(ins["clip"]):
                    specs.append({"out": os.path.join(WORK, f"card_{i:02d}_{j}.png"), "kind": "placeholder",
                                  "title": ins["clip"], "sub": ins.get("describe", "")})
    return specs


VIDEO_EXT = (".mp4", ".webm", ".mov", ".mkv", ".m4v")


def footage_path(clip):
    """The footage file for a clip name, in any common video format, or None."""
    exact = os.path.join(FOOTAGE_DIR, clip)
    if os.path.exists(exact):
        return exact
    stem = os.path.splitext(clip)[0]
    for ext in VIDEO_EXT:
        alt = os.path.join(FOOTAGE_DIR, stem + ext)
        if os.path.exists(alt):
            return alt
    return None


def footage_window(path, length, anchor, max_speed, start=0.0):
    """Which part of a clip to use, and how fast, to fill `length` seconds."""
    clip = duration_of(path)
    avail = max(0.05, clip - start)
    speed = 1.0
    if avail > length:
        speed = min(max_speed, avail / length)
    content = min(avail, length * speed)
    begin = max(start, clip - content) if anchor == "end" else start
    return begin, content, speed


def footage_chain(label_in, path, length, anchor, max_speed, w, h, start=0.0):
    begin, content, speed = footage_window(path, length, anchor, max_speed, start)
    return (f"{label_in}trim=start={begin:.3f}:duration={content:.3f},setpts=(PTS-STARTPTS)/{speed:.4f},"
            f"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color={BG},"
            f"fps=30,tpad=stop_mode=clone:stop_duration={length:.3f},trim=duration={length:.3f},setpts=PTS-STARTPTS")


def scene_input(name):
    path = os.path.join(OUT, "scenes", f"{name}.mp4")
    if not os.path.exists(path):
        sys.exit(f"missing scene render {path} — run: node render-scenes.mjs {name}")
    return path


def make_segment(i, s, length):
    dst = os.path.join(WORK, f"seg_{i:02d}.mp4")
    hold = f"tpad=stop_mode=clone:stop_duration={length:.3f},trim=duration={length:.3f},setpts=PTS-STARTPTS,format=yuv420p"
    if s["type"] == "scene":
        ffmpeg("-ss", f"{s.get('from', 0)}", "-i", scene_input(s["scene"]), "-vf", f"fps=30,{hold}", *x264(), dst)
    elif s["type"] in ("footage", "card"):
        clip = footage_path(s.get("clip", "")) if s["type"] == "footage" else None
        if clip:
            chain = footage_chain("[0:v]", clip, length, s.get("anchor", "start"), s.get("max_speed", 1.5), W, H, s.get("from", 0.0))
            ffmpeg("-i", clip, "-filter_complex", chain + ",format=yuv420p[v]", "-map", "[v]", *x264(), dst)
        else:
            ffmpeg("-loop", "1", "-framerate", "30", "-t", f"{length:.3f}", "-i", os.path.join(WORK, f"card_{i:02d}.png"),
                   "-vf", hold, *x264(), dst)
    elif s["type"] == "pip":
        x, y, bw, bh = s["box"]
        args = ["-ss", f"{s.get('from', 0)}", "-i", scene_input(s["scene"])]
        graph = [f"[0:v]fps=30,{hold}[b0]"]
        prev = "b0"
        for j, ins in enumerate(s["inserts"]):
            at, d = ins["at"], ins["duration"]
            clip = footage_path(ins["clip"])
            if clip:
                args += ["-i", clip]
                chain = footage_chain(f"[{j + 1}:v]", clip, d, ins.get("anchor", "end"), ins.get("max_speed", 2.0), bw, bh)
            else:
                args += ["-loop", "1", "-framerate", "30", "-t", f"{d:.3f}", "-i", os.path.join(WORK, f"card_{i:02d}_{j}.png")]
                chain = (f"[{j + 1}:v]scale={bw}:{bh},fps=30,tpad=stop_mode=clone:stop_duration={d:.3f},"
                         f"trim=duration={d:.3f},setpts=PTS-STARTPTS")
            graph.append(f"{chain},pad={bw + 6}:{bh + 6}:3:3:color=0x4a4a4a,format=yuva420p,"
                         f"fade=t=in:st=0:d=0.3:alpha=1,fade=t=out:st={max(0.0, d - 0.3):.3f}:d=0.3:alpha=1,"
                         f"setpts=PTS+{at:.3f}/TB[p{j}]")
            graph.append(f"[{prev}][p{j}]overlay=x={x - 3}:y={y - 3}:eof_action=pass:enable='between(t,{at:.3f},{at + d:.3f})'[b{j + 1}]")
            prev = f"b{j + 1}"
        graph.append(f"[{prev}]format=yuv420p[v]")
        script = os.path.join(WORK, f"seg_{i:02d}.graph")
        open(script, "w").write(";\n".join(graph))
        ffmpeg(*args, "-filter_complex_script", script, "-map", "[v]", "-t", f"{length:.3f}", *x264(), dst)
    else:
        sys.exit(f"unknown segment type {s['type']}")
    return dst


# ---------------------------------------------------------------- audio

def loudnorm(src, dst):
    probe = run(["ffmpeg", "-hide_banner", "-i", src, "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-"])
    stats = json.loads(probe.stderr[probe.stderr.rindex("{"):probe.stderr.rindex("}") + 1])
    flt = ("loudnorm=I=-16:TP=-1.5:LRA=11:linear=true:"
           f"measured_I={stats['input_i']}:measured_TP={stats['input_tp']}:measured_LRA={stats['input_lra']}:"
           f"measured_thresh={stats['input_thresh']}:offset={stats['target_offset']},aresample=48000")
    ffmpeg("-i", src, "-af", flt, "-ar", "48000", "-ac", "2", dst)


def build_audio(total, placed, music):
    """placed: [(path, start_seconds)]. Returns the path of the final mix."""
    os.makedirs(WORK, exist_ok=True)
    raw = os.path.join(WORK, "narration_raw.wav")
    nar = os.path.join(WORK, "narration.wav")
    args = ["-f", "lavfi", "-t", f"{total:.3f}", "-i", "anullsrc=r=48000:cl=stereo"]
    graph, labels = [], ["[0:a]"]
    for k, (path, at) in enumerate(placed):
        args += ["-i", path]
        ms = int(round(at * 1000))
        graph.append(f"[{k + 1}:a]aresample=48000,aformat=channel_layouts=stereo,adelay={ms}|{ms}[t{k}]")
        labels.append(f"[t{k}]")
    graph.append("".join(labels) + f"amix=inputs={len(labels)}:normalize=0:duration=first:dropout_transition=0[mix]")
    script = os.path.join(WORK, "narration.graph")
    open(script, "w").write(";\n".join(graph))
    ffmpeg(*args, "-filter_complex_script", script, "-map", "[mix]", "-t", f"{total:.3f}", "-c:a", "pcm_s16le", raw)
    if placed:
        loudnorm(raw, nar)
    else:
        shutil.copyfile(raw, nar)
    if not music:
        return nar
    mixed = os.path.join(WORK, "mix.wav")
    graph = (f"[0:a]aresample=48000,aformat=channel_layouts=stereo,atrim=duration={total:.3f},volume=-16dB,"
             f"afade=t=in:st=0:d=2,afade=t=out:st={max(0.0, total - 3.5):.3f}:d=3.5[m];"
             "[1:a]asplit=2[n][key];"
             "[m][key]sidechaincompress=threshold=0.02:ratio=8:attack=15:release=500[duck];"
             "[n][duck]amix=inputs=2:normalize=0:duration=first[out]")
    ffmpeg("-stream_loop", "-1", "-i", music, "-i", nar, "-filter_complex", graph, "-map", "[out]",
           "-t", f"{total:.3f}", "-c:a", "pcm_s16le", mixed)
    return mixed


# ---------------------------------------------------------------- captions

ASS_HEAD = """[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Cap,Inter Medium,44,&H00F2F2F2,&H000000FF,&H40121212,&H40121212,0,0,0,0,100,100,0.2,0,3,16,0,2,240,240,58,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""


def write_captions(edl, order, starts, ends, takes_len, lead):
    script = read_script()
    cues = []
    for sec in order:
        if not edl.get("sections", {}).get(sec, {}).get("captions", True) or sec not in script:
            continue
        begin = starts[sec] + lead(sec)
        span = takes_len.get(sec)
        if span is None:
            span = max(1.5, ends[sec] - begin - edl.get("narration", {}).get("tail", 0.9))
        chunks = caption_chunks(script[sec])
        weights = [len(c) + 8 for c in chunks]
        t = begin
        for c, wgt in zip(chunks, weights):
            d = span * wgt / sum(weights)
            cues.append((t, t + d - 0.08, c))
            t += d
    with open(os.path.join(OUT, "captions.srt"), "w", encoding="utf-8") as f:
        for n, (a, b, c) in enumerate(cues, 1):
            f.write(f"{n}\n{ts_srt(a)} --> {ts_srt(b)}\n" + "\n".join(two_lines(c)) + "\n\n")
    with open(os.path.join(WORK, "captions.ass"), "w", encoding="utf-8") as f:
        f.write(ASS_HEAD)
        for a, b, c in cues:
            text = "\\N".join(two_lines(c))
            f.write("Dialogue: 0," + ts_ass(a) + "," + ts_ass(b) + ",Cap,,0,0,0,," + text + "\n")
    return len(cues)


# ---------------------------------------------------------------- main

def main():
    dry = "--dry" in sys.argv
    no_captions = "--no-captions" in sys.argv
    edl = json.load(open(os.path.join(HERE, "edl.json"), encoding="utf-8"))
    X = edl.get("crossfade", 0.4)
    os.makedirs(WORK, exist_ok=True)
    takes, music = find_takes()
    takes_clean, takes_len = {}, {}
    for sec, src in takes.items():
        takes_clean[sec], takes_len[sec] = clean_take(sec, src)
    segs, order, starts, total, lead = plan(edl, takes_len)
    ends = {}
    for s in segs:
        ends[s["section"]] = s["start"] + s["duration"]
    print(f"{'sec':>3} {'start':>7} {'length':>7}  take")
    for sec in order:
        tk = f"{takes_len[sec]:.1f}s ({os.path.basename(takes[sec])})" if sec in takes else "— (silent, scripted timing)"
        print(f"{sec:>3} {starts[sec]:7.1f} {ends[sec] - starts[sec]:7.1f}  {tk}")
    print(f"total {total:.1f}s; music: {os.path.basename(music) if music else 'none'}")
    missing = [s["clip"] for s in segs if s["type"] == "footage" and not footage_path(s["clip"])]
    missing += [i["clip"] for s in segs if s["type"] == "pip" for i in s["inserts"] if not footage_path(i["clip"])]
    if missing:
        print("placeholder cards for missing footage: " + ", ".join(missing))
    if dry:
        return
    specs = cards_needed(segs)
    if specs:
        spec_path = os.path.join(WORK, "cards.json")
        json.dump(specs, open(spec_path, "w"))
        run(["node", os.path.join(HERE, "render-cards.mjs"), spec_path])
    files = []
    for i, s in enumerate(segs):
        length = s["duration"] + (X if i < len(segs) - 1 else 0.0)
        files.append(make_segment(i, s, length))
        print(f"  segment {i:02d} {s['type']:<7} {s.get('scene') or s.get('clip') or s.get('title', ''):<28} {s['duration']:6.2f}s{'  (held for narration)' if s.get('held') else ''}")
    placed = [(takes_clean[sec], starts[sec] + lead(sec)) for sec in order if sec in takes_clean]
    audio = build_audio(total, placed, music)
    n_cues = 0 if no_captions else write_captions(edl, order, starts, ends, takes_len, lead)
    graph, prev, offset = [], "0:v", 0.0
    for i in range(1, len(files)):
        offset += segs[i - 1]["duration"]
        graph.append(f"[{prev}][{i}:v]xfade=transition=fade:duration={X}:offset={offset:.3f}[x{i}]")
        prev = f"x{i}"
    tail = [f"fade=t=in:st=0:d={edl.get('fade_in', 0.8)}",
            f"fade=t=out:st={max(0.0, total - edl.get('fade_out', 1.4)):.3f}:d={edl.get('fade_out', 1.4)}"]
    if n_cues:
        tail.append("ass=captions.ass")
    graph.append(f"[{prev}]" + ",".join(tail) + ",format=yuv420p[vout]")
    script = os.path.join(WORK, "final.graph")
    open(script, "w").write(";\n".join(graph))
    out = os.path.join(OUT, "tellhouse.mp4")
    inputs = []
    for f in files:
        inputs += ["-i", f]
    ffmpeg(*inputs, "-i", audio, "-filter_complex_script", script, "-map", "[vout]", "-map", f"{len(files)}:a",
           "-t", f"{total:.3f}", "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-r", "30",
           "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart", out, cwd=WORK)
    # Review sheet: one frame every 6 seconds, stamped with its time.
    frames = math.ceil(duration_of(out) / 6)
    rows = math.ceil(frames / 5)
    ffmpeg("-i", out, "-vf", "select='isnan(prev_selected_t)+gte(t-prev_selected_t\\,6)',scale=384:216,"
           "drawtext=font=Inter:text='%{pts\\:hms}':x=8:y=8:fontsize=18:"
           f"fontcolor=white:box=1:boxcolor=black@0.6:boxborderw=4,tile=5x{rows}:padding=6:margin=6:color=0x0d0d0d",
           "-fps_mode", "passthrough", "-frames:v", "1", "-update", "1", os.path.join(OUT, "contact-sheet.png"))
    length = duration_of(out)
    v = run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate",
             "-of", "csv=p=0", out]).stdout.strip()
    print(f"\nwrote {os.path.relpath(out, HERE)}: {length:.2f}s, {v}, {n_cues} captions -> out/captions.srt, out/contact-sheet.png")
    limit = edl.get("max_duration", 180.0)
    if length > limit:
        print("\n" + "!" * 72 + f"\n!!  TOO LONG: {length:.2f}s is over the {limit:.0f}s limit. Shorten takes or segments.\n" + "!" * 72)
        sys.exit(2)


if __name__ == "__main__":
    main()
