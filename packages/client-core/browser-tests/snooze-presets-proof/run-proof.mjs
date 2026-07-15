// Snooze lengths proof driver (snooze presets).
//
// It (1) esbuild-bundles the REAL Cockpit SnoozeCard (harness-entry.tsx -> the shipping component +
// settingsClient + snoozeFormat), (2) serves this directory over http, and (3) drives the card in a real
// Chromium, proving the client guarantees with hard evidence:
//   claim A - the list renders the Gateway's lengths, in the Gateway's words ("15 minutes" ... "8 hours"),
//             with the dot on the Gateway's default.
//   claim B - picking a different row sends ONE PUT carrying BOTH the list and the new default, so the
//             two can never be written apart, and the dot lands on the picked row.
//   claim C - adding a length sends the widened list and KEEPS the existing default.
//   claim D - removing the row that holds the dot moves the dot to the shortest remaining length rather
//             than leaving the default off the menu.
//   claim E - the last remaining length cannot be removed (the menu can never be empty).
//   claim F - "Add a length" is disabled once the menu is full (five).
//
// This proves the CLIENT flow against shipping code. The Gateway's own storage and validation are proven
// separately in C# (SnoozePresetsConfigTests + the Gateway end-to-end suite). It is NOT the real Gateway.
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
const cockpitCss = join(here, "../../../../apps/cockpit/src/settings/settings.css");
const port = Number(process.env.PORT || 8797);
const contentTypes = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
};

const log = (m) => console.log(`[snooze-proof] ${m}`);

async function bundle() {
  const result = await build({
    entryPoints: [join(here, "harness-entry.tsx")],
    bundle: true,
    format: "iife",
    write: false,
    target: "es2021",
    jsx: "automatic",
    loader: { ".ts": "ts", ".tsx": "tsx" },
    logLevel: "warning",
  });
  const js = result.outputFiles[0].text;
  log(`bundled the real Cockpit SnoozeCard (${js.length} bytes)`);
  return js;
}

