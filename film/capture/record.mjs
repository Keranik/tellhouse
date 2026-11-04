#!/usr/bin/env node
// Record the live Tellhouse app for the film: one clip per shot, 1920x1080.
//
//   BASE_URL=https://tellhouse.onrender.com node record.mjs        every shot
//   BASE_URL=http://localhost:5042 node record.mjs --only 05,06b    some shots (id prefixes)
//   node record.mjs --list                                         what each shot does
//   SHELF_PHOTO=/path/to/shelf.jpg ...                              enables the SAM shelf shot
//   --headed                                                       watch it drive the browser
//
// Clips land in ../footage/<shot>.mp4, where assemble.py picks them up.
// Frames come from Chrome's screencast with their real timing, so a clip plays
// at the speed it happened; long model waits (Muse Wonder, SAM, a picture card
// being drawn) are cut out with a jump cut. A visible cursor dot moves to each
// thing that is clicked, and typing is human-paced. Each shot starts from a
// fresh browser context and is independent: one failing shot does not stop the
// others. Needs Node 18+, `npm install`, and `npx playwright install chromium`;
// ffmpeg on PATH (or the optional ffmpeg-static package) for crisp clips,
// otherwise Playwright's own video recorder is used and clips are .webm.
import { createRequire } from 'node:module';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import url from 'node:url';

const require = createRequire(import.meta.url);
const here = path.dirname(url.fileURLToPath(import.meta.url));
const FOOTAGE = path.resolve(here, '..', 'footage');
const BASE = (process.env.BASE_URL || '').replace(/\/+$/, '');
const PEOPLE = { Margaret: 1, Daniel: 2, Lisa: 3, Timmy: 4, Mia: 5, June: 6 };
const argv = process.argv.slice(2);
const has = (f) => argv.includes(f);
const only = (() => { const i = argv.indexOf('--only'); return i >= 0 ? argv[i + 1].split(',').map((s) => s.trim()).filter(Boolean) : null; })();

// Every clip is 1920x1080, but the app is laid out at a smaller viewport and
// scaled up, so its words stay readable in the film: x4/3 for full-frame shots,
// x1.5 for the shots that play small, picture-in-picture, in section 08.
const ZOOM = {
  full: { viewport: { width: 1440, height: 810 }, deviceScaleFactor: 4 / 3 },
  pip: { viewport: { width: 1280, height: 720 }, deviceScaleFactor: 1.5 },
};
const SHOTS = [
  { id: '05-margaret-front-door', what: 'As Margaret on her front door: tap the "milk" card, wait for the house to ask, say yes.', run: shotFrontDoor },
  { id: '05b-daniel-inbox', what: "As Daniel: open the Inbox, show Margaret's message, mark it seen.", run: shotDanielInbox },
  { id: '06b-private-trace', what: 'As Margaret: type "My ankle has swollen up again.", send, say yes to telling Daniel, open every step.', run: shotPrivate },
  { id: '07b-claims', what: 'As Daniel: say "I\'ll get Mia at 3", then open Claims and scroll.', run: shotClaims },
  { id: '08a-wonder', zoom: 'pip', what: "As Daniel on Briefing: ask Muse Wonder what needs attention; hold on its findings.", run: shotWonder },
  { id: '08b-shelf', zoom: 'pip', what: 'As Lisa on Briefing: check the shelf photo (SHELF_PHOTO) with SAM 3.1; hold on the outlines.', run: shotShelf },
  { id: '08c-picture-card', zoom: 'pip', what: 'As Margaret: "Ask Timmy to come down for dinner at 6:30", yes; then Timmy\'s Inbox picture card.', run: shotPictureCard },
  { id: '08d-models-page', zoom: 'pip', what: 'The models page, scrolled slowly.', run: shotModels },
];

if (has('--list')) {
  for (const s of SHOTS) console.log(`${s.id.padEnd(24)} ${s.what}`);
  console.log(`\nClips go to ${path.relative(process.cwd(), FOOTAGE) || FOOTAGE}/<shot>.mp4`);
  process.exit(0);
}
if (!BASE) {
  console.error('Set BASE_URL, e.g. BASE_URL=https://tellhouse.onrender.com node record.mjs  (or --list)');
  process.exit(1);
}

