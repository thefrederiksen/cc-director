// Snooze lengths proof harness: mounts the REAL SnoozeCard against a fake Gateway.
//
// The card moved into client-core when Settings was unified across the Cockpit and the phone, so this
// harness now proves the one component BOTH surfaces render, not the desktop's copy of it.
//
// What is REAL: the shipping SnoozeCard component, the shipping settingsClient (getGatewaySettings /
// setSnoozePresets), and the shipping snoozeFormat helpers - the whole client path a user's click takes.
//
// What is SIMULATED: the Gateway. A fetch shim answers GET /gateway/settings from an in-page list and
// records every PUT /gateway/snooze-presets body, so the driver can assert what the click actually sent.
// The shim applies each PUT to its state and answers the next GET from it, exactly as a real Gateway
// would, so the card re-renders from the server's truth rather than from local optimism.
//
// This proves the CLIENT list/default flow against shipping code. The Gateway's own validation is proven
// separately by the C# tests (SnoozePresetsConfigTests + the Gateway end-to-end suite).
import { createRoot } from "react-dom/client";
import { SnoozeCard } from "../../src/settings/NotificationsTab";

declare global {
  interface Window {
    __puts: Array<{ presets: number[]; defaultMinutes: number }>;
    __state: { presets: number[]; defaultMinutes: number };
  }
}

// The Gateway's stored truth, starting at the shipped lengths with the shipped default.
window.__state = { presets: [15, 60, 240, 480], defaultMinutes: 60 };
window.__puts = [];

const realFetch = window.fetch.bind(window);

window.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
  const method = (init?.method ?? "GET").toUpperCase();

  if (url.includes("/gateway/settings") && method === "GET") {
    // The per-account settings snapshot (issue #2022): machine diagnostics moved to the About page and the
    // vestigial global fields (training capture, telemetry consent) are gone, so the snapshot is purely the
    // account settings the collapsed page renders.
    return new Response(
      JSON.stringify({
        snoozeDefaultMinutes: window.__state.defaultMinutes,
        snoozePresets: window.__state.presets,
        snoozeMaxPresets: 5,
        timeZone: "UTC",
        timeZoneMachineDefault: "UTC",
      }),
      { status: 200, headers: { "content-type": "application/json" } },
    );
  }

  if (url.includes("/gateway/snooze-presets") && method === "PUT") {
    const body = JSON.parse(String(init?.body ?? "{}")) as { presets: number[]; defaultMinutes: number };
    window.__puts.push(body);
    // Answer like the Gateway: store it, and hand back the stored (ascending) list.
    window.__state = { presets: [...body.presets].sort((a, b) => a - b), defaultMinutes: body.defaultMinutes };
    return new Response(
      JSON.stringify({ presets: window.__state.presets, defaultMinutes: window.__state.defaultMinutes, maxPresets: 5 }),
      { status: 200, headers: { "content-type": "application/json" } },
    );
  }

  return realFetch(input as RequestInfo, init);
}) as typeof window.fetch;

createRoot(document.getElementById("root")!).render(<SnoozeCard />);
