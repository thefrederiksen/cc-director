// Car Mode Help Mode proof driver (Help Mode, issue #1441).
//
// It (1) esbuild-bundles the REAL product Help flow (harness-entry.tsx -> useCarMode.help + getCarModeHelp),
// (2) serves this directory over http, and (3) drives the "Help" button in a real Chromium (fake media
// device), proving the client Help guarantees with hard evidence:
//   claim A - the on-screen cheat-sheet is rendered from GET /carmode/help (the modes and first title
//             match the server's response).
//   claim B - tapping Help from IDLE starts Car Mode (primes the audio in the gesture) and SPEAKS the
//             server's curated script VERBATIM through the good voice (/wingman/tts got exactly that text).
//   claim C - after the help finishes, the microphone returns to the owner (phase = listening).
//   claim D - tapping Help again while RUNNING speaks the curated script immediately (a second time).
//
// This proves the CLIENT Help flow against the shipping code. It does NOT prove the real model's
// addressing-boundary choice (that is a separate live-model proof) and it is NOT the real phone.
//
// Run:  node run-proof.mjs   ->  writes evidence-<date>.json + a screenshot, prints PASS/FAIL. ASCII only.

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
const port = Number(process.env.PORT || 8795);
const contentTypes = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8" };
const HELP_SPOKEN =
  "I'm your fleet manager, and you talk to me two ways. By default you command me. To talk to a session, "
  + "start with tell, answer, reply, or message, and name it. Say over and out when you're done.";

const log = (m) => console.log(`[help-proof] ${m}`);

async function writeFakeAudioWav(path, toneSeconds = 2, silenceSeconds = 4, rate = 48000, freq = 440) {
  const total = Math.round((toneSeconds + silenceSeconds) * rate);
  const toneSamples = Math.round(toneSeconds * rate);
  const dataBytes = total * 2;
  const buf = Buffer.alloc(44 + dataBytes);
  buf.write("RIFF", 0); buf.writeUInt32LE(36 + dataBytes, 4); buf.write("WAVE", 8);
  buf.write("fmt ", 12); buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(1, 22);
  buf.writeUInt32LE(rate, 24); buf.writeUInt32LE(rate * 2, 28); buf.writeUInt16LE(2, 32); buf.writeUInt16LE(16, 34);
  buf.write("data", 36); buf.writeUInt32LE(dataBytes, 40);
  for (let i = 0; i < total; i++) {
    const v = i < toneSamples ? Math.round(Math.sin((2 * Math.PI * freq * i) / rate) * 12000) : 0;
    buf.writeInt16LE(v, 44 + i * 2);
  }
  await writeFile(path, buf);
  return path;
}

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
  log(`bundled the real product Help flow (${js.length} bytes)`);
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

