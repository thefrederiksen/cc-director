// Cockpit snooze menu proof harness: mounts the REAL Cockpit SessionMenu against a fake Gateway.
//
// What is REAL: the shipping SessionMenu component, the shipping useSnoozeOptions shared cache, the
// shipping buildSnoozeMenu decision, and the shipping holdSession client call - the whole path a click
// takes in the browser.
//
// What is SIMULATED: the Gateway. A fetch shim answers GET /gateway/snooze-presets from an in-page value
// and records every POST /sessions/{sid}/hold body, so the driver can assert what the click actually
// sent. It applies the hold to its session state and the page re-renders from it, exactly as the real
// roster would after the Gateway confirmed.
//
// This proves the CLIENT flow against shipping code. The Gateway's own storage and timer are proven
// separately: in C# by the Gateway suite, and live against a real Gateway + Director for the desktop.
import { useState } from "react";
import { createRoot } from "react-dom/client";
import { SessionMenu } from "../../../../apps/cockpit/src/sessions/SessionMenu";
import type { SessionDto } from "../../src/api/client";

declare global {
  interface Window {
    __holds: Array<{ onHold: boolean; snoozeMinutes?: number }>;
    __presets: { presets: number[]; defaultMinutes: number; maxPresets: number };
    __presetFetches: number;
    __setOnHold: (v: boolean) => void;
  }
}

window.__holds = [];
window.__presetFetches = 0;
window.__presets = { presets: [15, 60, 240, 480], defaultMinutes: 60, maxPresets: 5 };

const realFetch = window.fetch.bind(window);

window.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
  const method = (init?.method ?? "GET").toUpperCase();

  if (url.includes("/gateway/snooze-presets")) {
    window.__presetFetches++;
    return new Response(JSON.stringify(window.__presets), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  }

  if (url.includes("/hold") && method === "POST") {
    const body = JSON.parse(String(init?.body ?? "{}")) as { onHold: boolean; snoozeMinutes?: number };
    window.__holds.push(body);
    window.__setOnHold(body.onHold);
    return new Response(JSON.stringify({ onHold: body.onHold }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  }

  return realFetch(input as RequestInfo, init);
}) as typeof window.fetch;

function Harness() {
  const [onHold, setOnHold] = useState(false);
  window.__setOnHold = setOnHold;
  // Three cards, like a rail: proves every menu shares ONE presets fetch rather than one each.
  const sessions: SessionDto[] = [1, 2, 3].map((n) => ({
    sessionId: `sid-${n}`,
    name: `session ${n}`,
    onHold: n === 1 ? onHold : false,
  } as SessionDto));

  return (
    <div style={{ display: "flex", gap: 24, padding: 16 }}>
      {sessions.map((s) => (
        <div key={s.sessionId} className="roster-li" style={{ position: "relative", width: 200, height: 60,
          border: "1px solid #30363d", borderRadius: 8, padding: 8 }}>
          <div style={{ fontSize: 13 }}>{s.name}{s.onHold ? " - Snoozed" : ""}</div>
          <SessionMenu session={s} variant="rail" />
        </div>
      ))}
    </div>
  );
}

createRoot(document.getElementById("root")!).render(<Harness />);
