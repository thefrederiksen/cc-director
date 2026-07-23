// @vitest-environment jsdom
import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { NetworkDiag } from "@devthrottle/client-core/api/client";

const { getNetDiagEcho, getNetworkDiag } = vi.hoisted(() => ({
  getNetDiagEcho: vi.fn(),
  getNetworkDiag: vi.fn(),
}));

vi.mock("@devthrottle/client-core/api/client", () => ({
  getNetDiagEcho,
  getNetworkDiag,
}));

import { useNetStatus } from "@devthrottle/client-core/net/useNetStatus";

const gatewayVerdict = {
  level: "green",
  label: "Gateway-owned label",
  detail: "Gateway-owned detail.",
} as const;

function diagnosticWithGatewayVerdict(): NetworkDiag {
  return {
    tailscaleAvailable: false,
    backendState: null,
    selfName: null,
    selfTailscaleIp: null,
    udpOk: null,
    mappingVariesByDestIp: null,
    nearestDerp: null,
    peers: [],
    notes: [],
    collectedAt: "2026-07-22T12:00:00Z",
    connectionVerdict: gatewayVerdict,
  };
}

describe("useNetStatus Gateway verdict seam", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getNetDiagEcho.mockResolvedValue({
      clientIp: "203.0.113.42",
      clientPath: "other",
      forwardedFor: "",
      host: "gateway.test.invalid",
      machineName: "HOSTED",
      gatewayLanIp: null,
      gatewayTailnetName: null,
      serverTime: "2026-07-22T12:00:00Z",
    });
  });

  it("renders the successful Gateway verdict verbatim", async () => {
    getNetworkDiag.mockResolvedValue(diagnosticWithGatewayVerdict());

    const { result } = renderHook(() => useNetStatus());

    await waitFor(() => expect(getNetworkDiag).toHaveBeenCalledTimes(1));
    expect(getNetDiagEcho).not.toHaveBeenCalled();
    await waitFor(() => expect(result.current).toEqual(gatewayVerdict));
  });

  it("retains the non-verdict checking state when the diagnostic fails instead of inventing Offline", async () => {
    let rejectDiagnostic: ((reason?: unknown) => void) | undefined;
    getNetworkDiag.mockReturnValue(new Promise((_, reject) => { rejectDiagnostic = reject; }));

    const { result } = renderHook(() => useNetStatus());
    await waitFor(() => expect(getNetworkDiag).toHaveBeenCalledTimes(1));

    await act(async () => {
      rejectDiagnostic?.(new Error("diagnostic timed out while the roster remains healthy"));
      await Promise.resolve();
    });

    expect(result.current).toEqual({
      level: "grey",
      label: "Checking",
      detail: "Checking your connection...",
    });
    expect(result.current.label).not.toBe("Offline");
  });
});
