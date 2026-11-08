// Headless-Chrome check of the built page under its real placement prefix.
//   BASE=/void/baddaysim-pain-box/ OUT=dist node build.mjs
//   PW_DIR=<dir with node_modules/playwright> node test/e2e.mjs
// Serves dist/ only under BASE (anything else is 404), so a wrong base fails here, not in production.
import http from "node:http";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import assert from "node:assert/strict";

const base = process.env.BASE ?? "/void/baddaysim-pain-box/";
const out = process.env.OUT ?? "dist";
const require = createRequire((process.env.PW_DIR ?? process.cwd()) + "/");
const { chromium } = require("playwright");
const types = { ".html": "text/html", ".js": "text/javascript", ".css": "text/css" };

const server = http.createServer(async (req, res) => {
  const path = new URL(req.url, "http://x").pathname;
  if (!path.startsWith(base)) { res.writeHead(404).end("outside base"); return; }
  const rel = path.slice(base.length) || "index.html";
  try {
    const body = await readFile(`${out}/${rel}`);
    res.writeHead(200, { "content-type": types[rel.slice(rel.lastIndexOf("."))] ?? "application/octet-stream" }).end(body);
  } catch { res.writeHead(404).end("missing"); }
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));
const origin = `http://127.0.0.1:${server.address().port}`;

const browser = await chromium.launch();
const errors = [], failed = [];
const page = await browser.newPage({ viewport: { width: 900, height: 1500 } });
page.on("pageerror", (e) => errors.push(String(e)));
page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
page.on("response", (r) => r.status() >= 400 && failed.push(`${r.status()} ${r.url()}`));

await page.goto(`${origin}${base}?seed=1&speed=16`, { waitUntil: "load" });
const text = (id) => page.locator(`#${id}`).innerText();
assert.equal(await text("counter"), "--");
assert.equal(await text("toggle"), "Open the pain box");

// Open it: the counter must start from the model's forecast (seed 1 breaks at 14.7 s) and count down.
await page.click("#toggle");
const first = await text("counter");
assert.match(first, /^\d+\.\d s$/);
await page.waitForTimeout(500);
const second = await text("counter");
assert.ok(parseFloat(second) < parseFloat(first), `counter did not count down: ${first} -> ${second}`);
const painNow = parseFloat(await page.locator(".meter .val").nth(0).innerText());
assert.ok(painNow >= 0);
await page.screenshot({ path: process.env.SHOT_DIR ? `${process.env.SHOT_DIR}/running.png` : "/dev/null" });

// 16x speed: the will breaks at 14.7 s of sim time, about a second of wall time.
await page.waitForFunction(() => document.getElementById("counter").innerText === "14.7 s" && document.getElementById("toggle").innerText === "Open the box again", null, { timeout: 15000 });
assert.match(await text("counter-note"), /will broke/);
assert.equal(await page.locator(".meter .val").nth(2).innerText(), "0.00"); // will is spent
if (process.env.SHOT_DIR) await page.screenshot({ path: `${process.env.SHOT_DIR}/broken.png` });

// Toggle path: reopen, switch the box off early; the will must hold.
await page.click("#toggle");
await page.waitForTimeout(300);
await page.click("#toggle");
await page.waitForFunction(() => /switched off/.test(document.getElementById("counter-note").innerText), null, { timeout: 5000 });
assert.match(await text("counter-note"), /The will held/);
if (process.env.SHOT_DIR) await page.screenshot({ path: `${process.env.SHOT_DIR}/disabled.png` });

assert.deepEqual(failed, [], "requests failed under base");
assert.deepEqual(errors, [], "console errors");
// And a wrong prefix must not serve the page.
const wrong = await page.goto(`${origin}/elsewhere/`, { waitUntil: "load" });
assert.equal(wrong.status(), 404);
await browser.close();
server.close();
console.log(`e2e ok under ${base}`);
