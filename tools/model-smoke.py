#!/usr/bin/env python3
"""Call each Meta model Tellhouse uses, once, with your key, and say plainly
whether it answered in the shape the house expects.

Standard library only. The key is read from MODEL_API_KEY and never printed.

    MODEL_API_KEY=... python3 tools/model-smoke.py                 # Muse Spark and Muse Wonder
    MODEL_API_KEY=... python3 tools/model-smoke.py --voice clip.wav
    MODEL_API_KEY=... python3 tools/model-smoke.py --sam shelf.jpg --prompt "package of wet wipes"
    MODEL_API_KEY=... python3 tools/model-smoke.py --image

The WAV must be mono 16-bit at 16 or 24 kHz (what Home's mic records).
Each line ends PASS or FAIL with the HTTP status, the time it took, and a
short excerpt of the answer. Exit status is non-zero if anything failed.
"""
import argparse
import base64
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
import uuid

BASE = "https://api.meta.ai/v1"
RESULTS = []


def key():
    k = os.environ.get("MODEL_API_KEY", "").strip()
    if not k:
        sys.exit("MODEL_API_KEY is not set in this shell.")
    return k


def call(path, body, content_type="application/json", accept=None, timeout=180):
    data = body if isinstance(body, bytes) else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data=data, method="POST")
    req.add_header("Authorization", "Bearer " + key())
    req.add_header("Content-Type", content_type)
    if accept:
        req.add_header("Accept", accept)
    started = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read().decode("utf-8", "replace"), time.time() - started
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace"), time.time() - started
    except Exception as e:  # network, timeout
        return 0, repr(e), time.time() - started


def report(name, ok, status, seconds, excerpt):
    RESULTS.append(ok)
    excerpt = " ".join(str(excerpt).split())[:220]
    print(f"{'PASS' if ok else 'FAIL'}  {name:<24} HTTP {status}  {seconds:5.1f}s  {excerpt}")


def chat_json(name, model, schema, prompt, extra=None):
    body = {
        "model": model,
        "messages": [{"role": "user", "content": prompt}],
        "response_format": {"type": "json_schema", "json_schema": {"name": "smoke", "strict": True, "schema": schema}},
        "max_tokens": 4096,
    }
    body.update(extra or {})
    status, text, secs = call("/chat/completions", body)
    ok, excerpt = False, text
    if status == 200:
        try:
            doc = json.loads(text)
            content = doc["choices"][0]["message"]["content"]
            json.loads(content)
            ok = True
            excerpt = f"model={doc.get('model')} usage={doc.get('usage')} content={content}"
        except Exception as e:
            excerpt = f"answered but not in the expected shape: {e}: {text}"
    report(name, ok, status, secs, excerpt)


def spark():
    schema = {"type": "object", "properties": {"kind": {"type": "string"}, "who": {"type": "string"}},
              "required": ["kind", "who"], "additionalProperties": False}
    chat_json("Muse Spark (read)", "muse-spark-1.3", schema,
              "Classify: 'Ask Daniel to pick up milk on his way home'. kind is one of Request, Claim, Note; who is the person named.",
              {"reasoning_effort": "low", "temperature": 0})


def wonder():
    schema = {"type": "object", "properties": {"summary": {"type": "string"},
              "findings": {"type": "array", "items": {"type": "object",
                  "properties": {"headline": {"type": "string"}, "entry_ids": {"type": "array", "items": {"type": "integer"}}},
                  "required": ["headline", "entry_ids"], "additionalProperties": False}}},
              "required": ["summary", "findings"], "additionalProperties": False}
    record = {"entries": [{"id": 1, "by": "Lisa", "words": "I'll get Mia at 3"}, {"id": 2, "by": "Daniel", "words": "I'll pick Mia up at three"}]}
    chat_json("Muse Wonder (review)", "muse-wonder-1.0", schema,
              "Review this household record and report conflicts, citing entry ids: " + json.dumps(record),
              {"reasoning_effort": "medium"})


def multipart(fields, files):
    boundary = "----tellhouse" + uuid.uuid4().hex
    out = b""
    for name, (value, ctype) in fields.items():
        out += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n"
                f"Content-Type: {ctype}\r\n\r\n").encode() + value + b"\r\n"
    for name, (filename, value, ctype) in files.items():
        out += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{filename}\"\r\n"
                f"Content-Type: {ctype}\r\n\r\n").encode() + value + b"\r\n"
    out += f"--{boundary}--\r\n".encode()
    return out, "multipart/form-data; boundary=" + boundary


