import { useCallback, useEffect, useRef, useState } from "react";
import type { MutableRefObject } from "react";
import {
  enqueuePrompt,
  sendPrompt,
  uploadImage,
  gatewayErrorMessage,
  type QueueItem,
} from "@devthrottle/client-core/api/client";
import { backgroundTranscribeAndSend, type CapturedUtterance } from "@devthrottle/client-core/dictation/backgroundSend";
import { DictationDialog } from "@devthrottle/client-core/dictation/DictationDialog";
import { insertAt } from "@devthrottle/client-core/dictation/transcript";

// The composer (issue #972, completed in issue #1210) - the React port of the Blazor Cockpit composer.
// It drives the selected session's reply through the shared Gateway client:
//
//   Send  -> POST /sessions/{sid}/prompt { appendEnter: true }  (submit the typed line; Ctrl+Enter)
//   Speak -> mounts the shared DictationDialog (packages/client-core), exactly as the mobile
//            SessionControls does; the transcript inserts at the caret (Insert) or inserts + submits
//            (Send). One transcription path through the Gateway, unchanged.
//   Queue -> POST /sessions/{sid}/queue { text }                (append to the queue; Ctrl+Shift+Enter)
//   Attach -> POST /sessions/{sid}/upload-image                 (upload a device-local image, then
//             insert the Director-side saved path into the composer for the agent to read)
//
// Attach, clipboard paste, and drag-and-drop of an image all funnel through the single `attachFiles`
// helper (issue #1210) so there is exactly one upload code path (the shared `uploadImage`), never a
// second one.
//
// The composer text is owned by the parent (SessionDetail) so the queue's Pop and the screenshot
// gallery's Insert can drop text into it. Send/Queue are disabled while empty or a call is in flight.

export interface SessionComposerProps {
  sessionId: string | undefined;
  value: string;
  onChange: (value: string) => void;
  /** Replace the queue with the server's authoritative list after a Queue action. */
  onQueued: (items: QueueItem[]) => void;
  /**
   * When set, the composer publishes a function into this ref that focuses its textarea. The Source
   * Control tab (issue #1266) calls it after inserting a clicked file's path so the reader can type the
   * matching instruction straight away. Cleared on unmount so a stale focuser is never called.
   */
  focusHandleRef?: MutableRefObject<(() => void) | null>;
}

