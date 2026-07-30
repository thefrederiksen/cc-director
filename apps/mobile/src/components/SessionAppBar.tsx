import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { holdPillLabel } from "@devthrottle/client-core/sessions/snoozeAction";
import type { SessionManage } from "./useSessionManage";

// The ONE app bar shared by every per-session screen: Chat, Terminal and Voice mode (owner design
// review, "Option A"). It replaces the old SessionManageBar row, which mixed navigation (back to the
// roster) with actions (Snooze) and a destructive verb (Remove) in three equal-weight coloured slabs
// directly under the tab you press most - which is how Remove got mis-tapped.
//
// The rules this encodes, in the owner's order of use:
//
//   * Back to Sessions is NAVIGATION, so it lives at the LEFT of the app bar and is identical on
//     every screen. styles.css has always intended this ("Shared by every per-session screen via
//     .back-link", issue #1004); the session screens just never used it.
//   * Remove is rare and destructive, so it is in the overflow menu - still two taps away, never
//     under your thumb. It keeps its confirmation.
//   * Frequent actions do NOT live up here. They belong at the bottom, in the thumb zone; Voice mode
//     puts Snooze and Respond there. Screens with no bottom room for Snooze (Chat/Terminal, whose
//     bottom is the message composer) pass showSnooze so it appears in this menu instead.
//
// A CONTROL outranks an INDICATOR for the top-right corner. This bar used to put the overflow button
// on the LEFT, because the globally-mounted network pill was fixed to the top-right and nothing wanted
// to guess its width. The cost was a broken menu: .session-menu is anchored right:0 - correct for a
// button on the right - so hanging it off a LEFT button opened it off the left edge of the screen,
// cut in half and unreadable. The pill was then moved inline onto row 1, and finally deleted outright
// (see ConnectionBanner - the app says nothing while the connection is fine). The corner is the
// button's for good. Nothing guesses any widths: both rows are plain flex.
//
//   row 1:  [<- Sessions] ................... [...]     navigation left, menu right
//   row 2:  102 devthrottle / f9e7 .....................  the name gets the whole row

export interface SessionAppBarProps {
  title: string;
  manage: SessionManage;
  /** Put Snooze/Unsnooze in the overflow menu. Screens that surface Snooze in their own bottom bar
   *  (Voice mode) leave this off, so the verb is never in two places at once. */
  showSnooze?: boolean;
  /** Offer "Switch to voice mode" in the menu. The screens that are NOT voice (Chat/Terminal) pass
   *  this so voice is reachable in one tap from where you already are, instead of making you open the
   *  Voice mode tab first purely to find the button on it. */
  showSwitchToVoice?: boolean;
  /** Screen-specific menu entries, rendered above Remove. Use <button className="menu-item">. */
  extraMenuItems?: ReactNode;
  /** Router state for the Back to Sessions navigation. When supplied, the session route is replaced
   *  by the roster route so browser Back cannot reopen the session that was just left. */
  backState?: Record<string, unknown>;
}

