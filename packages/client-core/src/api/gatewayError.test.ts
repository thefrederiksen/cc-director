import { describe, it, expect } from "vitest";
import {
  CreditsError,
  GatewayError,
  GATEWAY_UNREACHABLE_MESSAGE,
  gatewayErrorMessage,
  type HostedAiUnavailable,
} from "./client";

// The user-facing error mapper (issue #1028): the Cockpit fleet/telemetry/roster pages must never
// show the internal "METHOD /path failed: NNN" diagnostic the client throws on a non-2xx, nor the
// browser's bare "Failed to fetch". gatewayErrorMessage() collapses every thrown error into one clean
// line. These tests pin the exact mapping the pages rely on.
describe("gatewayErrorMessage", () => {
  it("collapses a raw 'GET /path failed: 503' GatewayError to the friendly unreachable line", () => {
    const msg = gatewayErrorMessage(new GatewayError(503, "GET /directors failed: 503"));

    expect(msg).toBe(GATEWAY_UNREACHABLE_MESSAGE);
    // The bug this fixes: the method, path, and status must not survive into the user-facing string.
    expect(msg).not.toContain("GET /");
    expect(msg).not.toContain("failed:");
    expect(msg).not.toContain("503");
  });

  it("collapses the roster envelope failure to the friendly line", () => {
    const msg = gatewayErrorMessage(
      new GatewayError(503, "GET /sessions?envelope=true failed: 503"),
    );

    expect(msg).toBe(GATEWAY_UNREACHABLE_MESSAGE);
    expect(msg).not.toContain("/sessions");
  });

  it("treats 502 and 504 as unreachable too", () => {
    expect(gatewayErrorMessage(new GatewayError(502, "x failed: 502"))).toBe(GATEWAY_UNREACHABLE_MESSAGE);
    expect(gatewayErrorMessage(new GatewayError(504, "x failed: 504"))).toBe(GATEWAY_UNREACHABLE_MESSAGE);
  });

  it("maps the browser's bare 'Failed to fetch' (backend down) to the friendly line, not the raw string", () => {
    // A dead Gateway rejects fetch() with a TypeError - the cold-start roster leak (issue #1028 #4).
    const msg = gatewayErrorMessage(new TypeError("Failed to fetch"));

    expect(msg).toBe(GATEWAY_UNREACHABLE_MESSAGE);
    expect(msg).not.toContain("Failed to fetch");
  });

  it("passes a 401 re-auth message through verbatim (it is already human copy)", () => {
    const human = "This device is no longer authorized. Please sign in again.";
    expect(gatewayErrorMessage(new GatewayError(401, human))).toBe(human);
  });

  it("passes a 402 CreditsError message through verbatim", () => {
    const info: HostedAiUnavailable = {
      state: "NeedsCredits",
      text: "Voice needs credit. Add credits to turn it on.",
      ctaLabel: "Add credits",
      ctaAction: "OpenBilling",
      ctaUrl: null,
    };
    expect(gatewayErrorMessage(new CreditsError(info))).toBe(info.text);
  });

  it("reports a reachable-but-erroring status by number only, never the raw path", () => {
    const msg = gatewayErrorMessage(new GatewayError(500, "GET /directors/abc/settings failed: 500"));

    expect(msg).toBe("The Gateway rejected the request (error 500).");
    expect(msg).not.toContain("/directors");
    expect(msg).not.toContain("failed:");
  });

  it("falls back to the friendly line for a non-Error throw", () => {
    expect(gatewayErrorMessage("something odd")).toBe(GATEWAY_UNREACHABLE_MESSAGE);
    expect(gatewayErrorMessage(null)).toBe(GATEWAY_UNREACHABLE_MESSAGE);
  });
});