// ---------------------------------------------------------------- ffmpeg
function findFfmpeg() {
  const candidates = [process.env.FFMPEG, 'ffmpeg'];
  try { candidates.push(require('ffmpeg-static')); } catch { /* optional */ }
  for (const c of candidates.filter(Boolean)) {
    if (spawnSync(c, ['-version'], { stdio: 'ignore' }).status === 0) return c;
  }
  return null;
}
const FFMPEG = findFfmpeg();

// ---------------------------------------------------------------- recorder
// Chrome screencast frames, written to a temp dir with the time each arrived.
class Recorder {
  constructor(page, id) { this.page = page; this.id = id; this.frames = []; this.paused = false; this.cut = false; }
  async start() {
    if (!FFMPEG) return; // Playwright's recordVideo is running instead.
    this.dir = fs.mkdtempSync(path.join(os.tmpdir(), `th-${this.id}-`));
    this.client = await this.page.context().newCDPSession(this.page);
    this.client.on('Page.screencastFrame', async (f) => {
      const now = performance.now() / 1000;
      if (!this.paused) {
        const file = path.join(this.dir, `${String(this.frames.length).padStart(6, '0')}.jpg`);
        fs.writeFileSync(file, Buffer.from(f.data, 'base64'));
        this.frames.push({ file, t: now, cut: this.cut });
        this.cut = false;
      }
      try { await this.client.send('Page.screencastFrameAck', { sessionId: f.sessionId }); } catch { /* page gone */ }
    });
    await this.client.send('Page.startScreencast', { format: 'jpeg', quality: 92, maxWidth: 1920, maxHeight: 1080, everyNthFrame: 1 });
    await nudge(this.page);
  }
  pause() { this.paused = true; }
  async resume() { this.paused = false; this.cut = true; await nudge(this.page); }
  async stop() {
    if (!this.client) return;
    await nudge(this.page);
    await this.page.waitForTimeout(150);
    this.end = performance.now() / 1000;
    try { await this.client.send('Page.stopScreencast'); } catch { /* ignore */ }
  }
  save(out) {
    if (!FFMPEG) return false;
    if (this.frames.length === 0) { console.log(`   (no frames captured for ${this.id})`); return false; }
    const lines = ['ffconcat version 1.0'];
    this.frames.forEach((fr, i) => {
      const next = this.frames[i + 1];
      let d = (next ? next.t : this.end) - fr.t;
      if (next && next.cut) d = Math.min(d, 0.25); // jump cut over a paused wait
      lines.push(`file '${fr.file.replace(/'/g, "'\\''")}'`, `duration ${Math.max(1 / 30, d).toFixed(4)}`);
    });
    lines.push(`file '${this.frames[this.frames.length - 1].file.replace(/'/g, "'\\''")}'`);
    const list = path.join(this.dir, 'list.ffconcat');
    fs.writeFileSync(list, lines.join('\n') + '\n');
    const r = spawnSync(FFMPEG, ['-y', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', list,
      '-vf', 'fps=30,scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=0x191919,format=yuv420p',
      '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18', '-movflags', '+faststart', out], { stdio: 'inherit' });
    fs.rmSync(this.dir, { recursive: true, force: true });
    return r.status === 0;
  }
}

// A repaint so the screencast has a fresh frame at this moment.
async function nudge(page) {
  try { await page.evaluate(() => { const c = document.getElementById('__th_cursor'); if (c) c.style.outlineColor = c.style.outlineColor ? '' : 'transparent'; }); } catch { /* navigating */ }
}

