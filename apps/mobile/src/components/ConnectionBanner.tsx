import React from "react";
import { useConnectionHealth } from "@devthrottle/client-core/connection/health";
import { useNow } from "@devthrottle/client-core/sessions/waiting";

// The ONE global bad-connection indicator for the whole mobile app (mission: never clear good data
// just because the connection is bad). Mounted once in GatedLayout, so it sits above every gated
// screen - the roster, Chat, Voice, the terminal - and is the single voice for "the network is bad,
// what you see is the last known information." No page carries its own offline strip; they keep their
// content and this banner is the one explanation.
//
// It reads the shared connection-health signal (fed at the api/client.ts transport choke points) and
// renders nothing while the connection is good. When it goes bad it pins a slim strip at the very top
// of the screen and, once the last good contact is more than a few seconds old, says HOW stale things
// are ("updated 40s ago") - the honesty mechanism, so nothing on screen pretends to be live when it is
// not. It clears itself the moment the next contact succeeds.

// Below this the age is too small to be worth naming; the banner just says the connection is bad.
const STALE_THRESHOLD_MS = 3000;

// A compact "how long ago" label with seconds granularity (the mission's "updated 40s ago"), climbing
// to minutes and hours for a longer outage: "40s" -> "3m" -> "1h 4m".
function agoLabel(gapMs: number): string {
  const seconds = Math.floor(gapMs / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  const remMinutes = minutes % 60;
  return remMinutes > 0 ? `${hours}h ${remMinutes}m` : `${hours}h`;
}

export function ConnectionBanner(): React.ReactElement | null {
  const health = useConnectionHealth();
  // Tick once a second while the connection is bad, so the staleness age climbs live. While it is good
  // the banner renders nothing, so there is nothing to tick - drop to a slow 60s heartbeat rather than
  // running a 1s timer app-wide forever (a small mobile-battery courtesy). useNow restarts its interval
  // when the value changes, so the fast tick begins the moment the connection goes bad.
  const now = useNow(health.state === "bad" ? 1000 : 60000);

  if (health.state === "good") return null;

  const gap = health.lastGoodContactAt > 0 ? now - health.lastGoodContactAt : 0;
  const showAge = health.lastGoodContactAt > 0 && gap >= STALE_THRESHOLD_MS;

  return (
    <div className="conn-banner" role="status" aria-live="polite">
      <span className="conn-banner-text">Bad connection - showing last known information</span>
      {showAge && <span className="conn-banner-age">updated {agoLabel(gap)} ago</span>}
    </div>
  );
}
