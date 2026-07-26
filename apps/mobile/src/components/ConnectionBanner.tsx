import React, { useEffect, useRef, useState } from "react";
import { useConnectionHealth } from "@devthrottle/client-core/connection/health";
import { useNetStatus } from "@devthrottle/client-core/net/useNetStatus";
import { useNow } from "@devthrottle/client-core/sessions/waiting";

// The ONE global network indicator for the whole mobile app (mission: never clear good data just
// because the connection is bad). Mounted once in GatedLayout, so it sits above every gated screen -
// the roster, Chat, Voice, the terminal - and is the single voice for "the network is bad, what you
// see is the last known information." No page carries its own offline strip; they keep their content
// and this banner is the one explanation.
//
// IT SAYS NOTHING WHEN THE CONNECTION IS FINE. A working connection is the normal case and needs no
// words: an indicator that is on screen every second of every day is not information, it is furniture
// that other things have to move out of the way for. The phone used to carry a second, always-on
// "Connected" pill as well (components/StatusPill.tsx, deleted 2026-07-26), and that pill spent its
// whole life fighting controls for a corner - it landed on the roster's filter button, on the session
// screens' overflow menu button, on the Assistant's Chat/Voice toggle, and finally on the voice-mode
// banner's "Turn off" button, which is what killed it. Everything it had to say when the connection
// was good was "good". Everything it has to say when it is not is said here, once, in plain words.
//
// It reads two signals and renders whichever is worse:
//
//   * connection health (fed at the api/client.ts transport choke points) - can we reach the Gateway
//     at all. Bad: the strip names it and, once the last good contact is more than a few seconds old,
//     says HOW stale things are ("updated 40s ago") - the honesty mechanism, so nothing on screen
//     pretends to be live when it is not.
//   * the Gateway's connection verdict (GET /diag/network, useNetStatus) - the quality of the path we
//     do have. Only its RED level ("Slow" - relaying through a distant server) is worth a banner;
//     green is the normal case and amber/grey are the transient warm-up and checking states, which
//     resolve on their own within a poll or two and must never nag. The Gateway owns that ruling and
//     its words are rendered verbatim - this client never decides what a level means.
//
// Either way it clears itself the moment the connection is good again.

// Below this the age is too small to be worth naming; the banner just says the connection is bad.
const STALE_THRESHOLD_MS = 3000;

// The connection must stay bad this long before the banner appears (mobile-resilience mission, Phase 4).
// One lost packet - a single failed poll that the next tick recovers, or a terminal stream reconnect
// blipping while plain polls still succeed - flips the health signal bad then good in well under this
// window, so the banner never flashes for it. A genuine outage stays bad past the window and shows.
// Recovery is NOT debounced: the banner hides the instant the connection is good again.
const BAD_DEBOUNCE_MS = 2500;

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
  const net = useNetStatus();
  const barRef = useRef<HTMLDivElement | null>(null);
  // Debounced visibility: the banner shows only after the connection has been bad for BAD_DEBOUNCE_MS,
  // so a one-packet blip never flashes it; it hides immediately on recovery. The health store emits only
  // on an actual good<->bad flip, so this effect runs once per real transition, not per poll.
  const [unreachable, setUnreachable] = useState(false);
  useEffect(() => {
    if (health.state === "good") {
      setUnreachable(false);
      return;
    }
    const timer = window.setTimeout(() => setUnreachable(true), BAD_DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [health.state]);

  // Unreachable beats slow: if we cannot reach the Gateway at all, the quality of the path we would
  // have had is not the thing to say. Only one banner is ever on screen.
  const slow = !unreachable && net.level === "red";
  const shown = unreachable || slow;

  // Tick once a second only while the banner is shown, so the staleness age climbs live. While it is
  // hidden there is nothing to tick - drop to a slow 60s heartbeat rather than running a 1s timer
  // app-wide forever (a small mobile-battery courtesy).
  const now = useNow(shown ? 1000 : 60000);

  // IT PUBLISHES ITS OWN MEASURED HEIGHT AS --connbanner-h, exactly as the voice-mode banner publishes
  // --voicemode-h, and for the same reason: this bar is position:fixed, so it is out of the flow and
  // pushes nothing down by itself - it paints OVER the top of whatever screen is up. Screens read the
  // sum of the two (--topbars-h in styles.css) and give up that height; body reserves it as padding.
  // Without this, a bad connection covered the voice-mode banner's "Turn off" button - the only way out
  // of voice mode - and the session screens' back arrow. Measured, never assumed: the message wraps to
  // two or three lines on a narrow phone. Zero when the banner is not on screen, so nothing about any
  // screen changes in the normal case.
  useEffect(() => {
    const root = document.documentElement;
    if (!shown) {
      root.style.setProperty("--connbanner-h", "0px");
      return;
    }
    const el = barRef.current;
    if (el === null) return;
    const apply = () => root.style.setProperty("--connbanner-h", `${Math.round(el.getBoundingClientRect().height)}px`);
    apply();
    const observer = new ResizeObserver(apply);
    observer.observe(el);
    return () => {
      observer.disconnect();
      root.style.setProperty("--connbanner-h", "0px");
    };
  }, [shown]);

  if (!shown) return null;

  const gap = health.lastGoodContactAt > 0 ? now - health.lastGoodContactAt : 0;
  const showAge = health.lastGoodContactAt > 0 && gap >= STALE_THRESHOLD_MS;

  return (
    <div className="conn-banner" role="status" aria-live="polite" ref={barRef}>
      {slow ? (
        // The Gateway's own verdict on the path we do have. Its label and detail are rendered verbatim.
        <>
          <span className="conn-banner-text">{net.label} connection</span>
          <span className="conn-banner-age">{net.detail}</span>
        </>
      ) : (
        <>
          <span className="conn-banner-text">Bad connection - showing last known information</span>
          {showAge && <span className="conn-banner-age">updated {agoLabel(gap)} ago</span>}
        </>
      )}
    </div>
  );
}
