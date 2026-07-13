import type { NetworkDiag, NetDiagEcho, NetDiagPeer } from "../api/client";

// The status-pill resolver (Network Diagnostics mission, Phase 1). Pure and shared by the phone and the
// Cockpit. Per the Architect's guardrail A, the pill's colour comes from the AUTHORITATIVE direct flag of
// THIS device's self-peer in GET /diag/network - never from the app's speed-test verdict (which cannot
// tell warming-up from broken). And it never flashes RED on the normal cold-start: a just-relaying device
// is AMBER until the relay has PERSISTED across a couple of polls.

export type NetStatusLevel = "green" | "amber" | "red" | "grey";

export interface NetStatus {
  level: NetStatusLevel;
  label: string;
  detail: string;
}

/** How many consecutive relaying observations before AMBER (warming) becomes RED (persistently relaying). */
export const RELAY_POLLS_BEFORE_RED = 2;

// Find the Gateway's view of THIS device in the network diagnostic, matched by the IP the Gateway sees us
// as (echo.clientIp is our tailnet address, which appears as a peer's tailscaleIp).
export function findSelfPeer(network: NetworkDiag | null, echo: NetDiagEcho | null): NetDiagPeer | null {
  if (!network || !echo?.clientIp) return null;
  return network.peers.find((p) => p.tailscaleIp === echo.clientIp) ?? null;
}

/**
 * Resolve the status pill. `consecutiveRelay` is how many polls in a row this device has been observed
 * relaying (the caller threads it, resetting to 0 on any non-relay observation) - it is what keeps the
 * cold-start window AMBER rather than RED.
 */
export function evaluateNetStatus(
  network: NetworkDiag | null,
  echo: NetDiagEcho | null,
  consecutiveRelay = 0,
): NetStatus {
  if (echo === null) return { level: "grey", label: "Checking", detail: "Checking your connection..." };

  // A direct-LAN or local front door is unambiguously good without needing the authoritative peer view.
  if (echo.clientPath === "lan") return { level: "green", label: "Direct LAN", detail: "Straight to the Gateway over your local network." };
  if (echo.clientPath === "local") return { level: "green", label: "Local", detail: "On the Gateway machine." };

  // Tailscale front door: the colour depends on the AUTHORITATIVE direct-vs-relay for this device.
  if (network === null || !network.tailscaleAvailable) {
    return { level: "grey", label: "Checking", detail: "Confirming your Tailscale path..." };
  }
  const self = findSelfPeer(network, echo);
  if (self === null) return { level: "grey", label: "Unknown", detail: "The Gateway does not see this device yet." };

  if (self.direct === true) {
    return { level: "green", label: "Fast", detail: `Direct path over your LAN${self.latencyMs != null ? ` (${Math.round(self.latencyMs)} ms)` : ""}.` };
  }
  if (self.direct === false) {
    // Never RED on the cold-start: AMBER until the relay has persisted across a couple of polls.
    if (consecutiveRelay >= RELAY_POLLS_BEFORE_RED) {
      return { level: "red", label: "Slow", detail: "Relaying through a distant server instead of a direct path." };
    }
    return { level: "amber", label: "Warming up", detail: "Connecting - this speeds up once the direct path forms." };
  }
  // direct === null: not yet confirmed by a ping.
  return { level: "amber", label: "Checking", detail: "Confirming the path..." };
}

/** True when an observation counts as "relaying" for the consecutive-relay counter the caller threads. */
export function isRelayObservation(network: NetworkDiag | null, echo: NetDiagEcho | null): boolean {
  const self = findSelfPeer(network, echo);
  return self?.direct === false;
}
