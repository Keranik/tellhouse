// Time helpers shared by every scene. A scene calls Scene(duration, render)
// and the page then exposes window.DURATION and window.seek(t): seek renders
// the exact frame for time t (seconds), synchronously, from t alone.
(function () {
  const clamp = (x, a = 0, b = 1) => Math.min(b, Math.max(a, x));
  const lerp = (a, b, p) => a + (b - a) * p;
  const prog = (t, start, dur) => (dur <= 0 ? (t >= start ? 1 : 0) : clamp((t - start) / dur));
  const easeInOut = (p) => { p = clamp(p); return p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2; };
  const easeOut = (p) => 1 - Math.pow(1 - clamp(p), 3);
  const easeIn = (p) => Math.pow(clamp(p), 3);
  // Fade in over dur from start; optional fade out at outStart over outDur.
  const fade = (t, start, dur = 0.8, outStart = Infinity, outDur = 0.8) =>
    easeOut(prog(t, start, dur)) * (1 - easeIn(prog(t, outStart, outDur)));
  // Typewriter: how much of text shows at time t, typing from start at cps.
  const typed = (text, t, start, cps = 14) => text.slice(0, Math.max(0, Math.min(text.length, Math.floor((t - start) * cps))));
  const typingDone = (text, start, cps = 14) => start + text.length / cps;
  // A caret that blinks at 1 Hz while visible.
  const caretOn = (t) => Math.floor(t * 2) % 2 === 0;
  const $ = (sel) => document.querySelector(sel);
  const set = (el, styles) => { for (const k in styles) el.style[k] = styles[k]; };
  window.T = { clamp, lerp, prog, easeInOut, easeOut, easeIn, fade, typed, typingDone, caretOn, $, set };
  window.Scene = function (duration, render) {
    window.DURATION = duration;
    window.seek = function (t) { render(Math.max(0, Math.min(duration, t))); return true; };
    const q = new URLSearchParams(location.search);
    const start = () => window.seek(q.has('t') ? parseFloat(q.get('t')) : 0);
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
  };
})();