function serve(js) {
  const server = createServer(async (req, res) => {
    const path = (req.url || "/").split("?")[0];
    try {
      if (path === "/harness.iife.js") {
        res.writeHead(200, { "content-type": contentTypes[".js"] });
        res.end(js);
        return;
      }
      // The card's real stylesheet, served straight from the Cockpit source so the screenshot shows the
      // shipping look rather than a copy that could drift.
      if (path === "/settings.css") {
        res.writeHead(200, { "content-type": contentTypes[".css"] });
        res.end(await readFile(cockpitCss));
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

// What the user actually sees: each row's words, and which row holds the dot.
const readRows = () =>
  Array.from(document.querySelectorAll(".snooze-row")).map((row) => ({
    name: row.querySelector(".snooze-row-name")?.textContent ?? "",
    isDefault: row.querySelector('input[type="radio"]')?.checked === true,
    removeDisabled: Array.from(row.querySelectorAll("button")).find((b) => b.textContent === "Remove")?.disabled === true,
  }));

const clickRow = async (page, name, button) =>
  page.evaluate(
    ([n, b]) => {
      const row = Array.from(document.querySelectorAll(".snooze-row")).find(
        (r) => r.querySelector(".snooze-row-name")?.textContent === n,
      );
      if (!row) throw new Error(`no row named ${n}`);
      if (b === "radio") row.querySelector('input[type="radio"]').click();
      else Array.from(row.querySelectorAll("button")).find((x) => x.textContent === b).click();
      return true;
    },
    [name, button],
  );

const fail = [];
function check(claim, ok, detail) {
  log(`${ok ? "PASS" : "FAIL"} - ${claim}: ${detail}`);
  if (!ok) fail.push(claim);
}

async function main() {
  const js = await bundle();
  const server = await serve(js);
  const url = `http://127.0.0.1:${port}/`;
  log(`serving ${url}`);

  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 760, height: 720 } });
  const evidence = { scenario: "snooze lengths: render, pick default, add, remove", steps: {} };

  try {
    await page.goto(url);
    await waitFor(page, "the list to render from the Gateway", () => document.querySelectorAll(".snooze-row").length > 0);

    // ---- claim A: renders the Gateway's lengths, in words, dot on the Gateway's default.
    const initial = await page.evaluate(readRows);
    evidence.steps.initial = initial;
    await page.screenshot({ path: join(here, "evidence-list.png") });
    check(
      "A: renders the Gateway's lengths in words",
      JSON.stringify(initial.map((r) => r.name)) === JSON.stringify(["15 minutes", "1 hour", "4 hours", "8 hours"]),
      initial.map((r) => r.name).join(", "),
    );
    check(
      "A: the dot is on the Gateway's default",
      initial.filter((r) => r.isDefault).map((r) => r.name).join(",") === "1 hour",
      initial.filter((r) => r.isDefault).map((r) => r.name).join(",") || "(none)",
    );

    // ---- claim B: picking a row sends ONE PUT with BOTH list and default.
    await clickRow(page, "4 hours", "radio");
    await waitFor(page, "the default to move to 4 hours", () => window.__puts.length === 1);
    const putB = (await page.evaluate(() => window.__puts))[0];
    evidence.steps.pickDefault = putB;
    check(
      "B: picking a default sends the list AND the default together",
      JSON.stringify(putB.presets) === JSON.stringify([15, 60, 240, 480]) && putB.defaultMinutes === 240,
      JSON.stringify(putB),
    );
    await waitFor(
      page,
      "the dot to land on 4 hours",
      () =>
        Array.from(document.querySelectorAll(".snooze-row")).find(
          (r) => r.querySelector(".snooze-row-name")?.textContent === "4 hours",
        )?.querySelector('input[type="radio"]')?.checked === true,
    );
    await page.screenshot({ path: join(here, "evidence-default-moved.png") });
    check("B: the dot lands on the picked row", true, "4 hours holds the dot after the round trip");

    // ---- claim C: adding a length widens the list and keeps the default.
    await page.evaluate(() => Array.from(document.querySelectorAll("button")).find((b) => b.textContent === "Add a length").click());
    await page.fill("#settings-snooze-count", "30");
    await page.selectOption('select[aria-label="Unit"]', "minutes");
    await page.evaluate(() =>
      Array.from(document.querySelectorAll(".settings-field button")).find((b) => b.textContent === "Save").click(),
    );
    await waitFor(page, "the add to be sent", () => window.__puts.length === 2);
    const putC = (await page.evaluate(() => window.__puts))[1];
    evidence.steps.add = putC;
    check(
      "C: adding a length widens the list and keeps the default",
      JSON.stringify([...putC.presets].sort((a, b) => a - b)) === JSON.stringify([15, 30, 60, 240, 480]) &&
        putC.defaultMinutes === 240,
      JSON.stringify(putC),
    );

    // ---- claim F: the menu is now full (five), so Add is disabled.
    await waitFor(page, "the list to show five lengths", () => document.querySelectorAll(".snooze-row").length === 5);
    const addDisabled = await page.evaluate(
      () => Array.from(document.querySelectorAll("button")).find((b) => b.textContent === "Add a length")?.disabled === true,
    );
    evidence.steps.addDisabledWhenFull = addDisabled;
    await page.screenshot({ path: join(here, "evidence-full.png") });
    check("F: Add is disabled once the menu is full", addDisabled, `addDisabled=${addDisabled}`);

    // ---- claim D: removing the row holding the dot moves the dot to the shortest remaining.
    await clickRow(page, "4 hours", "Remove");
    await waitFor(page, "the remove to be sent", () => window.__puts.length === 3);
    const putD = (await page.evaluate(() => window.__puts))[2];
    evidence.steps.removeDefault = putD;
    check(
      "D: removing the default moves the dot to the shortest remaining length",
      JSON.stringify([...putD.presets].sort((a, b) => a - b)) === JSON.stringify([15, 30, 60, 480]) &&
        putD.defaultMinutes === 15,
      JSON.stringify(putD),
    );
    const afterRemove = await page.evaluate(readRows);
    check(
      "D: the default is still one of the offered lengths",
      afterRemove.some((r) => r.isDefault),
      afterRemove.filter((r) => r.isDefault).map((r) => r.name).join(",") || "(none - INVARIANT BROKEN)",
    );

    // ---- claim E: the last remaining length cannot be removed.
    for (const name of ["30 minutes", "1 hour", "8 hours"]) {
      await clickRow(page, name, "Remove");
      await waitFor(page, `${name} to be gone`, (n) => !Array.from(document.querySelectorAll(".snooze-row-name")).some((e) => e.textContent === n), 15000).catch(
        () => {},
      );
      await page.waitForTimeout(250);
    }
    const last = await page.evaluate(readRows);
    evidence.steps.lastRow = last;
    await page.screenshot({ path: join(here, "evidence-last-row.png") });
    check(
      "E: the last remaining length cannot be removed",
      last.length === 1 && last[0].removeDisabled === true,
      `${last.length} row(s) left, removeDisabled=${last[0]?.removeDisabled}`,
    );

    evidence.generatedAtUtc = new Date().toISOString();
    evidence.result = fail.length === 0 ? "PASS" : `FAIL (${fail.join("; ")})`;
    const stamp = evidence.generatedAtUtc.slice(0, 10);
    await writeFile(join(here, `evidence-${stamp}.json`), JSON.stringify(evidence, null, 2));
    log(`wrote evidence-${stamp}.json`);
  } finally {
    await browser.close();
    server.close();
  }

  log(fail.length === 0 ? "ALL CLAIMS PASS" : `FAILED: ${fail.join("; ")}`);
  process.exit(fail.length === 0 ? 0 : 1);
}

main().catch((e) => {
  console.error(`[snooze-proof] ERROR: ${e.stack || e.message}`);
  process.exit(1);
});
