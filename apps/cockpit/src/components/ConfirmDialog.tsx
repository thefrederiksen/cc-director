import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { Button } from "./Button";

// The one confirmation dialog every destructive action in the Cockpit routes through (issue #1244).
// Before this, destructive actions were handled three different ways: a good inline confirmation on the
// Account page, a blocking browser window.confirm/alert on Exes and the Voice Recorder, and NO
// confirmation at all on Schedule delete, Clear context, Clear queue, and screenshot delete. This is the
// single in-app dialog that replaces all three, so "are you sure?" looks and behaves the same across the
// whole app and can never again be a browser pop-up.
//
// It manages its own busy and error lifecycle so a calling page does not have to. The contract for the
// action is simple and fails loudly (no fallback):
//   - onConfirm may be synchronous or async. If it returns a promise, the dialog shows a busy state
//     until the promise settles.
//   - On success the dialog calls onClose (the caller clears its pending target, which unmounts it).
//   - On failure (a thrown error) the dialog stays open and shows exactly what went wrong, so the
//     person can read the error and either retry or cancel - the failure is never swallowed.

export interface ConfirmDialogProps {
  /** Whether the dialog is shown. Drive this from a "pending action" state on the calling page. */
  open: boolean;
  /** The question, e.g. "Delete this cron job?". */
  title: string;
  /** The body: what will happen, and any warning (for example, "This cannot be undone."). */
  message: ReactNode;
  /** The confirm button label; defaults to "Confirm". Use a verb, e.g. "Delete" or "Clear". */
  confirmLabel?: string;
  /** The cancel button label; defaults to "Cancel". */
  cancelLabel?: string;
  /**
   * Whether the confirm button is styled as destructive (red). Defaults to true, because this dialog
   * exists for destructive actions; pass false for a heavy-but-safe action (for example, a rebuild).
   */
  danger?: boolean;
  /** The confirm button label while the action runs; defaults to "Working...". */
  busyLabel?: string;
  /** Runs when the person confirms. Return a promise to have the dialog show a busy state and, on a
   *  thrown error, surface it inline. */
  onConfirm: () => void | Promise<void>;
  /** Runs when the person cancels, dismisses (backdrop or Escape), or the action succeeds. */
  onClose: () => void;
}

export function ConfirmDialog({
  open,
  title,
  message,
  confirmLabel = "Confirm",
  cancelLabel = "Cancel",
  danger = true,
  busyLabel = "Working...",
  onConfirm,
  onClose,
}: ConfirmDialogProps) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Reset the transient busy/error each time the dialog opens, so a previous failure never bleeds into
  // a fresh confirmation.
  useEffect(() => {
    if (open) {
      setBusy(false);
      setError(null);
    }
  }, [open]);

  // Escape dismisses the dialog, but never while its action is mid-flight (that would orphan the
  // in-flight request behind a closed dialog).
  useEffect(() => {
    if (!open) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape" && !busy) onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [open, busy, onClose]);

  if (!open) return null;

  const runConfirm = async () => {
    setError(null);
    try {
      const result = onConfirm();
      if (result instanceof Promise) {
        setBusy(true);
        await result;
      }
      onClose();
    } catch (err) {
      // Fail loudly: keep the dialog open and show precisely what went wrong.
      setError(gatewayErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div
      className="ui-modal-backdrop"
      onClick={() => {
        if (!busy) onClose();
      }}
    >
      <div
        className="ui-confirm"
        role="alertdialog"
        aria-modal="true"
        aria-label={title}
        onClick={(event) => event.stopPropagation()}
      >
        <div className="ui-confirm-title">{title}</div>
        <div className="ui-confirm-message">{message}</div>
        {error !== null && <div className="ui-confirm-error">{error}</div>}
        <div className="ui-confirm-actions">
          <Button variant="secondary" disabled={busy} onClick={onClose}>
            {cancelLabel}
          </Button>
          <Button variant={danger ? "danger" : "primary"} disabled={busy} onClick={() => void runConfirm()}>
            {busy ? busyLabel : confirmLabel}
          </Button>
        </div>
      </div>
    </div>
  );
}
