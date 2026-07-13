import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { useCarMode, type CarModeReply } from "@devthrottle/client-core/carmode/useCarMode";
import { carModeTurn } from "@devthrottle/client-core/carmode/carModeApi";
import { getAiProvider } from "@devthrottle/client-core/api/ai";
import { useScreenWakeLock } from "../hooks/useScreenWakeLock";

// The Car Mode page version, shown small in the header corner so the owner can confirm at a glance he is
// on the latest page, not an old cached bundle. This is a SIMPLE hand-bumped label (Soren's explicit ask):
// the old git short-sha + timestamp badge was replaced by a plain human version. BUMP THIS INTEGER BY HAND
// ON EVERY DEPLOY of the mobile app (v1 -> v2 -> v3 ...), so a glance at the corner tells the owner and the
// Architect exactly which page is live and what to look for after a deploy.
const CAR_MODE_VERSION = "v9";

// Car Mode (Car Mode mission): the standalone, chrome-less, full-screen page the owner opens to run
// the whole fleet by voice, hands-free, phone in his pocket. It is a THIN view over the shared
// useCarMode() turn-taking machine (decision 6) - all the state, capture, transcription, playback, and
// the two audible cues live in client-core; this page owns only the route, a big eyes-free layout, and
// the choice of brain responder.
//
// Phase 1 wires the turn-taking end to end with a STAND-IN reply so the hardest unknown - the
// walkie-talkie discipline and barge-in - is proven before the fleet brain is built on top of it. From
// Phase 2 the responder is the real POST /carmode/turn brain. The page picks which by the flag below;
// nothing else changes.

// Phase 2 is merged, so Car Mode talks to the real fleet brain (POST /carmode/turn). The Phase 1
// stand-in responder is kept just below, guarded by this flag, purely so the turn-taking machine can be
// exercised with no brain (and no credits) when diagnosing barge-in in isolation.
const USE_FLEET_BRAIN = true;

