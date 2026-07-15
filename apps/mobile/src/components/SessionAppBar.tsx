import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
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
// The right edge is shared with the globally-mounted network StatusPill (.net-pill, position: fixed,
// top-right). The app bar reserves space for it (--net-pill-reserve) so the overflow button sits
// clear of it and the title no longer runs underneath it.

export interface SessionAppBarProps {
  title: string;
  manage: SessionManage;
  /** Put Snooze/Unsnooze in the overflow menu. Screens that surface Snooze in their own bottom bar
   *  (Voice mode) leave this off, so the verb is never in two places at once. */
  showSnooze?: boolean;
  /** Screen-specific menu entries, rendered above Remove. Use <button className="menu-item">. */
  extraMenuItems?: ReactNode;
}

export function SessionAppBar({ title, manage, showSnooze = false, extraMenuItems }: SessionAppBarProps) {
  const navigate = useNavigate();
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

        {/* Row one ends here. The right of this row is left EMPTY on purpose: it is where the
            globally-mounted network pill (.net-pill, position: fixed, top-right) lands, so it reads
            as the status item of this bar instead of sitting on top of a control. Nothing here
            needs to guess how wide that pill is. */}
        <div className="session-bar-spacer" />
      </header>

      {/* The session name gets its OWN row, at full width. It cannot share the bar: between the back
          button, the menu button and the fixed network pill there is not enough room left for a real
          session name ("102 Ghost Directors from tests" collapsed to "102 M..."). This row is also
          what the name used to do WRONG - run underneath the pill and get covered by it. Even with
          this extra row the screen is shorter than before, because the three-slab manage row is gone. */}
      <h1 className="term-title session-title">{title}</h1>

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