// ---------------------------------------------------------------- the cursor and pacing
const CURSOR = () => {
  const install = () => {
    if (document.getElementById('__th_cursor')) return;
    const c = document.createElement('div');
    c.id = '__th_cursor';
    c.style.cssText = 'position:fixed;left:0;top:0;width:28px;height:28px;margin:-14px 0 0 -14px;border-radius:50%;'
      + 'background:rgba(242,132,111,0.32);border:2.5px solid rgba(255,255,255,0.95);box-shadow:0 2px 12px rgba(0,0,0,0.5);'
      + 'z-index:2147483647;pointer-events:none;outline:0 solid transparent;';
    let pos = { x: Math.round(innerWidth * 0.78), y: Math.round(innerHeight * 0.66) };
    try { pos = JSON.parse(sessionStorage.getItem('__th_cursor')) || pos; } catch { /* first page */ }
    const place = (x, y, s = 1) => { c.style.transform = `translate(${x}px, ${y}px) scale(${s})`; };
    place(pos.x, pos.y);
    document.documentElement.appendChild(c);
    window.__thMove = (x, y, ms = 650) => new Promise((resolve) => {
      const from = { ...pos }, t0 = performance.now();
      const step = (now) => {
        const p = Math.min(1, (now - t0) / ms), e = p < 0.5 ? 2 * p * p : 1 - Math.pow(-2 * p + 2, 2) / 2;
        pos = { x: from.x + (x - from.x) * e, y: from.y + (y - from.y) * e };
        place(pos.x, pos.y);
        try { sessionStorage.setItem('__th_cursor', JSON.stringify(pos)); } catch { /* ignore */ }
        if (p < 1) requestAnimationFrame(step); else resolve();
      };
      requestAnimationFrame(step);
    });
    window.__thPress = () => new Promise((resolve) => {
      const t0 = performance.now();
      const step = (now) => {
        const p = Math.min(1, (now - t0) / 260);
        place(pos.x, pos.y, 1 - 0.28 * Math.sin(Math.PI * p));
        c.style.background = `rgba(242,132,111,${0.32 + 0.4 * Math.sin(Math.PI * p)})`;
        if (p < 1) requestAnimationFrame(step); else resolve();
      };
      requestAnimationFrame(step);
    });
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install); else install();
};

const pause = (page, ms) => page.waitForTimeout(ms);

async function pointAt(page, locator) {
  await locator.scrollIntoViewIfNeeded();
  const box = await locator.boundingBox();
  if (!box) throw new Error('target is not visible');
  await page.evaluate(([x, y]) => window.__thMove && window.__thMove(x, y), [box.x + box.width / 2, box.y + Math.min(box.height / 2, 40)]);
}

async function press(page, locator) {
  await pointAt(page, locator);
  await page.evaluate(() => window.__thPress && window.__thPress());
  await locator.click();
}

async function typeInto(page, locator, text) {
  await press(page, locator);
  await locator.pressSequentially(text, { delay: 60 });
}

async function smoothScroll(page, dy, ms) {
  await page.evaluate(([dy, ms]) => new Promise((resolve) => {
    const y0 = window.scrollY, t0 = performance.now();
    const step = (now) => {
      const p = Math.min(1, (now - t0) / ms), e = p < 0.5 ? 2 * p * p : 1 - Math.pow(-2 * p + 2, 2) / 2;
      window.scrollTo(0, y0 + dy * e);
      if (p < 1) requestAnimationFrame(step); else resolve();
    };
    requestAnimationFrame(step);
  }), [dy, ms]);
}

async function as(page, person, where) {
  await page.goto(`${BASE}/as/${PEOPLE[person]}?return=${encodeURIComponent(where)}`, { waitUntil: 'networkidle', timeout: 180000 });
}

const composer = (page) => page.locator('[data-composer]').first();
const sendButton = (page) => page.locator('form[data-composer-form] [data-send], form[data-composer-form] button[type=submit]').last();

async function sendAndWaitForReply(page, words) {
  await typeInto(page, composer(page), words);
  await pause(page, 500);
  await press(page, sendButton(page));
  await page.waitForSelector('[data-house-reply]', { timeout: 90000 });
  await page.waitForLoadState('networkidle');
}

async function confirm(page) {
  const yes = page.locator('[data-confirm]').first();
  if (await yes.count() === 0) { console.log('   (no [data-confirm] on the page; nothing to say yes to)'); return; }
  await press(page, yes);
  await page.waitForLoadState('networkidle');
}