export function CarMode() {
  // Car Mode is eyes-free with the phone in a pocket: keep the screen awake the whole time (mission
  // Phase 1: a standalone page under /m with a screen wake-lock).
  useScreenWakeLock();

  // v5 - PIN THE SCREEN HEIGHT TO THE ACTUALLY-VISIBLE VIEWPORT. On the owner's Android PWA, CSS `dvh`
  // proved unreliable: the fixed .car-screen stayed taller than the visible area, so the footer's primary
  // button rendered BELOW the phone's bottom browser toolbar (cut off), in both idle and active states.
  // window.visualViewport.height is the true visible height (it excludes the toolbars and the keyboard), so
  // we publish it into the `--car-vh` CSS variable and the stylesheet sizes .car-screen to it (with a `dvh`
  // fallback for the first paint / browsers without visualViewport). We refresh on every visualViewport
  // resize/scroll and on orientation change, so it re-fits as the toolbars show and hide.
  useEffect(() => {
    const vv = window.visualViewport;
    const root = document.documentElement;
    const apply = () => {
      const height = vv !== null ? vv.height : window.innerHeight;
      root.style.setProperty("--car-vh", `${Math.round(height)}px`);
    };
    apply();
    vv?.addEventListener("resize", apply);
    vv?.addEventListener("scroll", apply);
    window.addEventListener("resize", apply);
    window.addEventListener("orientationchange", apply);
    return () => {
      vv?.removeEventListener("resize", apply);
      vv?.removeEventListener("scroll", apply);
      window.removeEventListener("resize", apply);
      window.removeEventListener("orientationchange", apply);
      root.style.removeProperty("--car-vh");
    };
  }, []);

  // The injected brain responder. Phase 2+: the real fleet brain. The stand-in (below) just echoes the
  // heard command so Phase 1's barge-in proof needs no server and no credits.
  const respond = useCallback(async (command: string, signal: AbortSignal): Promise<CarModeReply> => {
    if (USE_FLEET_BRAIN) {
      const result = await carModeTurn(command, signal);
      return {
        spoken: result.spoken,
        actions: result.actions,
        pendingConfirmation: result.pendingConfirmation,
        turnId: result.turnId,
        timing: result.timing,
      };
    }
    // Phase 1 stand-in: a canned acknowledgement that repeats the command back, so the owner can hear
    // the whole loop (transcribe -> speak -> interrupt) working before the brain exists.
    return { spoken: `You said: ${command}. Go ahead when you're ready.` };
  }, []);

  // The owner's configured hands-free sign-off phrase (set in the Cockpit "Car Mode" settings tab, a Gateway
  // setting so it reaches this phone). Start from the cached value for an instant, offline-safe default, then
  // sync the authoritative value from the Gateway. Default "over and out".
  const [endPhrase, setEndPhrase] = useState(() => localStorage.getItem("carmode.endphrase") || "over and out");
  useEffect(() => {
    let cancelled = false;
    void getAiProvider()
      .then((p) => {
        if (cancelled || !p.carModeEndPhrase) return;
        setEndPhrase(p.carModeEndPhrase);
        localStorage.setItem("carmode.endphrase", p.carModeEndPhrase);
      })
      .catch(() => {
        // Offline or Gateway unreachable: keep the cached phrase so Car Mode still ends turns.
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const {
    phase,
    started,
    transcript,
    reply,
    actions,
    pendingConfirmation,
    error,
    unsupported,
    holding,
    heldCount,
    holdMessage,
    askOwnerTurn,
    connectionDown,
    sendHeldTurn,
    discardHeldTurn,
    getMicLevel,
    endTurn,
    interrupt,
    start,
    stop,
  } = useCarMode({ respond, endPhrase });

  const statusText = phaseStatus(phase);

  return (
    <div className="car-screen">
      <header className="car-bar">
        <Link className="car-exit" to="/" onClick={stop} aria-label="Leave Car Mode">
          Exit
        </Link>
        <span className="car-title">Car Mode</span>
        {/* The simple, hand-bumped version in the corner: the owner glances here to confirm he is on the
            latest page, not an old cached bundle. Bumped by hand on every deploy (see CAR_MODE_VERSION). */}
        <span className="car-build">{CAR_MODE_VERSION}</span>
      </header>

      {/* The scrollable middle: everything between the fixed header and the fixed control footer. It flexes
          to fill the viewport and scrolls internally ONLY if its content is taller than the space, so the
          End Car Mode button and the controls in the footer are ALWAYS visible with no page scroll. */}
      <div className="car-middle">
        {error !== null && (
          <div className="car-error" role="alert">
            {error}
          </div>
        )}

        {unsupported && (
          <div className="car-error" role="alert">
            Car Mode needs Chrome or another Chromium browser for hands-free voice. Open this page in
            Chrome.
          </div>
        )}

        {/* Offline resilience (Phase 4a, #1427). The connection-down notice: the hands-free watch cannot
            reach the fleet, but the owner's audio keeps recording locally, so nothing is lost. */}
        {connectionDown && (
          <div className="car-conn-down" role="status">
            Connection down - I'll keep trying. Your recording is still being saved.
          </div>
        )}

        {/* The calm "saved, will send" holding banner: never a red error, because the speech is SAVED and
            auto-sends when the connection returns. Shows how many requests are waiting. */}
        {holding && (
          <div className="car-holding" role="status">
            <span className="car-holding-text">
              {holdMessage ?? "Saved - I'll send your request the moment we're back online."}
            </span>
            {heldCount > 1 && <span className="car-holding-count">{heldCount} requests saved</span>}
          </div>
        )}

        {/* A held turn that needs the owner's explicit choice. A "stale" turn (never sent to the brain,
            just old) can be sent safely; an "ambiguous" turn (already sent, result unknown) can only be
            discarded in Phase 4a, since a blind resend could act twice. */}
        {askOwnerTurn && (
          <div className="car-askowner" role="status">
            <span className="car-askowner-text">
              {askOwnerTurn.reason === "ambiguous"
                ? "I sent an earlier request but couldn't confirm it. I've left it alone to avoid doing it twice."
                : "You have an earlier saved request from a while ago."}
              {askOwnerTurn.transcript.length > 0 && <> "{askOwnerTurn.transcript}"</>}
            </span>
            <div className="car-askowner-buttons">
              {askOwnerTurn.reason === "stale" && (
                <button
                  type="button"
                  className="car-askowner-send"
                  onClick={() => sendHeldTurn(askOwnerTurn.id)}
                  disabled={phase !== "listening"}
                >
                  Send it now
                </button>
              )}
              <button
                type="button"
                className="car-askowner-discard"
                onClick={() => discardHeldTurn(askOwnerTurn.id)}
              >
                Discard
              </button>
            </div>
          </div>
        )}

        <main className="car-body">
          {/* The one big status orb + word: whose turn it is, readable at a glance / audible by the two
              cues. Its color is the whole state at a distance. */}
          <div className={`car-orb car-orb-${phase}`} aria-hidden="true">
            <span className="car-orb-pulse" />
          </div>
          <p className="car-status" aria-live="polite">
            {started ? statusText : "Tap Start, then just talk."}
          </p>

          {/* A small link to the End Word Test harness so the owner can reach it without remembering the
              URL (his ask). Shown only on the idle screen; it is a tuning tool, not part of a live turn. */}
          {!started && (
            <Link
              to="/endword"
              style={{ marginTop: 16, fontSize: 14, color: "#8ab4f8", textDecoration: "underline" }}
            >
              Open the End Word Test
            </Link>
          )}

          {/* Live microphone level meter (Architect direction): visibly shows the owner his voice is being
              picked up. Reads the capture stream's AnalyserNode via getMicLevel on an animation frame. It
              only moves while the microphone is actually capturing (the Listening phase); flat otherwise. */}
          {started && <MicMeter getLevel={getMicLevel} active={phase === "listening"} />}

          {pendingConfirmation && (
            <p className="car-confirm" role="status">
              Waiting for you to say "confirm" - or say "cancel".
            </p>
          )}

          {/* The heard command and the spoken reply, for eyes-on confirmation. */}
          {transcript.length > 0 && (
            <div className="car-heard">
              <span className="car-label">You said</span>
              <span className="car-heard-text">{transcript}</span>
            </div>
          )}
          {reply.length > 0 && (
            <div className="car-said">
              <span className="car-label">Assistant</span>
              <span className="car-said-text">{reply}</span>
            </div>
          )}
          {actions.length > 0 && (
            <ul className="car-actions">
              {actions.map((a, i) => (
                <li key={`${a.tool}-${i}`} className="car-action">
                  {a.summary}
                </li>
              ))}
            </ul>
          )}
        </main>
      </div>

      <footer className="car-foot">
        {!started ? (
          <button type="button" className="car-start" onClick={() => void start()} disabled={unsupported}>
            Start Car Mode
          </button>
        ) : (
          <>
            <p className="car-hint">
              Talk, then say (or tap) <strong>Over and out</strong> to send. Tap <strong>Stop</strong> to cut me off.
            </p>
            {/* Touch equivalents of the two spoken control phrases (Architect direction: the app must be
                fully usable by touch, so testing is never blocked by the spoken "over and out"). Each
                runs the EXACT same code path as the phrase. "Over and out" is the primary action, live
                only while Listening; "Stop" only cuts off while Speaking - matching the voice rules. */}
            <div className="car-touch">
              <button
                type="button"
                className="car-overandout"
                onClick={endTurn}
                disabled={phase !== "listening"}
              >
                Over and out
              </button>
              <button
                type="button"
                className="car-interrupt"
                onClick={interrupt}
                disabled={phase !== "speaking"}
              >
                Stop
              </button>
            </div>
            <button type="button" className="car-stop" onClick={stop}>
              End Car Mode
            </button>
          </>
        )}
      </footer>
    </div>
  );
}

// The live microphone level meter: a row of bars whose heights follow the input level, so the owner can
// SEE his voice is picked up (Architect direction). It runs its own animation-frame loop reading the
// polled level getter and holds the bar heights in local state, so only the meter re-renders each frame,
// never the whole Car Mode page. The centre bars react most, so it reads as an equalizer (the same shape
// as the dictation meter). When not capturing (getLevel returns 0, or between turns) the bars rest flat.
const METER_BAR_COUNT = 11;

function MicMeter({ getLevel, active }: { getLevel: () => number; active: boolean }) {
  const [levels, setLevels] = useState<number[]>(() => new Array(METER_BAR_COUNT).fill(0));
  // Read `active` from a ref inside the animation loop so the long-lived loop is not re-created each time
  // the phase flips; the loop itself is started once and torn down on unmount.
  const activeRef = useRef(active);
  activeRef.current = active;

  useEffect(() => {
    let raf = 0;
    const tick = () => {
      const level = activeRef.current ? getLevel() : 0;
      const centre = (METER_BAR_COUNT - 1) / 2;
      setLevels(
        new Array(METER_BAR_COUNT).fill(0).map((_, i) => {
          const falloff = 1 - Math.abs(i - centre) / (centre + 1);
          return Math.min(1, level * (0.55 + falloff));
        }),
      );
      raf = window.requestAnimationFrame(tick);
    };
    raf = window.requestAnimationFrame(tick);
    return () => window.cancelAnimationFrame(raf);
  }, [getLevel]);

  return (
    <div className={`car-meter ${active ? "car-meter-active" : ""}`} aria-hidden="true">
      {levels.map((v, i) => (
        <span key={i} className="car-meter-well">
          <span className="car-meter-bar" style={{ height: `${8 + v * 84}%` }} />
        </span>
      ))}
    </div>
  );
}

// The plain-English status line for each phase, the words that pair with the color orb and the cues.
function phaseStatus(phase: string): string {
  switch (phase) {
    case "listening":
      return "Listening - go ahead, then tap 'Over and out' to send.";
    case "thinking":
      return "Got it. Working on that...";
    case "speaking":
      return "Speaking. Tap 'Stop' to cut me off.";
    default:
      return "Ready.";
  }
}
