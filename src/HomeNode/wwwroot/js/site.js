// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// Mobile navigation drawer (narrow screens only; the toggle
// button is not rendered above the shell breakpoint). The
// rail becomes a modal drawer in the Material 3 pattern:
// opened from the top bar's house button or a swipe that
// starts at the left screen edge; closed by the scrim, a
// left swipe, Escape, or following a destination. While it
// is open, focus lives inside the drawer; on close, focus
// returns to the button that opened it.
(function () {
    var toggle = document.querySelector("[data-drawer-toggle]");
    var drawer = document.getElementById("app-sidebar");
    var scrim = document.querySelector("[data-drawer-scrim]");
    if (!toggle || !drawer || !scrim) return;

    var closeTimer = null;

    function isOpen() {
        return document.body.classList.contains("drawer-open");
    }

    function focusables() {
        return drawer.querySelectorAll(
            "a[href], button:not([disabled])");
    }

    function openDrawer() {
        window.clearTimeout(closeTimer);
        scrim.hidden = false;
        // Let the scrim paint at opacity 0 before the class
        // lands, so its fade-in transition actually runs.
        void scrim.offsetWidth;
        document.body.classList.add("drawer-open");
        toggle.setAttribute("aria-expanded", "true");
        var items = focusables();
        if (items.length) items[0].focus();
    }

    function closeDrawer() {
        if (!isOpen()) return;
        document.body.classList.remove("drawer-open");
        toggle.setAttribute("aria-expanded", "false");
        var wasInside = drawer.contains(document.activeElement);
        closeTimer = window.setTimeout(function () {
            if (!isOpen()) scrim.hidden = true;
        }, 260);
        if (wasInside) toggle.focus();
    }

    toggle.addEventListener("click", function () {
        if (isOpen()) closeDrawer(); else openDrawer();
    });

    scrim.addEventListener("click", closeDrawer);

    drawer.addEventListener("click", function (event) {
        if (event.target.closest("a[href]")) closeDrawer();
    });

    document.addEventListener("keydown", function (event) {
        if (!isOpen()) return;
        if (event.key === "Escape") {
            closeDrawer();
            return;
        }
        if (event.key !== "Tab") return;
        // Keep Tab cycling inside the open drawer.
        var items = Array.prototype.slice.call(focusables());
        if (!items.length) return;
        var first = items[0];
        var last = items[items.length - 1];
        if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    });

    // Edge swipe: a touch that starts within 24px of the
    // left edge and travels 48px right opens the drawer;
    // with the drawer open, a 48px leftward travel closes
    // it. Vertical-dominant movement is left alone, so
    // scrolling the page never trips the gesture.
    var touchStart = null;

    document.addEventListener("touchstart", function (event) {
        if (event.touches.length !== 1) { touchStart = null; return; }
        var t = event.touches[0];
        touchStart = {
            x: t.clientX,
            y: t.clientY,
            fromEdge: t.clientX <= 24,
            wasOpen: isOpen()
        };
    }, { passive: true });

    document.addEventListener("touchmove", function (event) {
        if (!touchStart || event.touches.length !== 1) return;
        var t = event.touches[0];
        var dx = t.clientX - touchStart.x;
        var dy = t.clientY - touchStart.y;
        if (Math.abs(dy) > Math.abs(dx)) return;
        if (!touchStart.wasOpen && touchStart.fromEdge && dx >= 48) {
            openDrawer();
            touchStart = null;
        } else if (touchStart.wasOpen && dx <= -48) {
            closeDrawer();
            touchStart = null;
        }
    }, { passive: true });

    document.addEventListener("touchend", function () {
        touchStart = null;
    }, { passive: true });

    // If the window grows past the breakpoint with the
    // drawer open, hand navigation back to the rail.
    window.addEventListener("resize", function () {
        if (window.innerWidth > 768 && isOpen()) closeDrawer();
    });
})();

// Home's example chips: a tap puts the example in the message box,
// ready to send or edit. Nothing is sent until the person sends it.
(function () {
    var box = document.querySelector("[data-composer]");
    if (!box) return;
    document.querySelectorAll("[data-example]").forEach(function (chip) {
        chip.addEventListener("click", function () {
            box.value = chip.getAttribute("data-example");
            box.focus();
        });
    });
    // Enter sends; Shift+Enter keeps a new line.
    box.addEventListener("keydown", function (event) {
        if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
            event.preventDefault();
            if (box.value.trim().length > 0 && box.form) {
                if (box.form.requestSubmit) box.form.requestSubmit(); else box.form.submit();
            }
        }
    });
})();

