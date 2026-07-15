import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { StatusPill } from "./StatusPill";
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
// cut in half and unreadable. The pill now rides the title row as an ordinary inline item (the fixed
// one stands down on session screens, see GatedLayout), the button is back in the corner, and its menu
// opens inward. Nothing guesses any widths: both rows are plain flex.
//
//   row 1:  [<- Sessions] ................... [...]     navigation left, menu right
//   row 2:  102 devthrottle / f9e7 ....... ( o Fast )    name flexes, indicator rides along

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
}

export function SessionAppBar({ title, manage, showSnooze = false, showSwitchToVoice = false, extraMenuItems }: SessionAppBarProps) {
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
          onClick={() => navigate("/")}
          aria-label="Back to sessions"
        >
          &larr; Sessions
        </button>

        {/* Claims the middle of row 1, pushing the menu button into the corner it should have had all
            along. It guesses nothing: it just takes whatever is left between the two controls. */}
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
                  {manage.held ? "Unsnooze" : "Snooze"}
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

      {/* The session name gets its OWN row. It cannot share row one: between the back button and the
          menu button there is not enough room left for a real session name ("102 Ghost Directors from
          tests" collapsed to "102 M..."). The network pill rides HERE, at the end of this row, because
          it is an indicator - it reads as the status of this session's connection, it can no longer
          cover the name the way the fixed pill did, and it is out of the corner the menu needs. The
          name still flexes and ellipsizes (.term-title), so the pill costs it only the pill's width. */}
      <div className="session-title-row">
        <h1 className="term-title session-title">{title}</h1>
        <StatusPill inline />
      </div>

      {manage.error !== null && <div className="banner banner-error" role="alert">{manage.error}</div>}
      {manage.held && <span className="manage-held-pill">Snoozed</span>}

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
