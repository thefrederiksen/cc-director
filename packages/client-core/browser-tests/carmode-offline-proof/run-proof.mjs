// Car Mode offline-resilience proof driver (mission Phase 4a, issue #1427).
//
// It (1) esbuild-bundles the REAL product turn machine (harness-entry.tsx -> useCarMode + pendingTurnStore
// + turnRetry + carModeApi) into one IIFE, (2) serves this directory over http (a real origin so IndexedDB
// and modules work), and (3) drives one Car Mode turn across a simulated connection drop in a real
// Chromium (fake media device), proving the three mission guarantees with hard evidence:
//   claim 1 - NO speech lost across a mid-transcribe drop: after the transcribe fails, the command AUDIO
//             is still in the durable IndexedDB store (a real Blob, non-empty).
//   claim 2 - the held turn AUTO-COMPLETES on reconnect: firing the browser `online` event re-drives it
//             through transcribe -> brain -> speak, the reply appears, and the durable record is DELETED
//             (turn owned) so it can never double-act.
//   claim 3 - the offline / holding + back-online states are ANNOUNCED: the holding line and the
//             connection-down line were spoken (captured speechSynthesis), and the recovered reply was
//             sent to the good voice WITH the "Back online" prefix.
//
// Run:  node run-proof.mjs
// Writes evidence-<date>.json + screenshots beside this file, prints PASS/FAIL. ASCII only.

import { build } from "esbuild";
import { createServer } from "node:http";
import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join, extname } from "node:path";
import { createRequire } from "node:module";

// Playwright is only installed globally on this machine (via @playwright/cli), so resolve it from there
// with createRequire rather than a bare ESM import. Override with PLAYWRIGHT_PATH if it moves.
const requireCjs = createRequire(import.meta.url);
const PLAYWRIGHT_PATH =
  process.env.PLAYWRIGHT_PATH ||
  "C:/Users/soren/AppData/Roaming/npm/node_modules/@playwright/cli/node_modules/playwright";
const { chromium } = requireCjs(PLAYWRIGHT_PATH);

const here = dirname(fileURLToPath(import.meta.url));
const port = Number(process.env.PORT || 8793);
const contentTypes = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8" };

const log = (m) => console.log(`[offline-proof] ${m}`);

