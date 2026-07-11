import { useCallback } from "react";
import { Link } from "react-router-dom";
import { useCarMode, type CarModeReply } from "@devthrottle/client-core/carmode/useCarMode";
import { carModeTurn } from "@devthrottle/client-core/carmode/carModeApi";
import { useScreenWakeLock } from "../hooks/useScreenWakeLock";

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
        <span className="car-bar-spacer" aria-hidden="true" />
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

      <footer className="car-foot">
        {!started ? (
          <button type="button" className="car-start" onClick={() => void start()} disabled={unsupported}>
            Start Car Mode
          </button>
        ) : (
          <>
            <p className="car-hint">
              Say <strong>"over and out"</strong> to send. Say <strong>"stop"</strong> to cut me off.
            </p>
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
