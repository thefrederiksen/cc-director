import type { ThrottleWindow } from "./statsClient";
import "./throttleWindow.css";

// The Your Throttle period selector, shared by the Cockpit and the phone (mission "Clean up Your Throttle",
// ruling R4, and CLAUDE.md rule 8's shape: one component on two surfaces, so neither shell can grow a
// selector of its own that offers a different list).
//
// THE CLIENT IS DUMB (CLAUDE.md rule 7). The lengths offered are the Gateway's served `choices`, in the
// Gateway's order with the Gateway's labels - the last is the ledger's own retention, and this component
// could not know that. Which choice is in effect is read from the served window's `days`. When the served
// window is a WEEK (the mentor report's link opens the page on exactly the week it covered), that week is
// shown as the selected item using the Gateway's label, and no length is marked. Choosing calls back with
// the length; the page puts it in the URL and asks the Gateway, which decides what it means.
//
// Sizing here is the desktop baseline (throttleWindow.css, beside this file); the phone re-tunes it for
// touch under its `.screen` ancestor, exactly as the shared Settings cards are re-tuned.

export function ThrottleWindowSelector({
  window,
  onChoose,
}: {
  window: ThrottleWindow;
  /** Called with the chosen length in days - one of the served choices. */
  onChoose: (days: number) => void;
}) {
  const inEffect = window.kind === "default" || window.kind === "days" ? window.days : null;
  return (
    <div className="thr-window" role="group" aria-label="Period">
      {window.kind === "week" && (
        <span className="thr-window-choice active" aria-current="true" data-testid="thr-window-week">
          {window.label}
        </span>
      )}
      {window.choices.map((choice) => (
        <button
          key={choice.days}
          type="button"
          className={choice.days === inEffect ? "thr-window-choice active" : "thr-window-choice"}
          aria-pressed={choice.days === inEffect}
          onClick={() => onChoose(choice.days)}
        >
          {choice.label}
        </button>
      ))}
    </div>
  );
}
