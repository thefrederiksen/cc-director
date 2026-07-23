import { useEffect, useState } from "react";
import { getNetworkDiag } from "../api/client";
import type { NetStatus } from "./netStatus";

// Auto-run-on-open network status for the header pill (Network Diagnostics mission, Phase 1), shared by the
// phone and the Cockpit so the two pills can never drift. The Gateway owns the finished ruling returned by
// GET /diag/network; this hook renders that object verbatim and never derives a label from diagnostic fields.
// It re-polls on a slow cadence and never runs the heavy download or upload throughput test.

const POLL_MS = 30000;

export function useNetStatus(): NetStatus {
  const [status, setStatus] = useState<NetStatus>({ level: "grey", label: "Checking", detail: "Checking your connection..." });

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    async function poll() {
      try {
        const network = await getNetworkDiag();
        if (cancelled) return;
        setStatus(network.connectionVerdict);
      } catch {
        // A failed quality diagnostic supplies no new Gateway ruling. Keep the prior Gateway verdict, or the
        // initial Checking state, rather than inventing an Offline claim while roster traffic may be healthy.
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