export function SessionAppBar({ title, manage, showSnooze = false, showSwitchToVoice = false, extraMenuItems, backState }: SessionAppBarProps) {
  const navigate = useNavigate();
  const { sessionId } = useParams<{ sessionId: string }>();
  const [open, setOpen] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const menuRef = useRef<HTMLDivElement | null>(null);

  // Close the menu on an outside tap or Escape, the way a menu is expected to behave.
  useEffect(() => {
    if (!open) return;
    const onPointer = (e: PointerEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("pointerdown", onPointer);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("pointerdown", onPointer);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const onConfirmRemove = useCallback(async () => {
    try {
      await manage.removeSession();
    } catch {
      // The hook has already surfaced the message; keep the sheet open so it is readable.
      setConfirming(false);
    }
  }, [manage]);

  return (
    <>
      <header className="app-bar session-app-bar">
        <button
          type="button"
          className="back-link session-back"
          onClick={() =>
            navigate("/", backState !== undefined ? { state: backState, replace: true } : undefined)
          }
          aria-label="Back to sessions"
        >
          &larr; Sessions
        </button>

        {/* The spacer pushes the menu button to the corner and guesses no widths. It used to be two
            spacers with the network status pill centred between them; the pill is gone (see
            ConnectionBanner) and row 1 is now just Back and the menu button. */}
        <div className="session-bar-spacer" />

        <div className="session-menu-wrap" ref={menuRef}>
          <button
            type="button"
            className="session-menu-btn"
            onClick={() => setOpen((v) => !v)}
            aria-haspopup="menu"
            aria-expanded={open}
            aria-label="Session menu"
          >
            <span className="session-menu-dots" aria-hidden="true" />
          </button>

          {open && (
            <div className="session-menu" role="menu">
              {showSwitchToVoice && (
                <button
                  type="button"
                  className="menu-item"
                  role="menuitem"
                  onClick={() => {
                    setOpen(false);
                    // Hand the switch-on to the Voice screen rather than doing it here: useVoiceMode
                    // already owns that verb (mark the session Voice on its Director, then explain on
                    // the Gateway). Duplicating it here would be a second copy free to disagree with
                    // the first. The screen reads this and runs its own onSwitchOn once, on arrival.
                    navigate(`/session/${encodeURIComponent(sessionId ?? "")}/voice`, {
                      state: { switchOn: true },
                    });
                  }}
                >
                  Switch to voice mode
                </button>
              )}

              {showSnooze && (
                <button
                  type="button"
                  className="menu-item"
                  role="menuitem"
                  onClick={() => {
                    setOpen(false);
                    void manage.toggleHold();
                  }}
                  disabled={manage.busy || manage.onHold === null}
                >
                  {manage.held || manage.deferred ? "Unsnooze" : "Snooze"}
                </button>
              )}

              {extraMenuItems !== undefined && (
                <div className="menu-group" onClick={() => setOpen(false)}>
                  {extraMenuItems}
                </div>
              )}

              <button
                type="button"
                className="menu-item menu-item-danger"
                role="menuitem"
                onClick={() => {
                  setOpen(false);
                  setConfirming(true);
                }}
                disabled={manage.busy}
              >
                Remove session
              </button>
            </div>
          )}
        </div>

      </header>

      {/* The session name gets its OWN row, and now the WHOLE row: it cannot share row one, where the
          back button and the menu button leave no room for a real name ("102 Ghost Directors from tests"
          collapsed to "102 M..."). The network pill used to ride the end of this row and has moved up to
          the middle of row 1 - see the note there; in short, it was sitting directly above the Voice mode
          tab and getting hit instead of it. */}
      <div className="session-title-row">
        <h1 className="term-title session-title">{title}</h1>
      </div>

      {manage.error !== null && <div className="banner banner-error" role="alert">{manage.error}</div>}
      {/* A prompt to this session was NOT delivered - the user's words never reached the agent (issue
          internal#811). It lives on the shared app bar so it is on EVERY per-session screen: the loss
          usually happens while the phone is somewhere else entirely (a dictation sent from the roster,
          the screen locked), so a banner that only existed on one tab would be a banner nobody sees.
          Sticky by design - it clears when a prompt actually lands, never on a tap and never on a timer -
          because a failure the user can wave away is how two spoken prompts went missing for two days. */}
      {manage.deliveryNotice !== null && (
        <div className="banner banner-not-delivered" role="alert">{manage.deliveryNotice}</div>
      )}
      {/* The snooze pill. A DEFERRED snooze reads "Snoozing when it finishes" (asked for while the agent is
          working, so it arms when the work ends) - this is what makes snoozing a busy session give instant
          feedback instead of looking like nothing happened. An armed snooze reads the Gateway FOLD
          (manage.snoozed), not the raw onHold flag - a Held session that has started working is blue
          "Working" and must not still read "Snoozed" - with the running hold time beside it, matching the
          roster and the desktop rail. */}
      {(() => {
        const pill = holdPillLabel(
          { held: manage.held, deferred: manage.deferred },
          manage.snoozed,
          manage.holdCountdown,
        );
        return pill !== null ? <span className="manage-held-pill">{pill}</span> : null;
      })()}

      {confirming && (
        <div className="confirm-overlay" role="dialog" aria-modal="true" aria-label="Remove session">
          <div className="confirm-card">
            <h2 className="confirm-title">Remove this session?</h2>
            <p className="confirm-text">This will terminate it. This cannot be undone.</p>
            <div className="confirm-actions">
              <button
                type="button"
                className="confirm-btn confirm-cancel"
                onClick={() => setConfirming(false)}
                disabled={manage.busy}
              >
                Cancel
              </button>
              <button
                type="button"
                className="confirm-btn confirm-remove"
                onClick={onConfirmRemove}
                disabled={manage.busy}
              >
                {manage.busy ? "Removing..." : "Remove"}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