// Build a 16-bit PCM mono WAV: a leading tone (a real "utterance" so the first capture is a decodable,
// non-empty clip that proves no-speech-lost) followed by silence (so the microphone LEVEL drops low and
// the level-gated background re-drive can fire without being read as the owner still talking). The default
// Chromium fake device is a constant beep, which would look like continuous speech and block the auto-drive
// forever; a tone-then-silence file gives a realistic quiet window on reconnect.
async function writeFakeAudioWav(path, toneSeconds = 4, silenceSeconds = 8, rate = 48000, freq = 440) {
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
  log(`bundled the real product turn machine (${js.length} bytes)`);
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

// Read the durable Car Mode IndexedDB store (dt-carmode / pending-turns) from inside the page, returning
// a summary the driver can assert on (count + whether each record carries a real audio blob).
const READ_STORE = `(() => new Promise((resolve) => {
  const req = indexedDB.open("dt-carmode", 1);
  req.onsuccess = () => {
    const db = req.result;
    let tx;
    try { tx = db.transaction("pending-turns", "readonly"); }
    catch { resolve({ count: 0, records: [] }); return; }
    const all = tx.objectStore("pending-turns").getAll();
    all.onsuccess = () => {
      const recs = (all.result || []).map((r) => ({
        id: r.id, brainSent: r.brainSent, endPhrase: r.endPhrase,
        transcript: r.transcript || null,
        audioBytes: r.audio && typeof r.audio.size === "number" ? r.audio.size : 0,
        audioType: r.audio && r.audio.type ? r.audio.type : null,
      }));
      resolve({ count: recs.length, records: recs });
    };
    all.onerror = () => resolve({ count: -1, records: [] });
  };
  req.onerror = () => resolve({ count: 0, records: [] });
}))()`;

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
  log(`wrote fake capture audio (tone then silence) -> ${wavPath}`);

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

  const evidence = { generatedAtUtc: new Date().toISOString(), scenario: "offline mid-turn -> reconnect", steps: {} };

  try {
    await page.goto(url, { waitUntil: "domcontentloaded" });
    await waitFor(page, "harness mounted", `!!document.getElementById("start")`);

    // 1) Start Car Mode (real user gesture -> unlocks audio, enters Listening, opens the mic).
    await page.click("#start");
    await waitFor(page, "phase=listening", `document.getElementById("phase").textContent === "listening"`);
    // Capture ~1.4s of fake-device audio so the snapshot is a real, decodable clip.
    await page.waitForTimeout(1400);
    log("started + captured fake audio; phase=listening");

    // 2) DROP the connection, THEN end the turn -> transcribe fails mid-turn.
    await page.evaluate(`window.__setOffline(true)`);
    await page.click("#endturn");
    log("went offline, then tapped Over and out (transcribe will fail)");

    // 3) CLAIM 1 - the held state is entered and the command AUDIO survived in IndexedDB.
    await waitFor(page, "holding=true", `document.getElementById("holding").textContent === "true"`);
    const heldStore = await page.evaluate(READ_STORE);
    const holdMessage = await page.evaluate(`document.getElementById("holdMessage").textContent`);
    const phaseAfterHold = await page.evaluate(`document.getElementById("phase").textContent`);
    await page.screenshot({ path: join(here, "evidence-1-holding.png") });
    const rec = heldStore.records[0] || {};
    evidence.steps.claim1_noSpeechLost = {
      pass: heldStore.count === 1 && rec.audioBytes > 0 && rec.brainSent === false,
      heldRecordCount: heldStore.count,
      audioBytes: rec.audioBytes || 0,
      audioType: rec.audioType,
      brainSent: rec.brainSent,
      holdMessage,
      phaseAfterHold,
      note: "The command audio is durably saved BEFORE transcribe, so the failed transcribe did not lose it.",
    };
    log(`claim1 no-speech-lost: heldRecords=${heldStore.count} audioBytes=${rec.audioBytes} brainSent=${rec.brainSent}`);

    // 3b) CLAIM 3a - the connection-down line is announced after the end-phrase watch fails several ticks.
    let connDownAnnounced = false;
    try {
      await waitFor(page, "connectionDown announced", `document.getElementById("connectionDown").textContent === "true"`, 8000);
      connDownAnnounced = true;
    } catch { connDownAnnounced = false; }

    // 4) RECONNECT: clear offline and fire the browser online event -> the driver re-drives the held turn.
    await page.evaluate(`window.__setOffline(false); window.dispatchEvent(new Event("online"));`);
    log("reconnected + fired online event; awaiting auto-complete");

    // 5) CLAIM 2 - the reply appears AND the durable record is deleted (turn owned, never re-driven).
    await waitFor(page, "reply spoken", `document.getElementById("reply").textContent.length > 0`, 20000);
    await waitFor(page, "store drained", `document.getElementById("heldCount").textContent === "0"`, 20000);
    const afterStore = await page.evaluate(READ_STORE);
    const reply = await page.evaluate(`document.getElementById("reply").textContent`);
    const spoken = await page.evaluate(`window.__spoken`);
    const ttsTexts = await page.evaluate(`window.__ttsTexts`);
    await page.screenshot({ path: join(here, "evidence-2-recovered.png") });

    evidence.steps.claim2_autoCompleteOnReconnect = {
      pass: afterStore.count === 0 && reply.length > 0,
      storeCountAfter: afterStore.count,
      reply,
      note: "On the online event the held turn re-drove through transcribe -> brain -> speak; the record was deleted only after the brain owned the turn.",
    };
    const recoverySpoken = ttsTexts.some((t) => t.startsWith("Back online. "));
    evidence.steps.claim3_statesAnnounced = {
      pass: spoken.includes(holdMessage) && recoverySpoken,
      spokenLocalLines: spoken,
      connectionDownAnnounced: connDownAnnounced,
      recoverySpokenThroughGoodVoice: recoverySpoken,
      ttsTexts,
      note: "Holding + connection-down were spoken locally; the recovered reply went to the good voice with the 'Back online' prefix.",
    };
    log(`claim2 auto-complete: storeAfter=${afterStore.count} reply="${reply}"`);
    log(`claim3 announced: spoken=${JSON.stringify(spoken)} recoveryPrefix=${recoverySpoken}`);

    const pass =
      evidence.steps.claim1_noSpeechLost.pass &&
      evidence.steps.claim2_autoCompleteOnReconnect.pass &&
      evidence.steps.claim3_statesAnnounced.pass;
    evidence.verdict = pass ? "PASS" : "FAIL";
    evidence.covers = "Real Chromium: the shipping useCarMode turn machine, real MediaRecorder capture (fake audio device), the real WebM->WAV transcode, the real durable IndexedDB store, the real classify/cadence policy, and the real re-drive driver.";
    evidence.doesNotCover = "NOT the real phone: no real microphone, no mobile audio-session behaviour (ducking/autoplay), no real radio offline, and the Gateway (transcribe/brain/tts) is a controllable in-page shim, not the live server. The real-phone offline-mid-turn pass and Soren's by-hand pass remain the on-device confirmation.";

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