// Busy forms: while a message is being read (a live model call takes
// a few seconds), the button that sent it says so, and a second
// submit is ignored. The submitter is never disabled, so its own
// name and value still travel with the form.
(function () {
    document.querySelectorAll("form[data-busy]").forEach(function (form) {
        form.addEventListener("submit", function (event) {
            if (form.getAttribute("data-busy") === "on") {
                event.preventDefault();
                return;
            }
            form.setAttribute("data-busy", "on");
            var button = event.submitter || form.querySelector("button[type=submit]");
            if (button) {
                button.setAttribute("aria-disabled", "true");
                button.classList.add("is-busy");
                button.textContent = form.getAttribute("data-busy-label") || "Working…";
            }
        });
    });
    // A page restored from the back/forward cache comes back usable.
    window.addEventListener("pageshow", function () {
        document.querySelectorAll("form[data-busy='on']").forEach(function (form) {
            form.setAttribute("data-busy", "");
        });
    });
})();

// Home's Private switch. A private message is read by the house's own
// rules and by no model; the server decides that on its own (anything
// health-shaped is kept private there whatever this page says), so
// this only shows the person, as they type, what will happen. While
// Private is on, the mic is off: voice goes through Muse Voice
// Transcribe on the Meta Model API, and a private message goes to no
// model at all.
(function () {
    var form = document.querySelector("form[data-composer-form]");
    if (!form) return;
    var box = form.querySelector("[data-composer]");
    var priv = form.querySelector("[data-private]");
    var mic = form.querySelector("[data-mic]");
    var note = form.querySelector("[data-tool-note]");
    var autoOn = false;
    // A short mirror of the house's health-word rule, for the hint only.
    var healthy = /\b(hurts?|hurting|pain|painful|sore|ache|aching|swollen|swelling|sprain(ed)?|dizzy|fever|nause(a|ous)|bleeding|bruised?|headache|migraine|rash|wound|injur(y|ed)|fell|fallen|faint(ed)?|vomit(ing|ed)?|cough(ing)?|chest|breath|seizure|stroke|blood pressure|blood sugar|diabet(es|ic)|insulin|sick|unwell|pills?|tablets?|medications?|medicines?|meds|prescriptions?|dose|inhaler|pharmacy|doctor'?s?|nurse|hospital|clinic|surgery|therapy|therapist|diagnos(is|ed)|depressed|depression|anxious|anxiety|panic|lonely|private|secret|don'?t tell)\b/i;
    function sync() {
        var on = priv && priv.checked;
        form.classList.toggle("is-private", !!on);
        if (mic) {
            mic.hidden = !!on;
        }
        if (note) {
            note.textContent = on
                ? "Private: no model will read this. The house's own rules write it down."
                : (mic ? "Speak or type." : "");
        }
        form.setAttribute("data-busy-label", on ? (form.getAttribute("data-private-label") || "Reading privately…") : (form.getAttribute("data-busy-label-base") || form.getAttribute("data-busy-label")));
    }
    form.setAttribute("data-busy-label-base", form.getAttribute("data-busy-label") || "Reading…");
    if (priv) {
        priv.addEventListener("change", function () { autoOn = false; sync(); });
    }
    if (box && priv) {
        box.addEventListener("input", function () {
            var looks = healthy.test(box.value);
            if (looks && !priv.checked) {
                priv.checked = true;
                autoOn = true;
                sync();
                if (note) note.textContent = "This sounds like health, so it stays private: no model will read it.";
            } else if (!looks && autoOn) {
                priv.checked = false;
                autoOn = false;
                sync();
            }
        });
    }
    // Draft and voice: whether the person changed the words before sending.
    form.addEventListener("submit", function () {
        var heard = form.querySelector("[data-heard-edited]");
        if (heard && box) heard.value = String((form.getAttribute("data-heard-text") || "") !== "" && box.value.trim() !== form.getAttribute("data-heard-text"));
        var draft = form.querySelector("[data-draft-edited]");
        if (draft && box) draft.value = String(box.value.trim() !== (draft.getAttribute("data-draft-text") || "").trim());
    });
    sync();
})();

