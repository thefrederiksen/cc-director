import { useCallback, useState } from "react";
import {
  clearQueue,
  deleteQueueItem,
  editQueueItem,
  moveQueueItemDown,
  moveQueueItemUp,
  sendQueueItem,
  gatewayErrorMessage,
  type QueueItem,
} from "@devthrottle/client-core/api/client";
import { ConfirmDialog } from "../components";

// The prompt queue panel (issue #972) - the React port of the Blazor Cockpit queue tab. Every verb
// goes to the owning Director through the Gateway and returns the authoritative queue, so the list is
// replaced from the response (no optimistic drift). Send-now submits a queued prompt immediately;
// Pop removes it and drops its text back into the composer; move/edit/remove/clear round out parity.

export interface QueuePanelProps {
  sessionId: string | undefined;
  queue: QueueItem[];
  onQueue: (items: QueueItem[]) => void;
  /** Drop a popped item's text back into the composer. */
  onPop: (text: string) => void;
}

export function QueuePanel({ sessionId, queue, onQueue, onPop }: QueuePanelProps) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editingText, setEditingText] = useState("");
  // Clearing the whole queue drops every queued prompt, so it asks through the shared ConfirmDialog
  // (issue #1244) rather than firing on the first click.
  const [confirmClear, setConfirmClear] = useState(false);

  const run = useCallback(
    async (verb: () => Promise<QueueItem[]>) => {
      if (!sessionId || busy) return;
      setBusy(true);
      setError(null);
      try {
        onQueue(await verb());
      } catch (err) {
        setError(gatewayErrorMessage(err));
      } finally {
        setBusy(false);
      }
    },
    [sessionId, busy, onQueue],
  );

  const saveEdit = useCallback(
    async (itemId: string) => {
      const text = editingText;
      setEditingId(null);
      setEditingText("");
      if (sessionId) await run(() => editQueueItem(sessionId, itemId, text));
    },
    [sessionId, editingText, run],
  );

  const pop = useCallback(
    async (itemId: string, text: string) => {
      onPop(text);
      if (sessionId) await run(() => deleteQueueItem(sessionId, itemId));
    },
    [sessionId, onPop, run],
  );

  return (
    <div className="qpanel">
      <div className="qpanel-head">
        <span className="qpanel-title">Queue{queue.length > 0 ? ` (${queue.length})` : ""}</span>
        {queue.length > 0 && sessionId && (
          <button type="button" className="linkbtn" disabled={busy} onClick={() => setConfirmClear(true)}>
            Clear
          </button>
        )}
      </div>

      {error !== null && <div className="qpanel-error">{error}</div>}

      {queue.length === 0 ? (
        <div className="qpanel-empty">No queued prompts.</div>
      ) : (
        <ul className="qlist">
          {queue.map((q, i) => {
            const editing = editingId === q.id;
            return (
              <li className="qitem" key={q.id}>
                <div className="qitem-top">
                  <span className="qitem-idx">{i + 1}</span>
                  <span className="qitem-text">{q.text}</span>
                </div>
                {editing ? (
                  <div className="qitem-edit">
                    <textarea
                      className="qitem-edit-box"
                      rows={3}
                      value={editingText}
                      onChange={(e) => setEditingText(e.target.value)}
                    />
                    <div className="qitem-btns">
                      <button type="button" className="qbtn" disabled={busy} onClick={() => void saveEdit(q.id)}>
                        Save
                      </button>
                      <button
                        type="button"
                        className="qbtn"
                        onClick={() => {
                          setEditingId(null);
                          setEditingText("");
                        }}
                      >
                        Cancel
                      </button>
                    </div>
                  </div>
                ) : (
                  <div className="qitem-btns">
                    {sessionId && (
                      <button type="button" className="qbtn" title="Submit now" disabled={busy} onClick={() => void run(() => sendQueueItem(sessionId, q.id))}>
                        Send
                      </button>
                    )}
                    {sessionId && (
                      <button type="button" className="qbtn" title="Move up" disabled={busy || i === 0} onClick={() => void run(() => moveQueueItemUp(sessionId, q.id))}>
                        Up
                      </button>
                    )}
                    {sessionId && (
                      <button type="button" className="qbtn" title="Move down" disabled={busy || i === queue.length - 1} onClick={() => void run(() => moveQueueItemDown(sessionId, q.id))}>
                        Down
                      </button>
                    )}
                    <button
                      type="button"
                      className="qbtn"
                      title="Edit text"
                      onClick={() => {
                        setEditingId(q.id);
                        setEditingText(q.text);
                      }}
                    >
                      Edit
                    </button>
                    <button type="button" className="qbtn" title="Remove from queue and paste into composer" disabled={busy} onClick={() => void pop(q.id, q.text)}>
                      Pop
                    </button>
                    {sessionId && (
                      <button type="button" className="qbtn qdel" title="Remove" disabled={busy} onClick={() => void run(() => deleteQueueItem(sessionId, q.id))}>
                        Remove
                      </button>
                    )}
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}

      <ConfirmDialog
        open={confirmClear}
        title="Clear the prompt queue?"
        message={`This removes ${
          queue.length === 1 ? "the 1 queued prompt" : `all ${queue.length} queued prompts`
        }. This cannot be undone.`}
        confirmLabel="Clear queue"
        busyLabel="Clearing..."
        onConfirm={async () => {
          if (sessionId === undefined) return;
          // Let a failure throw so the dialog surfaces it (fail loudly); the Gateway returns the
          // authoritative (now empty) queue on success.
          onQueue(await clearQueue(sessionId));
        }}
        onClose={() => setConfirmClear(false)}
      />
    </div>
  );
}
