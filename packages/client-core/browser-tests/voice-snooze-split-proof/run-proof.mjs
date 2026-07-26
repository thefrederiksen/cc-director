// Phone voice-mode split Snooze proof driver.
//
// It (1) esbuild-bundles the REAL mobile VoiceMode screen (harness-entry.tsx -> the shipping page +
// useSessionManage + useSnoozeOptions + buildSnoozeMenu + holdSession), (2) serves this directory over
// http with the phone app's real stylesheet, and (3) drives it in a real Chromium at a phone viewport,
// proving the client guarantees with hard evidence:
//   claim A - the Snooze slab is TWO targets, and the wide one keeps most of the width, so the tap made
//             every turn cannot land on the picker by accident.
//   claim B - tapping the wide part sends NO length (the Gateway applies the user's default) and returns
//             to the queue - the behaviour that shipped, unchanged.
//   claim C - tapping the narrow part opens a sheet listing the Gateway's lengths with the default
//             marked - the SAME words the Cockpit menu and the desktop rail show.
//   claim D - picking "4 hours" POSTs snoozeMinutes=240, not the default, and returns to the queue.
//   claim E - picking a length while ALREADY snoozed re-arms (onHold=true + the new length). It never
//             un-snoozes, which is what a shared toggle would have done.
//   claim F - when this phone has never read the lengths, there is NO picker at all and the plain
//             Snooze still works. It never invents lengths that are not the user's.
//
// This proves the CLIENT flow against shipping code. It is NOT the real Gateway - the snooze storage and
// timer are proven separately by the Gateway C# suite (SnoozePresetsConfigTests + end-to-end).
//
// Run:  node run-proof.mjs   ->  writes evidence-<date>.json + screenshots, prints PASS/FAIL. ASCII only.

import { build } from "esbuild";
import { createServer } from "node:http";
import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join, extname } from "node:path";
import { createRequire } from "node:module";

const requireCjs = createRequire(import.meta.url);
const PLAYWRIGHT_PATH =
  process.env.PLAYWRIGHT_PATH ||
  "C:/Users/soren/AppData/Roaming/npm/node_modules/@playwright/cli/node_modules/playwright";
const { chromium } = requireCjs(PLAYWRIGHT_PATH);

const here = dirname(fileURLToPath(import.meta.url));
const mobileCss = join(here, "../../../../apps/mobile/src/styles.css");
const port = Number(process.env.PORT || 8801);
const contentTypes = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
};

const log = (m) => console.log(`[voice-snooze-proof] ${m}`);

// The screen's import graph contains a stylesheet of its own (the dictation dialog's), so the bundle has
// TWO outputs. Both are served: dropping the CSS would render the page in less than its real styles.
async function bundle() {
  const result = await build({
    entryPoints: [join(here, "harness-entry.tsx")],
    bundle: true,
    format: "iife",
    write: false,
    outdir: join(here, "bundle"),
    target: "es2021",
    jsx: "automatic",
    loader: { ".ts": "ts", ".tsx": "tsx" },
    logLevel: "warning",
  });
  const js = result.outputFiles.find((f) => f.path.endsWith(".js"))?.text;
  const css = result.outputFiles.find((f) => f.path.endsWith(".css"))?.text ?? "";
  if (js === undefined) throw new Error("the bundle produced no JavaScript output");
  log(`bundled the real mobile VoiceMode screen (${js.length} bytes js, ${css.length} bytes css)`);
  return { js, css };
}

function serve(js, css) {
  const server = createServer(async (req, res) => {
    const path = (req.url || "/").split("?")[0];
    try {
      if (path === "/harness.iife.js") {
        res.writeHead(200, { "content-type": contentTypes[".js"] });
        res.end(js);
        return;
      }
      if (path === "/bundle.css") {
        res.writeHead(200, { "content-type": contentTypes[".css"] });
        res.end(css);
        return;
      }
      if (path === "/styles.css") {
        res.writeHead(200, { "content-type": contentTypes[".css"] });
        res.end(await readFile(mobileCss));
        return;
      }
      const file = path === "/" ? "index.html" : path.replace(/^\/+/, "");
      const body = await readFile(join(here, file));
      res.writeHead(200, { "content-type": contentTypes[extname(file)] || "application/octet-stream" });
      res.end(body);
    } catch {
      res.writeHead(404, { "content-type": "text/plain" });
      res.end("not found");
    }
  });
  return new Promise((resolve) => server.listen(port, "127.0.0.1", () => resolve(server)));
}

