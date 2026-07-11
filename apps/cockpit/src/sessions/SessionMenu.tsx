import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import {
  gatewayErrorMessage,
  getHandover,
  holdSession,
  killSession,
  type SessionDto,
  type SessionHandover,
} from "@devthrottle/client-core/api/client";
import { renameSession } from "@devthrottle/client-core/fleet/fleetClient";

// The session menu (issue #1214): a three-dot control with Rename, Put on hold / Resume, Handover info,
// and Close session. It is the SAME component on the session page and on every rail card. Every action
// goes Cockpit -> Gateway with relative URLs through the shared client (renameSession, holdSession,
// killSession, getHandover) - the browser never learns a Director address. Close asks for confirmation
// (it is destructive). A failed action shows a visible error, never a silent failure.

export interface SessionMenuProps {
  session: SessionDto;
  /** Called after the session is closed, so the page can navigate away. */
  onClosed?: () => void;
  /** "page" sits in the session header; "rail" is the compact button on a roster card. */
  variant?: "page" | "rail";
}

type Dialog = "rename" | "close" | "handover";

export function SessionMenu({ session, onClosed, variant = "page" }: SessionMenuProps) {
  const sid = session.sessionId ?? "";
  const [open, setOpen] = useState(false);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [renameText, setRenameText] = useState("");
  const [handover, setHandover] = useState<SessionHandover | null>(null);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const btnRef = useRef<HTMLButtonElement | null>(null);
  const popRef = useRef<HTMLDivElement | null>(null);
  // The dropdown is rendered in a portal on document.body so it can never be clipped by the scrolling
  // rail or covered by a later card; its position tracks the button (issue #1214). It anchors from
  // the top (opening downward) when there is room below, and flips to anchor from the bottom (opening
  // upward) for a button near the bottom of the window, so it never runs off the bottom of the screen.
  const [popPos, setPopPos] = useState<{ top?: number; bottom?: number; right: number } | null>(null);

  useLayoutEffect(() => {
    if (!open) return;
    const place = () => {
      const r = btnRef.current?.getBoundingClientRect();
      if (!r) return;
      const right = Math.max(8, window.innerWidth - r.right);
      // The menu holds a fixed set of items; this height covers all of them plus padding. If the
      // space below the button cannot fit it, open upward from the button's top instead.
      const MENU_HEIGHT = 184;
      const spaceBelow = window.innerHeight - r.bottom;
      if (spaceBelow < MENU_HEIGHT + 8) {
        setPopPos({ bottom: Math.max(8, window.innerHeight - r.top + 4), right });
      } else {
        setPopPos({ top: r.bottom + 4, right });
      }
    };
    place();
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [open]);

  // Close the dropdown on an outside click or Escape. The portal'd popover is outside rootRef, so it
  // is excluded explicitly.
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      const t = e.target as Node;
      if (rootRef.current?.contains(t)) return;
      if (popRef.current?.contains(t)) return;
      setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const closeDialog = useCallback(() => {
    setDialog(null);
    setError(null);
    setHandover(null);
  }, []);

  const openRename = useCallback(() => {
    setOpen(false);
    setError(null);
    setRenameText(session.name ?? "");
    setDialog("rename");
  }, [session.name]);

  const openClose = useCallback(() => {
    setOpen(false);
    setError(null);
    setDialog("close");
  }, []);

  const openHandover = useCallback(() => {
    setOpen(false);
    setError(null);
    setHandover(null);
    setDialog("handover");
    setBusy(true);
    getHandover(sid)
      .then((h) => setHandover(h))
      .catch((err) => setError(gatewayErrorMessage(err)))
      .finally(() => setBusy(false));
  }, [sid]);

  const doRename = useCallback(async () => {
    const name = renameText.trim();
    if (sid.length === 0 || name.length === 0) return;
    setBusy(true);
    setError(null);
    try {
      await renameSession(sid, name);
      closeDialog();
    } catch (err) {
      setError(gatewayErrorMessage(err));
    } finally {
      setBusy(false);
    }
  }, [sid, renameText, closeDialog]);

  const doHold = useCallback(async () => {
    if (sid.length === 0) return;
    setOpen(false);
    setBusy(true);
    setError(null);
    try {
      await holdSession(sid, !session.onHold);
    } catch (err) {
      setError(gatewayErrorMessage(err));
    } finally {
      setBusy(false);
    }
  }, [sid, session.onHold]);

  const doClose = useCallback(async () => {
    if (sid.length === 0) return;
    setBusy(true);
    setError(null);
    try {
      await killSession(sid);
      closeDialog();
      onClosed?.();
    } catch (err) {
      setError(gatewayErrorMessage(err));
    } finally {
      setBusy(false);
    }
  }, [sid, closeDialog, onClosed]);

  return (
    <div className={`session-menu ${variant}${open ? " open" : ""}`} ref={rootRef}>
      <button
        ref={btnRef}
        type="button"
        className="session-menu-btn"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label="Session menu"
        title="Session menu"
        onClick={(e) => {
          e.preventDefault();
          e.stopPropagation();
          setError(null);
          setOpen((o) => !o);
        }}
      >
        <span aria-hidden="true">...</span>
      </button>

      {open && popPos !== null &&
        createPortal(
          <div
            ref={popRef}
            className="session-menu-pop"
            role="menu"
            style={{
              position: "fixed",
              right: popPos.right,
              // Always set BOTH top and bottom (one to a value, the other to "auto") so that when the
              // menu flips upward (bottom-anchored) no stray top from CSS can leave both set at once,
              // which would break the menu's position.
              top: popPos.top ?? "auto",
              bottom: popPos.bottom ?? "auto",
            }}
          >
            <button type="button" role="menuitem" className="session-menu-item" onClick={openRename}>
              Rename
            </button>
            <button type="button" role="menuitem" className="session-menu-item" onClick={() => void doHold()}>
              {session.onHold ? "Resume" : "Put on hold"}
            </button>
            <button type="button" role="menuitem" className="session-menu-item" onClick={openHandover}>
              Handover info
            </button>
            <button type="button" role="menuitem" className="session-menu-item danger" onClick={openClose}>
              Close session
            </button>
          </div>,
          document.body,
        )}

      {/* The action error is shown on the button row when no dialog is open (e.g. a failed Hold). */}
      {error !== null && dialog === null && <span className="session-menu-error">{error}</span>}

      {dialog !== null && (
        <div className="session-dialog-overlay" onClick={closeDialog}>
          <div className="session-dialog" role="dialog" aria-modal="true" onClick={(e) => e.stopPropagation()}>
            {dialog === "rename" && (
              <>
                <h3 className="session-dialog-title">Rename session</h3>
                <input
                  className="session-dialog-input"
                  value={renameText}
                  onChange={(e) => setRenameText(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") void doRename();
                  }}
                  placeholder="Session name"
                  autoFocus
                />
                {error !== null && <div className="session-dialog-error">{error}</div>}
                <div className="session-dialog-actions">
                  <button type="button" className="session-dialog-btn" onClick={closeDialog} disabled={busy}>
                    Cancel
                  </button>
                  <button
                    type="button"
                    className="session-dialog-btn primary"
                    onClick={() => void doRename()}
                    disabled={busy || renameText.trim().length === 0}
                  >
                    {busy ? "Saving..." : "Save"}
                  </button>
                </div>
              </>
            )}

            {dialog === "close" && (
              <>
                <h3 className="session-dialog-title">Close session</h3>
                <p className="session-dialog-text">
                  Close <strong>{session.name || sid}</strong>? This ends the session on its machine and
                  removes it from the roster.
                </p>
                {error !== null && <div className="session-dialog-error">{error}</div>}
                <div className="session-dialog-actions">
                  <button type="button" className="session-dialog-btn" onClick={closeDialog} disabled={busy}>
                    Cancel
                  </button>
                  <button
                    type="button"
                    className="session-dialog-btn danger"
                    onClick={() => void doClose()}
                    disabled={busy}
                  >
                    {busy ? "Closing..." : "Close session"}
                  </button>
                </div>
              </>
            )}

            {dialog === "handover" && (
              <>
                <h3 className="session-dialog-title">Handover info</h3>
                {busy && handover === null && <div className="session-dialog-text">Loading...</div>}
                {error !== null && <div className="session-dialog-error">{error}</div>}
                {handover !== null && (
                  <dl className="session-handover">
                    <dt>Name</dt><dd>{handover.displayName || "(unnamed)"}</dd>
                    <dt>Session ID</dt><dd className="mono">{handover.sessionId}</dd>
                    <dt>Repo</dt><dd className="mono">{handover.repoPath}</dd>
                    <dt>Director ID</dt><dd className="mono">{handover.directorId}</dd>
                    <dt>Machine</dt><dd>{handover.machineName}</dd>
                    <dt>Version</dt><dd>{handover.version}</dd>
                  </dl>
                )}
                <div className="session-dialog-actions">
                  <button type="button" className="session-dialog-btn" onClick={closeDialog}>
                    Close
                  </button>
                </div>
              </>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