async function main() {
  const js = await bundle();
  const server = await serve(js);
  const url = `http://127.0.0.1:${port}/`;
  log(`serving ${url}`);

  const wavPath = join(here, "fake-capture.wav");
  await writeFakeAudioWav(wavPath);

  const browser = await chromium.launch({
    args: [
      "--use-fake-device-for-media-stream",
      "--use-fake-ui-for-media-stream",
      `--use-file-for-fake-audio-capture=${wavPath}`,
      "--autoplay-policy=no-user-gesture-required",
    ],
  });
  const context = await browser.newContext({ permissions: ["microphone"] });
  const page = await context.newPage();
  page.on("console", (m) => { if (m.type() === "error") console.log(`  [page-error] ${m.text()}`); });

  const evidence = { generatedAtUtc: new Date().toISOString(), scenario: "Help button: idle-start-and-speak, then active-speak", steps: {} };

  try {
    await page.goto(url, { waitUntil: "domcontentloaded" });
    await waitFor(page, "harness mounted", `!!document.getElementById("help")`);

    // CLAIM A - the cheat-sheet was fetched from GET /carmode/help and rendered (modes + first title).
    await waitFor(page, "cheat-sheet rendered", `document.getElementById("cheatModes").textContent === "2"`);
    const cheatModes = await page.evaluate(`document.getElementById("cheatModes").textContent`);
    const cheatFirstTitle = await page.evaluate(`document.getElementById("cheatFirstTitle").textContent`);
    evidence.steps.claimA_cheatSheetRendered = {
      pass: cheatModes === "2" && cheatFirstTitle === "Command me",
      cheatModes, cheatFirstTitle,
      note: "The on-screen cheat-sheet came from GET /carmode/help - the same source as the spoken help.",
    };

    // CLAIM B - tap Help from IDLE: it starts Car Mode and speaks the server's curated script verbatim.
    const startedBefore = await page.evaluate(`document.getElementById("started").textContent`);
    await page.click("#help");
    await waitFor(page, "started=true after Help", `document.getElementById("started").textContent === "true"`);
    await waitFor(page, "reply is the curated script", `document.getElementById("reply").textContent.length > 0`);
    const reply = await page.evaluate(`document.getElementById("reply").textContent`);
    const ttsAfterIdle = await page.evaluate(`window.__ttsTexts.slice()`);
    await page.screenshot({ path: join(here, "evidence-help-spoken.png") });
    evidence.steps.claimB_idleHelpStartsAndSpeaks = {
      pass: startedBefore === "false" && reply === HELP_SPOKEN && ttsAfterIdle.includes(HELP_SPOKEN),
      startedBefore, reply, spokenThroughGoodVoice: ttsAfterIdle,
      note: "From idle, Help started Car Mode (priming the audio in the tap gesture) and sent the EXACT server script to /wingman/tts.",
    };

    // CLAIM C - after the help finishes, the microphone returns to the owner (phase = listening).
    await waitFor(page, "phase returns to listening", `document.getElementById("phase").textContent === "listening"`, 20000);
    const phaseAfter = await page.evaluate(`document.getElementById("phase").textContent`);
    evidence.steps.claimC_returnsToListening = {
      pass: phaseAfter === "listening",
      phaseAfter,
      note: "speakAndPlay handed the microphone back after the help clip finished.",
    };

    // CLAIM D - tap Help again while RUNNING: it speaks the curated script immediately (a second time).
    await page.click("#help");
    await waitFor(page, "help spoken a second time",
      `window.__ttsTexts.filter((t) => t === ${JSON.stringify(HELP_SPOKEN)}).length >= 2`, 20000);
    const ttsAfterActive = await page.evaluate(`window.__ttsTexts.slice()`);
    const helpFetchCount = await page.evaluate(`window.__helpFetchCount`);
    evidence.steps.claimD_activeHelpSpeaksAgain = {
      pass: ttsAfterActive.filter((t) => t === HELP_SPOKEN).length >= 2,
      spokenCount: ttsAfterActive.filter((t) => t === HELP_SPOKEN).length,
      helpFetchCount,
      note: "While running, Help spoke the curated script immediately again (each trigger reads the one server source).",
    };

    const pass = Object.values(evidence.steps).every((s) => s.pass);
    evidence.verdict = pass ? "PASS" : "FAIL";
    evidence.covers = "Real Chromium: the shipping useCarMode help()/speakHelp path, start() priming audio in the gesture, real getCarModeHelp() fetch + parse, real reply <audio> playback, and the on-screen cheat-sheet - both triggers read the ONE server help source.";
    evidence.doesNotCover = "NOT the real phone (no real mic/audio-session/autoplay), and NOT the real model's addressing-boundary choice (a separate live-model proof). The Gateway is an in-page shim serving the curated help content. Soren's by-hand phone pass remains the final on-device confirmation.";

    const date = new Date().toISOString().slice(0, 10);
    await writeFile(join(here, `evidence-${date}.json`), JSON.stringify(evidence, null, 2));
    log(`VERDICT: ${evidence.verdict}`);
    console.log(JSON.stringify(evidence, null, 2));

    await browser.close();
    server.close();
    process.exit(pass ? 0 : 1);
  } catch (err) {
    log(`FAILED: ${err && err.message ? err.message : err}`);
    try { await page.screenshot({ path: join(here, "evidence-error.png") }); } catch {}
    await browser.close();
    server.close();
    process.exit(2);
  }
}

main();
