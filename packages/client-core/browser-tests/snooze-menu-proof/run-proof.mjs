// Cockpit snooze menu proof driver.
//
// It (1) esbuild-bundles the REAL Cockpit SessionMenu (harness-entry.tsx -> the shipping component +
// useSnoozeOptions + buildSnoozeMenu + holdSession), (2) serves this directory over http, and (3) drives
// the menu in a real Chromium, proving the client guarantees with hard evidence:
//   claim A - the plain item names the user's default length, from the Gateway ("Snooze  (1 hour)").
//   claim B - "Snooze for" opens the Gateway's four lengths, with the default marked - the SAME words
//             the desktop menu shows.
//   claim C - picking "4 hours" POSTs snoozeMinutes=240, not the default.
//   claim D - the plain Snooze click sends NO length, so the Gateway applies the default.
//   claim E - a snoozed session shows "Unsnooze" and STILL offers "Snooze for", so a length can be
//             changed in one step.
//   claim F - three menus on the page share ONE presets fetch (a fetch per card would make opening a
//             menu slow and chatty).
//
// This proves the CLIENT flow against shipping code. It is NOT the real Gateway - its storage and timer
// are proven by the Gateway C# suite and, for the desktop, live against a real Gateway + Director.
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
const cockpitCss = join(here, "../../../../apps/cockpit/src/styles.css");
const port = Number(process.env.PORT || 8798);
const contentTypes = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
};

