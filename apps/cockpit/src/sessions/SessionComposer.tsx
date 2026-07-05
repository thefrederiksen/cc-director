import { useCallback, useRef, useState } from "react";
import {
  enqueuePrompt,
  sendPrompt,
  uploadImage,
  type QueueItem,
} from "@devthrottle/client-core/api/client";

// The composer (issue #972) - the React port of the Blazor Cockpit composer. It drives the selected
// session's reply through the shared Gateway client:
//
//   Send  -> POST /sessions/{sid}/prompt { appendEnter: true }  (submit the typed line; Ctrl+Enter)
//   Queue -> POST /sessions/{sid}/queue { text }                (append to the queue; Ctrl+Shift+Enter)
//   Attach -> POST /sessions/{sid}/upload-image                 (upload a device-local image, then
//             insert the Director-side saved path into the composer for the agent to read)
//
// The composer text is owned by the parent (SessionDetail) so the queue's Pop and the screenshot
// gallery's Insert can drop text into it. Send/Queue are disabled while empty or a call is in flight.

export interface SessionComposerProps {
  sessionId: string | undefined;
  value: string;
  onChange: (value: string) => void;
  /** Replace the queue with the server's authoritative list after a Queue action. */
  onQueued: (items: QueueItem[]) => void;
}

export function SessionComposer({ sessionId, value, onChange, onQueued }: SessionComposerProps) {
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const fileRef = useRef<HTMLInputElement | null>(null);

  const send = useCallback(async () => {
    if (!sessionId || busy) return;
    const text = value;
    if (text.trim().length === 0) return;
    setBusy(true);
    setError(null);
    onChange(""); // clear immediately, like the desktop composer
    try {
      await sendPrompt(sessionId, text, true);
      setStatus("Sent");
    } catch (err) {
      onChange(text); // restore so a failed send never loses the typed text
      setError(err instanceof Error ? err.message : "Send failed");
    } finally {
      setBusy(false);
    }
  }, [sessionId, busy, value, onChange]);

  const queue = useCallback(async () => {
    if (!sessionId || busy) return;
    const text = value;
    if (text.trim().length === 0) return;
    setBusy(true);
    setError(null);
    try {
      const items = await enqueuePrompt(sessionId, text);
      onQueued(items);
      onChange("");
      setStatus("Queued");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Queue failed");
    } finally {
      setBusy(false);
    }
  }, [sessionId, busy, value, onChange, onQueued]);

  const onKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
      // Ctrl+Shift+Enter = Queue; Ctrl+Enter = Send; plain Enter = newline (default).
      if (e.key === "Enter" && e.ctrlKey && e.shiftKey) {
        e.preventDefault();
        void queue();
        return;
      }
      if (e.key === "Enter" && e.ctrlKey) {
        e.preventDefault();
        void send();
      }
    },
    [queue, send],
  );

  const onAttach = useCallback(
    async (e: React.ChangeEvent<HTMLInputElement>) => {
      const files = e.target.files;
      if (!sessionId || !files || files.length === 0) return;
      setBusy(true);
      setError(null);
      try {
        const paths: string[] = [];
        for (const file of Array.from(files)) {
          const path = await uploadImage(sessionId, file);
          if (path) paths.push(path);
        }
        if (paths.length > 0) {
          // Insert the saved Director-side paths into the composer (a trailing space separates them),
          // the same end state as the desktop "drag the image onto the prompt".
          const prefix = value.length > 0 && !value.endsWith(" ") ? `${value} ` : value;
          onChange(`${prefix}${paths.join(" ")} `);
          setStatus(paths.length === 1 ? "Image attached" : `${paths.length} images attached`);
        }
      } catch (err) {
        setError(err instanceof Error ? err.message : "Attach failed");
      } finally {
        setBusy(false);
        if (fileRef.current) fileRef.current.value = ""; // allow re-selecting the same file
      }
    },
    [sessionId, value, onChange],
  );

  const empty = value.trim().length === 0;

  return (
    <div className="composer">
      <textarea
        className="composer-input"
        rows={3}
        placeholder="Type a message... (Ctrl+Enter to send, Ctrl+Shift+Enter to queue)"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={onKeyDown}
        spellCheck={false}
      />
      <div className="composer-btns">
        <button type="button" className="composer-btn send" disabled={busy || empty} onClick={() => void send()}>
          Send
        </button>
        <button type="button" className="composer-btn" disabled={busy || empty} onClick={() => void queue()}>
          Queue
        </button>
        <button
          type="button"
          className="composer-btn"
          disabled={busy}
          onClick={() => fileRef.current?.click()}
          title="Upload a device-local image and insert its path"
        >
          Attach
        </button>
        <input
          ref={fileRef}
          type="file"
          accept="image/*"
          multiple
          className="composer-file"
          onChange={(e) => void onAttach(e)}
        />
        {status !== null && <span className="composer-status">{status}</span>}
        {error !== null && <span className="composer-error">{error}</span>}
      </div>
    </div>
  );
}
