// Car Mode offline-resilience proof harness (mission Phase 4a, issue #1427). This mounts the REAL
// shipping useCarMode() turn-taking machine in a real browser and drives one turn across a simulated
// connection drop, so the resilience path is exercised end to end against the actual product code -
// not a copy. The Playwright driver (run-proof.mjs) reads window.__RESULT__ for the verdict.
//
// What is REAL here: the useCarMode hook, MicRecorder capture (fake media device), the WebM/Opus ->
// WAV transcode (decodeAudioData), the durable IndexedDB command-audio store (pendingTurnStore), the
// classify/cadence policy (turnRetry), the background re-drive driver, and the audible-state calls.
// What is SIMULATED: the Gateway (a controllable in-page fetch shim) and the "offline" condition (the
// shim throws a network error, exactly as a dropped fetch does). This is a desktop Chromium simulation,
// NOT the real phone - stated plainly in the report.

import React, { useCallback } from "react";
import { createRoot } from "react-dom/client";
import { useCarMode, type CarModeReply } from "../../src/carmode/useCarMode";
import { carModeTurn } from "../../src/carmode/carModeApi";

type W = typeof window & {
  __setOffline: (v: boolean) => void;
  __spoken: string[];
  __ttsTexts: string[];
  __view: unknown;
};
const w = window as unknown as W;

// ---- capture the spoken LOCAL announcements (speechSynthesis) so the driver can PROVE the holding /
// connection-down states were announced audibly, without a real voice sounding in headless. ----
const spoken: string[] = [];
w.__spoken = spoken;
try {
  const synth = window.speechSynthesis;
  if (synth) {
    synth.speak = (u: SpeechSynthesisUtterance) => {
      spoken.push(String((u as unknown as { text?: string }).text ?? ""));
    };
    synth.cancel = () => {};
  }
} catch {
  // no speechSynthesis in this browser; the driver falls back to asserting the state flags only
}

// ---- controllable Gateway fetch shim. When offline, Gateway calls throw like a real dropped fetch;
// otherwise they return canned success so the turn can complete. The tts text is recorded so the driver
// can prove the recovered reply was spoken through the good voice WITH the "Back online" prefix. ----
let offline = false;
w.__setOffline = (v: boolean) => {
  offline = v;
};
const ttsTexts: string[] = [];
w.__ttsTexts = ttsTexts;

function okJson(obj: unknown): Response {
  return new Response(JSON.stringify(obj), { status: 200, headers: { "content-type": "application/json" } });
}
// A tiny valid RIFF/WAVE blob (zero samples) so the reply <audio> has something to "play" in headless.
function silentWav(): Blob {
  const bytes = Uint8Array.from(
    atob("UklGRiQAAABXQVZFZm10IBAAAAABAAEAgD4AAAB9AAACABAAZGF0YQAAAAA="),
    (c) => c.charCodeAt(0),
  );
  return new Blob([bytes], { type: "audio/wav" });
}

const realFetch = window.fetch.bind(window);
window.fetch = (async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const url = String(typeof input === "string" ? input : input instanceof URL ? input.href : (input as Request).url);
  const isGateway = url.includes("/wingman/") || url.includes("/carmode/");
  if (offline && isGateway) {
    // Exactly how a real dropped connection surfaces to fetch (TypeError). gatewayFetch reports this as
    // unreachable, and the Car Mode code holds the turn rather than losing it.
    throw new TypeError("Failed to fetch");
  }
  if (url.includes("/wingman/transcribe")) {
    // NOTE: no trailing "over and out", so the hands-free end-phrase watch never auto-ends the turn -
    // the driver ends it explicitly with the button, to control exactly when the drop happens.
    return okJson({ transcript: "how many sessions need me" });
  }
  if (url.includes("/carmode/turn")) {
    return okJson({
      turnId: "proofturn",
      spoken: "Two sessions need you: Local Files Manager and Snooze Length Manager.",
      actions: [],
      pendingConfirmation: false,
      timing: null,
    });
  }
  if (url.includes("/wingman/tts")) {
    try {
      const body = init?.body ? JSON.parse(String(init.body)) : {};
      if (typeof body.text === "string") ttsTexts.push(body.text);
    } catch {
      /* ignore */
    }
    return new Response(silentWav(), { status: 200, headers: { "content-type": "audio/wav" } });
  }
  if (url.includes("/carmode/warmup")) return okJson({ warmed: true });
  if (url.includes("/carmode/telemetry")) return okJson({ recorded: true });
  return realFetch(input, init);
}) as typeof window.fetch;

function Harness(): React.ReactElement {
  const respond = useCallback(async (command: string, signal: AbortSignal): Promise<CarModeReply> => {
    const r = await carModeTurn(command, signal);
    return {
      spoken: r.spoken,
      actions: r.actions,
      pendingConfirmation: r.pendingConfirmation,
      turnId: r.turnId,
      timing: r.timing,
    };
  }, []);

  const v = useCarMode({ respond, endPhrase: "over and out" });
  // Expose the live view to the driver each render, so it can read state without scraping the DOM.
  w.__view = v;

  return (
    <div style={{ fontFamily: "monospace", padding: 16, color: "#fff", background: "#111", minHeight: "100vh" }}>
      <h2>Car Mode offline-resilience proof harness</h2>
      <div>phase: <b id="phase">{v.phase}</b></div>
      <div>holding: <b id="holding">{String(v.holding)}</b></div>
      <div>heldCount: <b id="heldCount">{v.heldCount}</b></div>
      <div>holdMessage: <b id="holdMessage">{v.holdMessage ?? ""}</b></div>
      <div>connectionDown: <b id="connectionDown">{String(v.connectionDown)}</b></div>
      <div>transcript: <b id="transcript">{v.transcript}</b></div>
      <div>reply: <b id="reply">{v.reply}</b></div>
      <div>error: <b id="error">{v.error ?? ""}</b></div>
      <div style={{ marginTop: 12 }}>
        <button id="start" onClick={() => void v.start()}>Start</button>
        <button id="endturn" onClick={v.endTurn}>Over and out</button>
      </div>
    </div>
  );
}

createRoot(document.getElementById("root")!).render(<Harness />);
