import { Link } from "react-router-dom";
import { useNetStatus } from "@devthrottle/client-core/net/useNetStatus";
import "./statusPill.css";

// The Cockpit's network status pill (Network Diagnostics mission, Phase 1): the same always-visible
// green/amber/red/grey indicator the phone shows, in the left rail. Colour comes from the authoritative
// direct-vs-relay for this device; tapping it opens the Network Diagnostics view.
export function CockpitStatusPill() {
  const status = useNetStatus();
  return (
    <Link
      to="/network"
      className={`net-pill net-pill-${status.level}`}
      title={status.detail}
      aria-label={`Network: ${status.label}. ${status.detail}`}
    >
      <span className="net-pill-dot" aria-hidden="true" />
      <span className="net-pill-label">Network: {status.label}</span>
    </Link>
  );
}
