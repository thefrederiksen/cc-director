// Car Mode Help Mode proof harness (Car Mode mission, Help Mode - issue #1441). This mounts the REAL
// shipping useCarMode() machine and the REAL getCarModeHelp() client call in a real browser and drives the
// "Help" button, so the Help flow is exercised end to end against the actual product code - not a copy.
// The Playwright driver (run-proof.mjs) reads the exposed state for the verdict.
//
// What is REAL here: the useCarMode hook and its help()/speakHelp path, start() priming the audio inside
// the tap gesture, MicRecorder capture (fake media device), the real getCarModeHelp() fetch + parse, the
// reply <audio> playback, and the two audible cues. What is SIMULATED: the Gateway (a controllable in-page
// fetch shim serving the curated help content and recording what was spoken). This is a desktop Chromium
// simulation, NOT the real phone - stated plainly in the report. The real model's addressing-boundary
// choice is a separate live-model proof; this proves the CLIENT Help flow.

import React, { useCallback, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { useCarMode, type CarModeReply } from "../../src/carmode/useCarMode";
import { carModeTurn, getCarModeHelp, type CarModeCheatSheet } from "../../src/carmode/carModeApi";

// The curated help the shimmed Gateway serves. The driver asserts the page SPEAKS exactly this (proving the
// button reads the server's script verbatim) and RENDERS the cheat-sheet from the same response.
const HELP_SPOKEN =
  "I'm your fleet manager, and you talk to me two ways. By default you command me. To talk to a session, "
  + "start with tell, answer, reply, or message, and name it. Say over and out when you're done.";
const HELP_CHEAT: CarModeCheatSheet = {
  modes: [
    { title: "Command me", hint: "The default.", examples: ["Who needs me?", "Read me the next one", "Snooze it"] },
    { title: "Talk to a session", hint: "Start with tell, answer, reply, or message.", examples: ["Tell the devthrottle session to run the tests"] },
  ],
  endTurn: "Say over and out when you're done.",
  help: "Say help any time.",
};

type W = typeof window & {
  __ttsTexts: string[];
  __helpFetchCount: number;
  __view: unknown;
};
const w = window as unknown as W;

const ttsTexts: string[] = [];
w.__ttsTexts = ttsTexts;
w.__helpFetchCount = 0;

// Silence the LOCAL speechSynthesis so an error path never actually sounds in headless.
try {
  const synth = window.speechSynthesis;
  if (synth) {
    synth.speak = () => {};
    synth.cancel = () => {};
  }
} catch {
  /* no speechSynthesis; fine */
}

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
  if (url.includes("/carmode/help")) {
    w.__helpFetchCount += 1;
    return okJson({ spoken: HELP_SPOKEN, cheatSheet: HELP_CHEAT });
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
  // Not exercised by the Help flow, but present so an accidental turn never hits the network.
  if (url.includes("/wingman/transcribe")) return okJson({ transcript: "" });
  if (url.includes("/carmode/turn")) return okJson({ turnId: "x", spoken: "", actions: [], pendingConfirmation: false, timing: null });
  if (url.includes("/carmode/warmup")) return okJson({ warmed: true });
  if (url.includes("/carmode/telemetry")) return okJson({ recorded: true });
  return realFetch(input, init);
}) as typeof window.fetch;

function Harness(): React.ReactElement {
  const respond = useCallback(async (command: string, signal: AbortSignal, idempotencyKey?: string): Promise<CarModeReply> => {
    const r = await carModeTurn(command, idempotencyKey, signal);
    return { spoken: r.spoken, actions: r.actions, pendingConfirmation: r.pendingConfirmation, turnId: r.turnId, timing: r.timing };
  }, []);

  const v = useCarMode({ respond, endPhrase: "over and out" });
  w.__view = v;

  // The page's own cheat-sheet fetch on mount (the real getCarModeHelp client path).
  const [cheat, setCheat] = useState<CarModeCheatSheet | null>(null);
  useEffect(() => {
    void getCarModeHelp().then((h) => setCheat(h.cheatSheet)).catch(() => {});
  }, []);

  return (
    <div style={{ fontFamily: "monospace", padding: 16, color: "#fff", background: "#111", minHeight: "100vh" }}>
      <h2>Car Mode Help Mode proof harness</h2>
      <div>phase: <b id="phase">{v.phase}</b></div>
      <div>started: <b id="started">{String(v.started)}</b></div>
      <div>reply: <b id="reply">{v.reply}</b></div>
      <div>transcript: <b id="transcript">{v.transcript}</b></div>
      <div>error: <b id="error">{v.error ?? ""}</b></div>
      <div>cheatModes: <b id="cheatModes">{cheat ? cheat.modes.length : 0}</b></div>
      <div>cheatFirstTitle: <b id="cheatFirstTitle">{cheat ? cheat.modes[0]?.title ?? "" : ""}</b></div>
      <div style={{ marginTop: 12 }}>
        <button id="help" onClick={v.help}>Help</button>
      </div>
    </div>
  );
}

createRoot(document.getElementById("root")!).render(<Harness />);
