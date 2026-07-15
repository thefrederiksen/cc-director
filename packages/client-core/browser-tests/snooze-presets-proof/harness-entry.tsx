// Snooze lengths proof harness: mounts the REAL Cockpit SnoozeCard against a fake Gateway.
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
import { SnoozeCard } from "../../../../apps/cockpit/src/settings/SettingsView";

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
    return new Response(
      JSON.stringify({
        version: "proof",
        state: "up",
        port: 7878,
        uptimeSeconds: 1,
        directors: 1,
        mode: "proof",
        addressingMode: "tailscale",
        cockpit: { port: 7879, up: true, url: null },
        autostart: { supported: false, enabled: null },
        wingmanTrainingCapture: false,
        telemetryConsent: true,
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
