#!/usr/bin/env node
// Render the film's story scenes, frame by frame.
//
//   node render-scenes.mjs                 render every scene in scenes/*.html
//   node render-scenes.mjs 07-promise      render just the named scene(s)
//   node render-scenes.mjs --missing       render only scenes whose mp4 is missing or stale
//   node render-scenes.mjs --still 07-promise 3 12.5   write PNG stills (out/stills/) for review
//   node render-scenes.mjs --jobs 2        scenes rendered side by side (default 3)
//
// Each scene page exposes window.DURATION and window.seek(t). Every frame is
// seek(i/30) then a PNG screenshot piped straight into ffmpeg, so frames are
// exact and nothing is dropped. Output: out/scenes/<name>.mp4 (H.264, yuv420p,
// crf 16, 30 fps; the final cut re-encodes). card.html is a template for placeholder cards, not a scene.
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs';
import path from 'node:path';
import url from 'node:url';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const here = path.dirname(url.fileURLToPath(import.meta.url));
const FPS = 30;
const scenesDir = path.join(here, 'scenes');
const outDir = path.join(here, 'out', 'scenes');
const stillDir = path.join(here, 'out', 'stills');

const argv = process.argv.slice(2);
const flag = (name) => { const i = argv.indexOf(name); if (i >= 0) { argv.splice(i, 1); return true; } return false; };
const opt = (name, dflt) => { const i = argv.indexOf(name); if (i >= 0) { const v = argv[i + 1]; argv.splice(i, 2); return v; } return dflt; };
const missingOnly = flag('--missing');
const stillMode = flag('--still');
const jobs = Math.max(1, parseInt(opt('--jobs', '3'), 10));

const allScenes = fs.readdirSync(scenesDir)
  .filter((f) => f.endsWith('.html') && f !== 'card.html')
  .map((f) => f.replace(/\.html$/, ''))
  .sort();

function newest(paths) {
  return Math.max(...paths.map((p) => { try { return fs.statSync(p).mtimeMs; } catch { return 0; } }));
}
function stale(name) {
  const out = path.join(outDir, `${name}.mp4`);
  if (!fs.existsSync(out)) return true;
  const libFiles = fs.readdirSync(path.join(scenesDir, 'lib')).map((f) => path.join(scenesDir, 'lib', f));
  return newest([path.join(scenesDir, `${name}.html`), ...libFiles]) > fs.statSync(out).mtimeMs;
}

async function openScene(browser, name) {
  const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
  page.on('pageerror', (e) => console.error(`[${name}] page error: ${e.message}`));
  await page.goto(url.pathToFileURL(path.join(scenesDir, `${name}.html`)).href);
  await page.evaluate(async () => {
    await Promise.all([
      document.fonts.load('400 40px "Source Serif 4"'), document.fonts.load('600 40px "Source Serif 4"'),
      document.fonts.load('400 40px "Inter"'), document.fonts.load('600 40px "Inter"'), document.fonts.load('700 40px "Inter"'),
    ]);
    await document.fonts.ready;
  });
  const duration = await page.evaluate(() => window.DURATION);
  if (typeof duration !== 'number' || !(duration > 0)) throw new Error(`${name}: window.DURATION is not set`);
  return { page, duration };
}

async function renderScene(browser, name) {
  const started = Date.now();
  const { page, duration } = await openScene(browser, name);
  fs.mkdirSync(outDir, { recursive: true });
  const out = path.join(outDir, `${name}.mp4`);
  const tmp = out + '.part.mp4';
  const ff = spawn('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'image2pipe', '-framerate', String(FPS), '-c:v', 'png', '-i', '-',
    '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '16', '-pix_fmt', 'yuv420p', '-r', String(FPS), '-movflags', '+faststart', tmp],
    { stdio: ['pipe', 'inherit', 'inherit'] });
  const done = once(ff, 'close');
  const frames = Math.round(duration * FPS);
  // Lossless PNG straight from the DevTools protocol, with Chrome's faster
  // compression: about a third quicker than page.screenshot.
  const cdp = await page.context().newCDPSession(page);
  for (let i = 0; i < frames; i++) {
    await page.evaluate((t) => window.seek(t), i / FPS);
    const shot = await cdp.send('Page.captureScreenshot', { format: 'png', optimizeForSpeed: true });
    const buf = Buffer.from(shot.data, 'base64');
    if (!ff.stdin.write(buf)) await once(ff.stdin, 'drain');
  }
  ff.stdin.end();
  const [code] = await done;
  await page.close();
  if (code !== 0) throw new Error(`${name}: ffmpeg exited ${code}`);
  fs.renameSync(tmp, out);
  console.log(`rendered ${name}: ${frames} frames (${duration}s) in ${((Date.now() - started) / 1000).toFixed(1)}s -> ${path.relative(here, out)}`);
}

async function stills(browser, name, times) {
  const { page, duration } = await openScene(browser, name);
  fs.mkdirSync(stillDir, { recursive: true });
  for (const t of (times.length ? times : [0, duration / 3, (2 * duration) / 3, duration])) {
    await page.evaluate((x) => window.seek(x), t);
    const file = path.join(stillDir, `${name}-${String(t).replace('.', '_')}.png`);
    await page.screenshot({ path: file, type: 'png' });
    console.log(path.relative(here, file));
  }
  await page.close();
}

const browser = await chromium.launch();
try {
  if (stillMode) {
    const [name, ...times] = argv;
    if (!allScenes.includes(name)) throw new Error(`no scene named ${name}`);
    await stills(browser, name, times.map(Number));
  } else {
    let names = argv.length ? argv : allScenes;
    for (const n of names) if (!allScenes.includes(n)) throw new Error(`no scene named ${n} (have: ${allScenes.join(', ')})`);
    if (missingOnly) names = names.filter(stale);
    if (names.length === 0) console.log('all scenes up to date');
    const queue = [...names];
    const failures = [];
    await Promise.all(Array.from({ length: Math.min(jobs, queue.length) }, async () => {
      while (queue.length) {
        const n = queue.shift();
        try { await renderScene(browser, n); } catch (e) { failures.push(n); console.error(`FAILED ${n}: ${e.message}`); }
      }
    }));
    if (failures.length) { process.exitCode = 1; }
  }
} finally {
  await browser.close();
}
