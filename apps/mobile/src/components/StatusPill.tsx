import { Link } from "react-router-dom";
import { useNetStatus } from "@devthrottle/client-core/net/useNetStatus";

// The network status pill (Network Diagnostics mission, Phase 1): a small, always-visible green/amber/red/
// grey dot + label that tells you your connection quality at a glance without opening a page. Its colour
// comes from the authoritative direct-vs-relay for this device (never the speed-test guess), and it never
// flashes red on the normal cold-start. Tapping it opens the full Diagnostics page.
export function StatusPill() {
  const status = useNetStatus();
  return (
    <Link
      to="/diagnostics"
      className={`net-pill net-pill-${status.level}`}
      title={status.detail}
      aria-label={`Network: ${status.label}. ${status.detail}`}
    >
      <span className="net-pill-dot" aria-hidden="true" />
      <span className="net-pill-label">{status.label}</span>
    </Link>
  );
}
