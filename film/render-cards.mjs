#!/usr/bin/env node
// Render still cards (placeholders for missing footage, title cards) from
// scenes/card.html. Input: a JSON file of [{ "out": "path.png", "kind":
// "placeholder"|"card", "title", "sub", "kicker", "hint" }]. Used by
// assemble.py; one browser renders them all.
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
import url from 'node:url';
const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const here = path.dirname(url.fileURLToPath(import.meta.url));
const specs = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
  for (const s of specs) {
    const q = new URLSearchParams();
    for (const k of ['kind', 'title', 'sub', 'kicker', 'hint']) if (s[k]) q.set(k, s[k]);
    await page.goto(url.pathToFileURL(path.join(here, 'scenes', 'card.html')).href + '?' + q.toString());
    await page.evaluate(async () => { await document.fonts.load('600 40px "Inter"'); await document.fonts.load('500 40px "Source Serif 4"'); await document.fonts.ready; });
    fs.mkdirSync(path.dirname(s.out), { recursive: true });
    await page.screenshot({ path: s.out, type: 'png' });
  }
} finally {
  await browser.close();
}