// ---------------------------------------------------------------- shots
async function shotFrontDoor(page, rec) {
  await as(page, 'Margaret', '/');
  await rec.start();
  await pause(page, 1400);
  const card = page.locator('[data-tap-card]').filter({ hasText: /milk/i }).first();
  await press(page, card);
  await page.waitForSelector('[data-house-reply]', { timeout: 90000 });
  await page.waitForLoadState('networkidle');
  await pause(page, 2800);
  await confirm(page);
  await pause(page, 3200);
}

async function shotDanielInbox(page, rec) {
  await as(page, 'Daniel', '/Member');
  await rec.start();
  await pause(page, 1500);
  const item = page.locator('article.inbox-item--unseen, article.inbox-item').first();
  if (await item.count()) {
    await pointAt(page, item);
    await pause(page, 2200);
    const seen = item.getByRole('button', { name: /mark as seen/i });
    if (await seen.count()) { await press(page, seen); await page.waitForLoadState('networkidle'); }
  } else {
    console.log('   (no inbox item found)');
  }
  await pause(page, 2400);
}

async function shotPrivate(page, rec) {
  await as(page, 'Margaret', '/');
  await rec.start();
  await pause(page, 1000);
  await sendAndWaitForReply(page, 'My ankle has swollen up again.');
  await pause(page, 2800);
  await confirm(page);
  await pause(page, 1600);
  const more = page.locator('details.trace-more > summary').first();
  if (await more.count()) {
    await press(page, more);
    await pause(page, 900);
    await smoothScroll(page, 520, 2600);
  }
  await pause(page, 2400);
}

async function shotClaims(page, rec) {
  await as(page, 'Daniel', '/');
  await rec.start();
  await pause(page, 900);
  await sendAndWaitForReply(page, "I'll get Mia at 3");
  await pause(page, 2400);
  const link = page.locator('a[href="/Promises"]').first();
  if (await link.count() && await link.isVisible()) await press(page, link); else await page.goto(`${BASE}/Promises`, { waitUntil: 'networkidle' });
  await page.waitForLoadState('networkidle');
  await pause(page, 1600);
  await smoothScroll(page, 560, 4200);
  await pause(page, 1800);
}

async function shotWonder(page, rec) {
  await as(page, 'Daniel', '/Brief');
  await rec.start();
  await pause(page, 1200);
  const ask = page.locator('[data-wonder-ask]').first();
  if (await ask.count()) {
    await press(page, ask);
    await pause(page, 1300);
    rec.pause();
    await page.waitForSelector('.attention-col--wonder .finding, .attention-col--wonder .finding-summary, .attention-col--wonder .note', { timeout: 120000 });
    await page.waitForLoadState('networkidle');
    await rec.resume();
  } else {
    console.log('   ([data-wonder-ask] not found; showing whatever review is on the page)');
  }
  const col = page.locator('.attention-col--wonder').first();
  if (await col.count()) { await pointAt(page, col); }
  await pause(page, 1500);
  await smoothScroll(page, 260, 2400);
  await pause(page, 3500);
}

async function shotShelf(page, rec) {
  const photo = process.env.SHELF_PHOTO;
  if (!photo || !fs.existsSync(photo)) { console.log('   skipped: set SHELF_PHOTO to a photo of the supply shelf'); return 'skipped'; }
  await as(page, 'Lisa', '/Brief');
  const shelf = page.locator('#shelf');
  if (await shelf.count()) await shelf.scrollIntoViewIfNeeded();
  await rec.start();
  await pause(page, 1200);
  await page.setInputFiles('[data-shelf-input]', photo);
  const go = page.locator('form[data-shelf-form] button[type=submit]').first();
  await pause(page, 600);
  await press(page, go);
  await pause(page, 1200);
  rec.pause();
  await page.waitForSelector('figure.shelf-figure', { timeout: 120000 });
  await page.waitForLoadState('networkidle');
  await page.locator('figure.shelf-figure').scrollIntoViewIfNeeded();
  await rec.resume();
  await pause(page, 1200);
  await pointAt(page, page.locator('.shelf-counts').first());
  await pause(page, 4000);
}