def voice(path):
    audio = open(path, "rb").read()
    request = {"model": "muse-voice-transcribe-1.0", "audioEncoding": "WAV", "mode": "PUSH_TO_TALK",
               "keywords": ["Margaret", "Daniel", "Lisa", "Timmy", "Mia", "June", "gauze", "wipes"]}
    body, ctype = multipart({"request": (json.dumps(request).encode(), "application/json")},
                            {"audio": ("clip.wav", audio, "audio/wav")})
    status, text, secs = call("/asr/transcribe", body, ctype)
    ok, excerpt = False, text
    if status == 200:
        try:
            doc = json.loads(text)
            ok = isinstance(doc.get("transcript"), str) or isinstance(doc.get("turns"), list)
            excerpt = f"transcript={doc.get('transcript')!r} audioDurationMs={doc.get('audioDurationMs')}"
        except Exception as e:
            excerpt = f"answered but not JSON: {e}: {text}"
    report("Muse Voice Transcribe", ok, status, secs, excerpt)


def responses_text(text):
    """The output_text of a Responses API answer, streamed (SSE) or not."""
    if text.lstrip().startswith("{"):
        doc = json.loads(text)
        parts = []
        for item in doc.get("output", []):
            for c in item.get("content", []) or []:
                if c.get("type") == "output_text":
                    parts.append(c.get("text", ""))
        return "".join(parts), doc
    parts = []
    for line in text.splitlines():
        if line.startswith("data:"):
            payload = line[5:].strip()
            if payload in ("", "[DONE]"):
                continue
            try:
                event = json.loads(payload)
            except json.JSONDecodeError:
                continue
            if event.get("type") == "response.output_text.delta":
                parts.append(event.get("delta", ""))
    return "".join(parts), None


def sam(path, prompt):
    ext = os.path.splitext(path)[1].lower().lstrip(".") or "jpeg"
    mime = "image/png" if ext == "png" else "image/webp" if ext == "webp" else "image/jpeg"
    data_url = f"data:{mime};base64," + base64.b64encode(open(path, "rb").read()).decode()
    body = {"model": "sam-3.1", "stream": True,
            "input": [{"role": "user", "content": [{"type": "input_text", "text": prompt},
                                                   {"type": "input_image", "image_url": data_url}]}],
            "metadata": {"include_confidence": "true", "score_threshold": "0.5"}}
    status, text, secs = call("/responses", body, accept="text/event-stream")
    ok, excerpt = False, text
    if status == 200:
        out, _ = responses_text(text)
        boxes = re.findall(r"<\|box;([^|]*)\|>", out)
        ok = True  # zero masks is a valid empty answer
        excerpt = f"{len(boxes)} '{prompt}' found; first line: {out.splitlines()[0] if out else '(empty)'}"
    report("SAM 3.1 (see)", ok, status, secs, excerpt)


def image():
    body = {"model": "muse-image-1.0",
            "input": "A flat, friendly picture card for a child: a dinner plate and a big clock showing 6:30. No people, no words."}
    status, text, secs = call("/responses", body)
    ok, excerpt = False, text
    if status == 200:
        try:
            doc = json.loads(text)
            calls = [i for i in doc.get("output", []) if i.get("type") == "image_generation_call"]
            result = calls[0].get("result") if calls else None
            ok = bool(result)
            excerpt = f"status={doc.get('status')} image bytes(base64)={len(result) if result else 0}"
        except Exception as e:
            excerpt = f"answered but not in the expected shape: {e}: {text}"
    report("Muse Image (draw)", ok, status, secs, excerpt)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--voice", metavar="WAV", help="a mono 16-bit WAV at 16 or 24 kHz")
    p.add_argument("--sam", metavar="PHOTO", help="a photo of a supply shelf")
    p.add_argument("--prompt", default="package of wet wipes", help="the noun phrase SAM looks for")
    p.add_argument("--image", action="store_true", help="draw one picture card")
    p.add_argument("--skip-text", action="store_true", help="skip Muse Spark and Muse Wonder")
    a = p.parse_args()
    key()
    if not a.skip_text:
        spark()
        wonder()
    if a.voice:
        voice(a.voice)
    if a.sam:
        sam(a.sam, a.prompt)
    if a.image:
        image()
    failed = RESULTS.count(False)
    print(f"\n{len(RESULTS) - failed} of {len(RESULTS)} answered as expected.")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
