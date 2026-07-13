import { describe, it, expect } from "vitest";
import { evaluateNetStatus, RELAY_POLLS_BEFORE_RED } from "./netStatus";
import type { NetworkDiag, NetDiagEcho, NetDiagPeer } from "../api/client";

// The status-pill resolver: colour comes from the AUTHORITATIVE self-peer direct flag, and the cold-start
// window is AMBER, never RED (guardrail A).

const PHONE_IP = "100.86.144.11";

function echo(clientPath: string, clientIp: string | null = PHONE_IP): NetDiagEcho {
  return {
    clientIp,
    clientPath,
    forwardedFor: "",
    host: "soren-north.taildb08ed.ts.net",
    machineName: "SOREN_NORTH",
    gatewayLanIp: "192.168.1.18",
    gatewayTailnetName: "soren-north.taildb08ed.ts.net",
    serverTime: "2026-07-13T12:00:00Z",
  };
}

function network(self: Partial<NetDiagPeer> | null): NetworkDiag {
  const peers: NetDiagPeer[] = self
    ? [{ name: "phone", tailscaleIp: PHONE_IP, os: "android", online: true, direct: null, path: null, latencyMs: null, note: null, ...self }]
    : [];
  return {
    tailscaleAvailable: true,
    backendState: "Running",
    selfName: "soren-north",
    selfTailscaleIp: "100.97.80.26",
    udpOk: true,
    mappingVariesByDestIp: false,
    nearestDerp: "Toronto",
    peers,
    notes: [],
    collectedAt: "2026-07-13T12:00:00Z",
  };
}

describe("evaluateNetStatus", () => {
  it("is grey while the echo has not arrived", () => {
    expect(evaluateNetStatus(null, null).level).toBe("grey");
  });

  it("is green on a direct-LAN front door without needing the peer view", () => {
    expect(evaluateNetStatus(null, echo("lan")).level).toBe("green");
  });

  it("is green when the authoritative self-peer is direct", () => {
    const s = evaluateNetStatus(network({ direct: true, path: "192.168.1.15:52091", latencyMs: 44 }), echo("tailscale"));
    expect(s.level).toBe("green");
    expect(s.detail).toContain("44 ms");
  });

  it("is AMBER (not red) when relaying on the cold-start window", () => {
    const s = evaluateNetStatus(network({ direct: false, path: "DERP(tor)" }), echo("tailscale"), 0);
    expect(s.level).toBe("amber");
  });

  it("goes RED only once relaying has PERSISTED", () => {
    const s = evaluateNetStatus(network({ direct: false, path: "DERP(tor)" }), echo("tailscale"), RELAY_POLLS_BEFORE_RED);
    expect(s.level).toBe("red");
  });

  it("is grey when the Gateway does not see this device yet", () => {
    expect(evaluateNetStatus(network(null), echo("tailscale")).level).toBe("grey");
  });

  it("is grey when Tailscale is unavailable", () => {
    const net = { ...network({ direct: true }), tailscaleAvailable: false };
    expect(evaluateNetStatus(net, echo("tailscale")).level).toBe("grey");
  });

  it("is amber while the ping verdict is not yet in (direct null)", () => {
    expect(evaluateNetStatus(network({ direct: null }), echo("tailscale")).level).toBe("amber");
  });
});