async function shotPictureCard(page, rec) {
  await as(page, 'Margaret', '/');
  await rec.start();
  await pause(page, 900);
  await sendAndWaitForReply(page, 'Ask Timmy to come down for dinner at 6:30');
  await pause(page, 2300);
  await confirm(page);
  await pause(page, 1600);
  rec.pause();
  await as(page, 'Timmy', '/Member');
  for (let i = 0; i < 12 && (await page.locator('.picture-card img').count()) === 0; i++) {
    await page.waitForTimeout(5000);
    await page.reload({ waitUntil: 'networkidle' });
  }
  const img = page.locator('.picture-card img').first();
  if (await img.count()) {
    await img.scrollIntoViewIfNeeded();
    await page.waitForFunction((el) => el.complete && el.naturalWidth > 0, await img.elementHandle(), { timeout: 30000 }).catch(() => {});
  } else {
    console.log('   (no picture card appeared within a minute)');
  }
  await rec.resume();
  await pause(page, 1400);
  if (await img.count()) await pointAt(page, img);
  await pause(page, 4000);
}

async function shotModels(page, rec) {
  await page.goto(`${BASE}/Model`, { waitUntil: 'networkidle', timeout: 180000 });
  await rec.start();
  await pause(page, 1500);
  await smoothScroll(page, 980, 7000);
  await pause(page, 1500);
}

// ---------------------------------------------------------------- main
const { chromium } = require('playwright');
fs.mkdirSync(FOOTAGE, { recursive: true });
console.log(`Recording ${BASE} -> ${FOOTAGE}${FFMPEG ? '' : '  (no ffmpeg found: using Playwright video, clips are .webm)'}`);
const browser = await chromium.launch({ headless: !has('--headed') });
const results = [];
try {
  process.stdout.write('Waking the site (a sleeping free instance can take a minute)… ');
  const warm = await browser.newPage();
  await warm.goto(BASE + '/', { waitUntil: 'domcontentloaded', timeout: 240000 });
  await warm.close();
  console.log('up.');
  for (const shot of SHOTS) {
    if (only && !only.some((o) => shot.id.startsWith(o))) continue;
    console.log(`\n▶ ${shot.id}: ${shot.what}`);
    const tmpVideo = fs.mkdtempSync(path.join(os.tmpdir(), 'th-pwvideo-'));
    const zoom = ZOOM[shot.zoom || 'full'];
    const context = await browser.newContext({
      ...zoom, locale: 'en-US', timezoneId: 'America/Chicago',
      ...(FFMPEG ? {} : { recordVideo: { dir: tmpVideo, size: { width: 1920, height: 1080 } } }),
    });
    await context.addInitScript(CURSOR);
    const page = await context.newPage();
    const rec = new Recorder(page, shot.id);
    const started = Date.now();
    let status = 'ok';
    try {
      const r = await shot.run(page, rec);
      if (r === 'skipped') status = 'skipped';
    } catch (e) {
      status = `failed: ${e.message.split('\n')[0]}`;
      console.log(`   ${status}`);
    }
    await rec.stop().catch(() => {});
    const out = path.join(FOOTAGE, `${shot.id}.mp4`);
    if (status !== 'skipped') {
      if (FFMPEG) {
        if (rec.save(out)) console.log(`   saved ${path.relative(process.cwd(), out)} (${((Date.now() - started) / 1000).toFixed(0)}s of driving)`);
      } else {
        const video = page.video();
        await context.close();
        if (video) { const p = await video.path(); fs.copyFileSync(p, out.replace(/\.mp4$/, '.webm')); console.log(`   saved ${shot.id}.webm`); }
      }
    }
    await context.close().catch(() => {});
    fs.rmSync(tmpVideo, { recursive: true, force: true });
    results.push([shot.id, status]);
  }
} finally {
  await browser.close();
}
console.log('\nSummary:');
for (const [id, status] of results) console.log(`  ${status === 'ok' ? '✓' : status === 'skipped' ? '–' : '✗'} ${id}  ${status === 'ok' ? '' : status}`);