async function waitFor(page, desc, fn, timeoutMs = 15000) {
  const start = Date.now();
  for (;;) {
    let ok = false;
    try { ok = await page.evaluate(fn); } catch { ok = false; }
    if (ok) return;
    if (Date.now() - start > timeoutMs) throw new Error(`timed out waiting for: ${desc}`);
    await page.waitForTimeout(150);
  }
}

const fail = [];
function check(claim, ok, detail) {
  log(`${ok ? "PASS" : "FAIL"} - ${claim}: ${detail}`);
  if (!ok) fail.push(claim);
}

const sheetChoices = () =>
  Array.from(document.querySelectorAll(".snooze-sheet-choice")).map((b) => b.textContent.trim());

// Back to the voice screen after a snooze bounced the router to the queue.
async function reopenVoiceScreen(page) {
  await page.evaluate(() => window.__reload());
  await waitFor(page, "the snooze button", () => document.querySelector(".voice-action-snooze") !== null);
}

async function run(page, evidence) {
  await waitFor(page, "the voice screen", () => document.querySelector(".voice-action-snooze") !== null);
  await waitFor(page, "the lengths to load", () => document.querySelector(".voice-snooze-more") !== null);
  await page.screenshot({ path: join(here, "evidence-split-button.png") });

  // ---- claim A: two targets in one slab, and the wide one keeps most of the width.
  const geometry = await page.evaluate(() => {
    const wide = document.querySelector(".voice-action-snooze").getBoundingClientRect();
    const narrow = document.querySelector(".voice-snooze-more").getBoundingClientRect();
    return {
      wide: { x: Math.round(wide.x), w: Math.round(wide.width), h: Math.round(wide.height) },
      narrow: { x: Math.round(narrow.x), w: Math.round(narrow.width), h: Math.round(narrow.height) },
    };
  });
  evidence.steps.geometry = geometry;
  check(
    "A: the slab is two targets, the wide Snooze keeping most of the width",
    geometry.wide.w >= geometry.narrow.w * 3 &&
      geometry.narrow.w >= 44 && geometry.narrow.h >= 44 &&
      geometry.narrow.x >= geometry.wide.x + geometry.wide.w,
    `wide=${geometry.wide.w}px narrow=${geometry.narrow.w}x${geometry.narrow.h}px`,
  );

  // ---- claim B: the wide part sends NO length and returns to the queue.
  await page.click(".voice-action-snooze");
  await waitFor(page, "the plain snooze", () => window.__holds.length === 1);
  await waitFor(page, "the queue", () => document.getElementById("queue-marker") !== null);
  const plain = (await page.evaluate(() => window.__holds))[0];
  evidence.steps.plainSnooze = plain;
  check(
    "B: the wide part sends no length, so the Gateway applies the default, and returns to the queue",
    plain.onHold === true && plain.snoozeMinutes === undefined,
    JSON.stringify(plain),
  );

  // Back to a fresh, un-snoozed voice screen for the picker claims.
  await page.evaluate(() => { window.__onHold = false; });
  await reopenVoiceScreen(page);

  // ---- claim C: the narrow part opens the Gateway's lengths, default marked.
  await page.click(".voice-snooze-more");
  await waitFor(page, "the sheet", () => document.querySelector(".snooze-sheet") !== null);
  const choices = await page.evaluate(sheetChoices);
  evidence.steps.sheetChoices = choices;
  await page.screenshot({ path: join(here, "evidence-length-sheet.png") });
  check(
    "C: the picker shows the Gateway's lengths with the default marked",
    JSON.stringify(choices) === JSON.stringify(["15 minutes", "1 hour  (default)", "4 hours", "8 hours"]),
    choices.join(" | "),
  );

  // ---- claim D: picking 4 hours sends 240 and returns to the queue.
  await page.click(".snooze-sheet-choice:nth-of-type(3)");
  await waitFor(page, "the length snooze", () => window.__holds.length === 2);
  await waitFor(page, "the queue", () => document.getElementById("queue-marker") !== null);
  const picked = (await page.evaluate(() => window.__holds))[1];
  evidence.steps.snoozeForFourHours = picked;
  check(
    "D: picking 4 hours sends snoozeMinutes=240 and returns to the queue",
    picked.onHold === true && picked.snoozeMinutes === 240,
    JSON.stringify(picked),
  );

  // ---- claim E: picking a length while ALREADY snoozed re-arms, never un-snoozes.
  await reopenVoiceScreen(page); // __onHold is still true from claim D
  await waitFor(page, "the Unsnooze label", () =>
    document.querySelector(".voice-action-snooze").textContent.trim() === "Unsnooze");
  await page.click(".voice-snooze-more");
  await waitFor(page, "the sheet", () => document.querySelector(".snooze-sheet") !== null);
  await page.screenshot({ path: join(here, "evidence-resnooze.png") });
  await page.click(".snooze-sheet-choice:nth-of-type(1)"); // 15 minutes
  await waitFor(page, "the re-arm", () => window.__holds.length === 3);
  const rearm = (await page.evaluate(() => window.__holds))[2];
  evidence.steps.reArmWhileSnoozed = rearm;
  check(
    "E: picking a length while snoozed re-arms the clock - it never un-snoozes",
    rearm.onHold === true && rearm.snoozeMinutes === 15,
    JSON.stringify(rearm),
  );

  // ---- claim F: no known lengths -> no picker, and the plain Snooze still works.
  await page.evaluate(() => {
    window.__presets = null;
    window.__onHold = false;
    window.__resetSnoozeCache();
  });
  await reopenVoiceScreen(page);
  await page.waitForTimeout(600); // give a picker every chance to appear before asserting it does not
  const noPicker = await page.evaluate(() => ({
    picker: document.querySelector(".voice-snooze-more") === null,
    snooze: document.querySelector(".voice-action-snooze").textContent.trim(),
  }));
  evidence.steps.noKnownLengths = noPicker;
  await page.screenshot({ path: join(here, "evidence-no-lengths.png") });
  check(
    "F: with no lengths known there is NO picker, and the plain Snooze remains",
    noPicker.picker && noPicker.snooze === "Snooze",
    JSON.stringify(noPicker),
  );

  await page.click(".voice-action-snooze");
  await waitFor(page, "the plain snooze", () => window.__holds.length === 4);
  const fallbackPlain = (await page.evaluate(() => window.__holds))[3];
  evidence.steps.plainSnoozeWithNoLengths = fallbackPlain;
  check(
    "F: the plain Snooze still works with no lengths known",
    fallbackPlain.onHold === true && fallbackPlain.snoozeMinutes === undefined,
    JSON.stringify(fallbackPlain),
  );
}

async function main() {
  const { js, css } = await bundle();
  const server = await serve(js, css);
  const url = `http://127.0.0.1:${port}/`;
  log(`serving ${url}`);

  const browser = await chromium.launch();
  // A phone viewport, because this is a phone screen and the split button's geometry is the claim.
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  const evidence = {
    scenario: "phone voice mode: the Snooze slab splits into a default-length tap and a length picker",
    steps: {},
  };

  try {
    await page.goto(url);
    await run(page, evidence);
    evidence.generatedAtUtc = new Date().toISOString();
    evidence.result = fail.length === 0 ? "PASS" : `FAIL (${fail.join("; ")})`;
    await writeFile(
      join(here, `evidence-${evidence.generatedAtUtc.slice(0, 10)}.json`),
      JSON.stringify(evidence, null, 2),
    );
    log("wrote evidence json");
  } finally {
    await browser.close();
    server.close();
  }

  log(fail.length === 0 ? "ALL CLAIMS PASS" : `FAILED: ${fail.join("; ")}`);
  process.exit(fail.length === 0 ? 0 : 1);
}

main().catch((e) => {
  console.error(`[voice-snooze-proof] ERROR: ${e.stack || e.message}`);
  process.exit(1);
});