// Push to talk: Muse Voice Transcribe. The browser records a short
// clip as 16 kHz mono 16-bit WAV, the house sends it to the Meta Model
// API, and the words come back into the message box for the person to
// read, change and send. Nothing is sent to the record until they do.
(function () {
    var form = document.querySelector("form[data-composer-form]");
    var mic = form && form.querySelector("[data-mic]");
    if (!mic) return;
    var label = mic.querySelector("[data-mic-label]");
    var box = form.querySelector("[data-composer]");
    var note = form.querySelector("[data-tool-note]");
    var priv = form.querySelector("[data-private]");
    var heardField = form.querySelector("[data-heard-call]");
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia || !(window.AudioContext || window.webkitAudioContext)) {
        mic.hidden = true;
        return;
    }
    var state = "idle", stream = null, ctx = null, source = null, proc = null, chunks = [], rate = 48000, timer = null;
    function say(text) { if (note) note.textContent = text; }
    function setLabel(text) { if (label) label.textContent = text; }

    function start() {
        navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } }).then(function (s) {
            stream = s;
            var AC = window.AudioContext || window.webkitAudioContext;
            ctx = new AC();
            rate = ctx.sampleRate;
            source = ctx.createMediaStreamSource(stream);
            proc = ctx.createScriptProcessor(4096, 1, 1);
            chunks = [];
            proc.onaudioprocess = function (e) { chunks.push(new Float32Array(e.inputBuffer.getChannelData(0))); };
            source.connect(proc);
            proc.connect(ctx.destination);
            state = "recording";
            mic.classList.add("is-recording");
            mic.setAttribute("aria-pressed", "true");
            setLabel("Listening… tap to stop");
            say("Recording. Speak in your own words.");
            timer = setTimeout(stop, 60000);
        }).catch(function () {
            say("The microphone is not available here. Type instead.");
        });
    }

    function stop() {
        if (state !== "recording") return;
        clearTimeout(timer);
        state = "sending";
        mic.classList.remove("is-recording");
        mic.setAttribute("aria-pressed", "false");
        setLabel("Muse Voice Transcribe is listening back…");
        try { source.disconnect(); proc.disconnect(); } catch (e) { }
        stream.getTracks().forEach(function (t) { t.stop(); });
        var wav = encode(chunks, rate);
        try { ctx.close(); } catch (e) { }
        var data = new FormData();
        data.append("audio", wav, "clip.wav");
        var speaker = form.querySelector("input[name=speakerId]:checked");
        if (speaker && speaker.value) data.append("speakerId", speaker.value);
        var token = form.querySelector("input[name=__RequestVerificationToken]");
        if (token) data.append("__RequestVerificationToken", token.value);
        fetch("?handler=Transcribe", { method: "POST", body: data, credentials: "same-origin" })
            .then(function (r) { return r.json(); })
            .then(function (result) {
                state = "idle";
                setLabel("Tap to talk");
                if (!result || !result.ok) {
                    say((result && result.note) || "Nothing was heard. Type it instead, or try again.");
                    return;
                }
                box.value = (box.value.trim() ? box.value.trim() + " " : "") + result.transcript;
                form.setAttribute("data-heard-text", box.value.trim());
                if (heardField) heardField.value = result.callId;
                if (result.sensitive && priv && !priv.checked) {
                    priv.checked = true;
                    priv.dispatchEvent(new Event("change"));
                    say("Heard by Muse Voice Transcribe. It sounds like health, so no model will read the message itself.");
                } else {
                    say("Heard by Muse Voice Transcribe in " + (result.ms >= 1000 ? (result.ms / 1000).toFixed(1) + " s" : result.ms + " ms") + ". Check the words, then send.");
                }
                box.focus();
            })
            .catch(function () {
                state = "idle";
                setLabel("Tap to talk");
                say("Muse Voice Transcribe did not answer. Type it instead, or try again.");
            });
    }

    // Float samples at the device rate, to 16 kHz mono 16-bit WAV.
    function encode(parts, fromRate) {
        var length = 0;
        parts.forEach(function (p) { length += p.length; });
        var all = new Float32Array(length), offset = 0;
        parts.forEach(function (p) { all.set(p, offset); offset += p.length; });
        var toRate = 16000, ratio = fromRate / toRate;
        var outLength = Math.floor(all.length / ratio);
        var pcm = new Int16Array(outLength);
        for (var i = 0; i < outLength; i++) {
            var startIdx = Math.floor(i * ratio), endIdx = Math.min(all.length, Math.floor((i + 1) * ratio));
            var sum = 0, count = 0;
            for (var j = startIdx; j < endIdx; j++) { sum += all[j]; count++; }
            var v = count ? sum / count : 0;
            v = Math.max(-1, Math.min(1, v));
            pcm[i] = v < 0 ? v * 0x8000 : v * 0x7fff;
        }
        var buffer = new ArrayBuffer(44 + pcm.length * 2);
        var view = new DataView(buffer);
        function str(o, s) { for (var k = 0; k < s.length; k++) view.setUint8(o + k, s.charCodeAt(k)); }
        str(0, "RIFF"); view.setUint32(4, 36 + pcm.length * 2, true); str(8, "WAVE");
        str(12, "fmt "); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
        view.setUint32(24, toRate, true); view.setUint32(28, toRate * 2, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
        str(36, "data"); view.setUint32(40, pcm.length * 2, true);
        for (var n = 0; n < pcm.length; n++) view.setInt16(44 + n * 2, pcm[n], true);
        return new Blob([view], { type: "audio/wav" });
    }

    setLabel("Tap to talk");
    mic.addEventListener("click", function () {
        if (state === "idle") start();
        else if (state === "recording") stop();
    });
})();
