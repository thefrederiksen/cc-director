import { Link } from "react-router-dom";
import { useNetStatus } from "@devthrottle/client-core/net/useNetStatus";

// The network status pill (Network Diagnostics mission, Phase 1): a small, always-visible green/amber/red/
// grey dot + label that tells you your connection quality at a glance without opening a page. Its colour
// comes from the authoritative direct-vs-relay for this device (never the speed-test guess), and it never
// flashes red on the normal cold-start. Tapping it opens the full Diagnostics page.
//
// It has two homes, because it is an INDICATOR and must never hold a corner a CONTROL needs:
//
//   * Default (fixed): pinned top-right on the screens that have no title row of their own to give it.
//   * inline: an ordinary flex item, rendered on the session screens' title row (SessionAppBar). The
//     fixed pill used to own the top-right corner of the session screens, which pushed the overflow
//     menu button to the LEFT of the bar - and its menu, anchored right:0, then opened off the left
//     edge of the screen and was unreadable. The indicator was costing the control its corner.
export interface StatusPillProps {
  /** Render as a normal flex item instead of a fixed top-right overlay. */
  inline?: boolean;
}

export function StatusPill({ inline = false }: StatusPillProps) {
  const status = useNetStatus();
  return (
    <Link
      to="/diagnostics"
      className={`net-pill net-pill-${status.level}${inline ? " net-pill-inline" : ""}`}
      title={status.detail}
      aria-label={`Network: ${status.label}. ${status.detail}`}
    >
      <span className="net-pill-dot" aria-hidden="true" />
      <span className="net-pill-label">{status.label}</span>
    </Link>
  );
}
