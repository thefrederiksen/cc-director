// Phone Voice-mode split Snooze proof harness: mounts the REAL mobile VoiceMode screen against a fake
// Gateway, at a phone viewport.
//
// What is REAL: the shipping VoiceMode page, its shipping useSessionManage hook (optimistic hold,
// rollback, immediate re-sync), the shipping useSnoozeOptions shared cache, the shipping buildSnoozeMenu
// decision - the same one the Cockpit menu and the desktop rail use - and the shipping holdSession call.
// The whole path a thumb-tap takes in the browser.
//
// What is SIMULATED: the Gateway. A fetch shim answers the roster, the wingman voice poll and
// GET /gateway/snooze-presets from in-page values, and records every POST /sessions/{sid}/hold body so
// the driver can assert what each half of the button actually sent. The Gateway's own snooze storage and
// timer are proven separately in C# (SnoozePresetsConfigTests + the Gateway end-to-end suite); this is
// not the real Gateway.
import { useState } from "react";
import { createRoot } from "react-dom/client";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { VoiceMode } from "../../../../apps/mobile/src/pages/VoiceMode";
import { resetSnoozeOptionsCache } from "../../src/settings/snoozeOptions";

declare global {
  interface Window {
    __holds: Array<{ onHold: boolean; snoozeMinutes?: number }>;
    /** Null = this phone has never successfully read the lengths, the "no caret" case. */
    __presets: { presets: number[]; defaultMinutes: number; maxPresets: number } | null;
    __onHold: boolean;
    __setOnHold: (v: boolean) => void;
    /** Re-enter the voice screen after a snooze bounced the router to the queue. */
    __reload: () => void;
    /** Drop the shared presets cache, so the "never read the lengths" case starts cold. */
    __resetSnoozeCache: () => void;
  }
}

window.__resetSnoozeCache = resetSnoozeOptionsCache;

const SID = "sid-voice-1";

window.__holds = [];
window.__onHold = false;
window.__presets = { presets: [15, 60, 240, 480], defaultMinutes: 60, maxPresets: 5 };

const realFetch = window.fetch.bind(window);

window.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
  const method = (init?.method ?? "GET").toUpperCase();
  const json = (body: unknown, status = 200) =>
    new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

  if (url.includes("/gateway/snooze-presets")) {
    // A phone that has never read the lengths is the null case: the endpoint is unreadable, so the
    // shared cache stays null and the screen must offer NO length picker at all.
    return window.__presets === null ? json({ error: "unavailable" }, 503) : json(window.__presets);
  }

  if (url.includes("/wingman/voice")) {
    return json({ ready: false, spoken: "", reply: "", generatedAt: "" });
  }

  if (url.includes("/hold") && method === "POST") {
    const body = JSON.parse(String(init?.body ?? "{}")) as { onHold: boolean; snoozeMinutes?: number };
    window.__holds.push(body);
    window.__onHold = body.onHold;
    window.__setOnHold(body.onHold);
    return json({ onHold: body.onHold, pending: false });
  }

  if (url.endsWith("/sessions") && method === "GET") {
    return json([
      {
        sessionId: SID,
        name: "voice session",
        onHold: window.__onHold,
        // The Gateway stamps the display verdict; the client renders it and never re-derives it. The
        // client FAILS LOUD on a session missing any of these, so a fake Gateway has to stamp them all -
        // which is the point: this harness cannot quietly answer with something the real one would not.
        triageBucket: window.__onHold ? "onHold" : "needsYou",
        effectiveColor: window.__onHold ? "grey" : "red",
        stateLabel: window.__onHold ? "Snoozed" : "Needs you",
        holdState: window.__onHold ? "Held" : "None",
        voiceMode: true,
      },
    ]);
  }

  return realFetch(input as RequestInfo, init);
}) as typeof window.fetch;

// The queue the screen returns to once a snooze lands. Rendering it as a real route is what proves the
// "and take me back to the list" half of both verbs.
function Queue() {
  return <div id="queue-marker">QUEUE</div>;
}

function Harness() {
  const [, setOnHold] = useState(false);
  // A remount counter, not a navigation: re-entering the screen the way a fresh tap from the queue would,
  // with no leftover router state from the snooze that just bounced us out.
  const [entry, setEntry] = useState(0);
  window.__setOnHold = setOnHold;
  window.__reload = () => setEntry((n) => n + 1);
  return (
    <MemoryRouter key={entry} initialEntries={[`/session/${SID}/voice`]}>
      <Routes>
        <Route path="/" element={<Queue />} />
        <Route path="/session/:sessionId/voice" element={<VoiceMode />} />
      </Routes>
    </MemoryRouter>
  );
}

createRoot(document.getElementById("root")!).render(<Harness />);
