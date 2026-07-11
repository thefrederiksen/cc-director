import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { useCarMode, type CarModeReply } from "@devthrottle/client-core/carmode/useCarMode";
import { carModeTurn } from "@devthrottle/client-core/carmode/carModeApi";
import { getGatewayHealth, gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { useScreenWakeLock } from "../hooks/useScreenWakeLock";

// The client build id, inlined by Vite's `define` at build time (git short sha + build timestamp; see
// vite.config.ts). Shown on the screen so the owner can confirm at a glance he is on the latest page,
// not an old cached bundle. Vite substitutes this constant in both dev and production builds, so it is
// always a real string in the app; the typeof guard only prevents a ReferenceError if this module is
// ever imported outside a Vite build (for example a unit test), and is not a build-time fallback.
const CLIENT_BUILD_ID = typeof __CLIENT_BUILD_ID__ === "string" ? __CLIENT_BUILD_ID__ : "no-build-id";

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

  // The live Gateway version, read once from /healthz, so the on-screen indicator pairs the CLIENT
  // build id (which page bundle) with the SERVER version (which Gateway build) - together they tell the
  // owner exactly what he is talking to. A read failure shows the specific reason, never a silent blank.
  const [gatewayVersion, setGatewayVersion] = useState("loading...");
  useEffect(() => {
    const controller = new AbortController();
    getGatewayHealth(controller.signal)
      .then((h) => setGatewayVersion(h.version))
      .catch((err) => {
        if (!controller.signal.aborted) setGatewayVersion(gatewayErrorMessage(err));
      });
    return () => controller.abort();
  }, []);

  // The injected brain responder. Phase 2+: the real fleet brain. The stand-in (below) just echoes the
  // heard command so Phase 1's barge-in proof needs no server and no credits.
  const respond = useCallback(async (command: string, signal: AbortSignal): Promise<CarModeReply> => {
    if (USE_FLEET_BRAIN) {
      const result = await carModeTurn(command, signal);
      return { spoken: result.spoken, actions: result.actions, pendingConfirmation: result.pendingConfirmation };
    }
    // Phase 1 stand-in: a canned acknowledgement that repeats the command back, so the owner can hear
    // the whole loop (transcribe -> speak -> interrupt) working before the brain exists.
    return { spoken: `You said: ${command}. Go ahead when you're ready.` };
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
    history,
    liveTranscript,
    recognizerState,
    recognizerError,
    getMicLevel,
    endTurn,
    interrupt,
    start,
    stop,
  } = useCarMode({ respond });

  const statusText = phaseStatus(phase);

  return (
    <div className="car-screen">
      <header className="car-bar">
        <Link className="car-exit" to="/" onClick={stop} aria-label="Leave Car Mode">
          Exit
        </Link>
        <span className="car-title">Car Mode</span>
        {/* The client build id in the corner: the owner glances here to confirm he is on the latest page,
            not an old cached bundle. The full build id + Gateway version are in the debug readout below. */}
        <span className="car-build" title={`Client build ${CLIENT_BUILD_ID}`}>
          {shortBuild(CLIENT_BUILD_ID)}
        </span>
      </header>

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

      <main className="car-body">
        {/* The one big status orb + word: whose turn it is, readable at a glance / audible by the two
            cues. Its color is the whole state at a distance. */}
        <div className={`car-orb car-orb-${phase}`} aria-hidden="true">
          <span className="car-orb-pulse" />
        </div>
        <p className="car-status" aria-live="polite">
          {started ? statusText : "Tap Start, then just talk."}
        </p>

        {/* Live microphone level meter (Architect direction): visibly shows the owner his voice is being
            picked up. Reads the capture stream's AnalyserNode via getMicLevel on an animation frame. It
            only moves while the microphone is actually capturing (the Listening phase); flat otherwise. */}
        {started && <MicMeter getLevel={getMicLevel} active={phase === "listening"} />}

        {pendingConfirmation && (
          <p className="car-confirm" role="status">
            Waiting for you to say "confirm" - or say "cancel".
          </p>
        )}

        {/* The heard command and the spoken reply. The interface is sound; this text is for eyes-on
            confirmation and debugging over chrome://inspect (mission decision 9). */}
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

      {/* Diagnostic readout (always visible). This is the eyes-on channel for confirming, at a glance,
          which page/Gateway is live and whether the phone is actually hearing the owner - the exact
          things a cached-bundle or dead-microphone failure would hide. Kept compact and monospace. */}
      <section className="car-debug" aria-label="Car Mode diagnostics">
        <DebugRow label="Client build" value={CLIENT_BUILD_ID} />
        <DebugRow label="Gateway" value={gatewayVersion} />
        <DebugRow label="Speech recognition" value={unsupported ? "NOT supported" : "supported"} />
        <DebugRow label="Recognizer" value={started ? recognizerState : "not started"} />
        <DebugRow label="Last recognizer error" value={recognizerError ?? "none"} />
        <DebugRow label="Hearing now" value={liveTranscript.length > 0 ? liveTranscript : "(silence)"} />
      </section>

      <footer className="car-foot">
        {!started ? (
          <button type="button" className="car-start" onClick={() => void start()} disabled={unsupported}>
            Start Car Mode
          </button>
        ) : (
          <>
            <p className="car-hint">
              Say <strong>"over and out"</strong> to send. Say <strong>"stop"</strong> to cut me off.
              Or use the buttons below.
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

      {history.length > 1 && (
        <details className="car-history">
          <summary>Recent turns ({history.length})</summary>
          <ul>
            {history.map((h, i) => (
              <li key={i}>
                <span className="car-history-you">{h.command}</span>
                <span className="car-history-said">{h.spoken}</span>
              </li>
            ))}
          </ul>
        </details>
      )}
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

// One label/value line in the diagnostic readout.
function DebugRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="car-debug-row">
      <span className="car-debug-label">{label}</span>
      <span className="car-debug-value">{value}</span>
    </div>
  );
}

// The short form of the build id for the header corner: just the git short sha (the first token), so
// the badge stays tiny. The full "sha timestamp" is in the title tooltip and the debug readout.
function shortBuild(buildId: string): string {
  const firstSpace = buildId.indexOf(" ");
  return firstSpace < 0 ? buildId : buildId.substring(0, firstSpace);
}

// The plain-English status line for each phase, the words that pair with the color orb and the cues.
function phaseStatus(phase: string): string {
  switch (phase) {
    case "listening":
      return "Listening - go ahead. I stay quiet until you say 'over and out'.";
    case "thinking":
      return "Got it. Working on that...";
    case "speaking":
      return "Speaking. Say 'stop' to cut me off.";
    default:
      return "Ready.";
  }
}