export function SessionComposer({ sessionId, value, onChange, onQueued, focusHandleRef }: SessionComposerProps) {
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dictating, setDictating] = useState(false);
  const [dragOver, setDragOver] = useState(false);
  const fileRef = useRef<HTMLInputElement | null>(null);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);
  // The caret position, snapshotted when Speak is pressed (the dialog is modal, so the box cannot
  // change while it is open). Dictation is inserted here, exactly like the desktop Insert button and
  // the mobile SessionControls, instead of always at the end.
  const caretRef = useRef(0);

  // Publish a focuser for the composer textarea into the parent-owned ref (issue #1266), and clear it on
  // unmount so the Source Control tab never calls into a torn-down composer.
  useEffect(() => {
    if (!focusHandleRef) return;
    focusHandleRef.current = () => textareaRef.current?.focus();
    return () => {
      focusHandleRef.current = null;
    };
  }, [focusHandleRef]);

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
      setError(gatewayErrorMessage(err));
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
      setError(gatewayErrorMessage(err));
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

  // The ONE image-upload path (issue #1210): Attach, clipboard paste, and drag-and-drop all call this.
  // It uploads each image with the shared `uploadImage` (browser-device image up to the Director) and
  // inserts the Director-side saved path(s) into the composer - the same end state as the desktop
  // "drag the image onto the prompt". There is no second upload code path anywhere.
  const attachFiles = useCallback(
    async (files: File[]) => {
      if (!sessionId || files.length === 0) return;
      setBusy(true);
      setError(null);
      try {
        const paths: string[] = [];
        for (const file of files) {
          const path = await uploadImage(sessionId, file);
          if (path) paths.push(path);
        }
        if (paths.length > 0) {
          const prefix = value.length > 0 && !value.endsWith(" ") ? `${value} ` : value;
          onChange(`${prefix}${paths.join(" ")} `);
          setStatus(paths.length === 1 ? "Image attached" : `${paths.length} images attached`);
        }
      } catch (err) {
        setError(gatewayErrorMessage(err));
      } finally {
        setBusy(false);
      }
    },
    [sessionId, value, onChange],
  );

  const onAttach = useCallback(
    async (e: React.ChangeEvent<HTMLInputElement>) => {
      const files = e.target.files;
      if (files) await attachFiles(Array.from(files));
      if (fileRef.current) fileRef.current.value = ""; // allow re-selecting the same file
    },
    [attachFiles],
  );

  // Clipboard paste of an image into the textarea (issue #1210): routes through the same attachFiles
  // upload. A paste that carries no image is left alone so normal text paste is unaffected.
  const onPaste = useCallback(
    (e: React.ClipboardEvent<HTMLTextAreaElement>) => {
      const images = Array.from(e.clipboardData.files).filter((f) => f.type.startsWith("image/"));
      if (images.length === 0) return;
      e.preventDefault();
      void attachFiles(images);
    },
    [attachFiles],
  );

  // Drag-and-drop of an image file onto the composer (issue #1210): same attachFiles upload.
  const onDrop = useCallback(
    (e: React.DragEvent<HTMLDivElement>) => {
      const images = Array.from(e.dataTransfer.files).filter((f) => f.type.startsWith("image/"));
      setDragOver(false);
      if (images.length === 0) return;
      e.preventDefault();
      void attachFiles(images);
    },
    [attachFiles],
  );

  const onDragOver = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    if (Array.from(e.dataTransfer.items).some((i) => i.kind === "file")) {
      e.preventDefault();
      setDragOver(true);
    }
  }, []);

  const onDragLeave = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    // Only clear when the pointer actually leaves the composer (not when moving between its children).
    if (e.currentTarget.contains(e.relatedTarget as Node | null)) return;
    setDragOver(false);
  }, []);

  // Speak: mount the shared dictation dialog, exactly like the mobile SessionControls. Insert drops the
  // transcript at the caret (no submit); Send inserts at the caret and submits via the same POST /prompt
  // path; SendAudio hands the raw clip to the durable background pipeline (one transcription path
  // through the Gateway).
  const onSpeak = useCallback(() => {
    caretRef.current = textareaRef.current?.selectionStart ?? value.length;
    setError(null);
    setDictating(true);
  }, [value]);

  const onDictateInsert = useCallback(
    (text: string) => {
      setDictating(false);
      if (text.trim().length === 0) return;
      onChange(insertAt(value, caretRef.current, text));
      setStatus("Inserted");
    },
    [value, onChange],
  );

  const onDictateSend = useCallback(
    async (text: string) => {
      setDictating(false);
      if (!sessionId) return;
      const combined = insertAt(value, caretRef.current, text).trim();
      if (combined.length === 0) return;
      setBusy(true);
      setError(null);
      onChange("");
      try {
        await sendPrompt(sessionId, combined, true);
        setStatus("Sent");
      } catch (err) {
        onChange(combined); // restore so a failed send never loses the typed + dictated text
        setError(gatewayErrorMessage(err));
      } finally {
        setBusy(false);
      }
    },
    [sessionId, value, onChange],
  );

  // Immediate (fire-and-forget) Send from the Speak dialog: the dialog captured the audio buffer and
  // closed itself. The durable pipeline transcodes + uploads + transcribes + submits in the background
  // (the roster shows the session orange, "Transcribing..."), so the browser is free immediately.
  const onDictateSendAudio = useCallback(
    (captured: CapturedUtterance) => {
      setDictating(false);
      if (!sessionId) return;
      const composerText = value;
      const caret = caretRef.current;
      onChange("");
      setStatus("Transcribing...");
      void backgroundTranscribeAndSend(sessionId, captured, {
        onError: (message) => setError(message),
        // Insert the dictated words at the snapshotted caret inside the typed text: the Gateway submits
        // before + dictation + after.
        composeParts: { before: composerText.slice(0, caret), after: composerText.slice(caret) },
        // If the send does not complete, the audio is kept durably for resume, but the typed text is
        // client-only: put it back so Send never silently loses it.
        onFailed: () => {
          if (composerText.trim().length > 0) onChange(composerText);
        },
      });
    },
    [sessionId, value, onChange],
  );

  const empty = value.trim().length === 0;

  return (
    <div
      className={`composer ${dragOver ? "composer-dragover" : ""}`}
      onDrop={onDrop}
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
    >
      <textarea
        ref={textareaRef}
        className="composer-input"
        rows={3}
        placeholder="Type a message... (Ctrl+Enter to send, Ctrl+Shift+Enter to queue)"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={onKeyDown}
        onPaste={onPaste}
        spellCheck={false}
      />
      <div className="composer-btns">
        <button type="button" className="composer-btn send" disabled={busy || empty} onClick={() => void send()}>
          Send
        </button>
        <button
          type="button"
          className="composer-btn"
          disabled={busy || dictating || !sessionId}
          onClick={onSpeak}
          title="Dictate into the composer"
        >
          Speak
        </button>
        <button type="button" className="composer-btn" disabled={busy || empty} onClick={() => void queue()}>
          Queue
        </button>
        <button
          type="button"
          className="composer-btn"
          disabled={busy}
          onClick={() => fileRef.current?.click()}
          title="Upload a device-local image and insert its path (or paste / drag one in)"
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

      {dictating && (
        <DictationDialog
          onInsert={onDictateInsert}
          onSend={onDictateSend}
          onSendAudio={onDictateSendAudio}
          onClose={() => setDictating(false)}
        />
      )}
    </div>
  );
}