const log = (m) => console.log(`[menu-proof] ${m}`);

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
  log(`bundled the real Cockpit SessionMenu (${js.length} bytes)`);
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
      // The Cockpit's real stylesheet, straight from source, so the screenshot cannot drift from ship.
      if (path === "/styles.css") {
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

const menuItems = () =>
  Array.from(document.querySelectorAll(".session-menu-pop [role=menuitem]")).map((b) => b.textContent.trim());

const fail = [];
function check(claim, ok, detail) {
  log(`${ok ? "PASS" : "FAIL"} - ${claim}: ${detail}`);
  if (!ok) fail.push(claim);
}

// Open the first card's menu.
async function openMenu(page) {
  await page.click(".session-menu.rail .session-menu-btn");
  await waitFor(page, "the menu to open", () => document.querySelector(".session-menu-pop") !== null);
}

async function main() {
  const js = await bundle();
  const server = await serve(js);
  const url = `http://127.0.0.1:${port}/`;
  log(`serving ${url}`);

  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 820, height: 520 } });
  const evidence = { scenario: "cockpit snooze menu: default named, lengths offered, chosen length sent", steps: {} };

  try {
    await page.goto(url);
    await waitFor(page, "the presets to load", () => window.__presetFetches > 0);

    // ---- claim F: three menus, ONE fetch.
    const fetches = await page.evaluate(() => window.__presetFetches);
    evidence.steps.presetFetchesForThreeMenus = fetches;
    check("F: three menus share one presets fetch", fetches === 1, `${fetches} fetch(es) for 3 menus`);

    // ---- claim A: the plain item names the default.
    await openMenu(page);
    const items = await page.evaluate(menuItems);
    evidence.steps.menuItems = items;
    await page.screenshot({ path: join(here, "evidence-menu.png") });
    check("A: the plain item names the default length", items.includes("Snooze  (1 hour)"), items.join(" | "));

    // ---- claim B: the submenu lists the Gateway's lengths, default marked.
    await page.hover(".session-menu-item.has-sub");
    await waitFor(page, "the submenu", () => document.querySelector(".session-menu-subpop") !== null);
    const subItems = await page.evaluate(() =>
      Array.from(document.querySelectorAll(".session-menu-subpop [role=menuitem]")).map((b) => b.textContent.trim()));
    evidence.steps.submenuItems = subItems;
    await page.screenshot({ path: join(here, "evidence-submenu.png") });
    check(
      "B: the submenu shows the Gateway's lengths with the default marked",
      JSON.stringify(subItems) === JSON.stringify(["15 minutes", "1 hour  (default)", "4 hours", "8 hours"]),
      subItems.join(" | "),
    );

    // ---- claim G: the flyout is actually ON SCREEN and reachable. It prefers to open left, but a card
    // near the LEFT edge has no room there - it must flip right rather than land at a negative x where
    // nothing can click it. This card is deliberately at the left edge.
    const box = await page.evaluate(() => {
      const r = document.querySelector(".session-menu-subpop").getBoundingClientRect();
      return { left: r.left, right: r.right, top: r.top, bottom: r.bottom, w: window.innerWidth, h: window.innerHeight };
    });
    evidence.steps.submenuBox = box;
    check(
      "G: the flyout is fully on screen for a card at the left edge",
      box.left >= 0 && box.right <= box.w && box.top >= 0 && box.bottom <= box.h,
      `left=${Math.round(box.left)} right=${Math.round(box.right)} viewport=${box.w}x${box.h}`,
    );

    // ---- claim C: picking 4 hours sends 240.
    await page.click(".session-menu-subpop [role=menuitem]:nth-child(3)");
    await waitFor(page, "the hold to be sent", () => window.__holds.length === 1);
    const holdC = (await page.evaluate(() => window.__holds))[0];
    evidence.steps.snoozeFor = holdC;
    check(
      "C: picking 4 hours sends snoozeMinutes=240",
      holdC.onHold === true && holdC.snoozeMinutes === 240,
      JSON.stringify(holdC),
    );

    // ---- claim E: snoozed -> Unsnooze, submenu still offered.
    await waitFor(page, "the card to read Snoozed", () => document.body.textContent.includes("Snoozed"));
    await openMenu(page);
    const snoozedItems = await page.evaluate(menuItems);
    evidence.steps.snoozedMenuItems = snoozedItems;
    await page.screenshot({ path: join(here, "evidence-unsnooze.png") });
    check("E: a snoozed session says Unsnooze", snoozedItems.includes("Unsnooze"), snoozedItems.join(" | "));
    check(
      "E: a snoozed session still offers Snooze for",
      snoozedItems.some((i) => i.startsWith("Snooze for")),
      snoozedItems.join(" | "),
    );

    // ---- claim D: the plain click sends NO length (the Gateway applies the default).
    await page.click(".session-menu-pop [role=menuitem]:nth-child(2)"); // "Unsnooze"
    await waitFor(page, "the unsnooze", () => window.__holds.length === 2);
    await waitFor(page, "the card to clear", () => !document.body.textContent.includes("Snoozed"));
    await openMenu(page);
    await page.click(".session-menu-pop [role=menuitem]:nth-child(2)"); // plain "Snooze  (1 hour)"
    await waitFor(page, "the plain snooze", () => window.__holds.length === 3);
    const holdD = (await page.evaluate(() => window.__holds))[2];
    evidence.steps.plainSnooze = holdD;
    check(
      "D: the plain Snooze click sends no length, so the Gateway applies the default",
      holdD.onHold === true && holdD.snoozeMinutes === undefined,
      JSON.stringify(holdD),
    );

    evidence.generatedAtUtc = new Date().toISOString();
    evidence.result = fail.length === 0 ? "PASS" : `FAIL (${fail.join("; ")})`;
    await writeFile(join(here, `evidence-${evidence.generatedAtUtc.slice(0, 10)}.json`), JSON.stringify(evidence, null, 2));
    log("wrote evidence json");
  } finally {
    await browser.close();
    server.close();
  }

  log(fail.length === 0 ? "ALL CLAIMS PASS" : `FAILED: ${fail.join("; ")}`);
  process.exit(fail.length === 0 ? 0 : 1);
}

main().catch((e) => {
  console.error(`[menu-proof] ERROR: ${e.stack || e.message}`);
  process.exit(1);
});
