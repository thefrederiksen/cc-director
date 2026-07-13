import { useEffect, useRef, useState } from "react";
import { getNetworkDiag, getNetDiagEcho } from "../api/client";
import { evaluateNetStatus, isRelayObservation, type NetStatus } from "./netStatus";

// Auto-run-on-open network status for the header pill (Network Diagnostics mission, Phase 1), shared by the
// phone and the Cockpit so the two pills can never drift. Per the Architect's guardrail B this is LIGHT: it
// reads GET /diag/echo (to learn which device the Gateway sees us as) + GET /diag/network (the authoritative
// direct-vs-relay), and NEVER runs the heavy download/upload throughput test. It re-polls on a slow cadence
// so the pill stays current, and threads a consecutive-relay counter so the cold-start window shows AMBER,
// not RED.

const POLL_MS = 30000;

export function useNetStatus(): NetStatus {
  const [status, setStatus] = useState<NetStatus>({ level: "grey", label: "Checking", detail: "Checking your connection..." });
  const relayCount = useRef(0);

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    async function poll() {
      try {
        const [echo, network] = await Promise.all([getNetDiagEcho(), getNetworkDiag()]);
        if (cancelled) return;
        relayCount.current = isRelayObservation(network, echo) ? relayCount.current + 1 : 0;
        setStatus(evaluateNetStatus(network, echo, relayCount.current));
      } catch {
        if (!cancelled) setStatus({ level: "grey", label: "Offline", detail: "Cannot reach the Gateway right now." });
      } finally {
        if (!cancelled) timer = setTimeout(poll, POLL_MS);
      }
    }

    void poll();
    return () => {
      cancelled = true;
      if (timer !== undefined) clearTimeout(timer);
    };
  }, []);

  return status;
}
