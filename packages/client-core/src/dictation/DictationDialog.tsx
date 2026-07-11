import { useCallback, useEffect, useRef, useState } from "react";
import { transcribeUtterance } from "../api/client";
import type { CapturedUtterance } from "./backgroundSend";
import { logCaptureHealth } from "./captureHealth";
import { playReadyCue } from "./readyCue";
import { MicRecorder } from "./recorder";
import { joinText } from "./transcript";
import { blobToWav16kMono } from "./wav";

// The ONE shared mobile dictation dialog (issue #817), used by the Terminal view (#810) and, when
// it lands, the Chat view (#811) - both mount this same component and pass their own onInsert /
// onSend, so the Speak control behaves identically everywhere from one source. It is the phone-web
// twin of the Android SpeakIntoTextboxDialog and the desktop SpeakDialog, obeying the canonical
// contract docs/architecture/dictation/DICTATION_UX_SPEC.md.
//
// Whole-clip BATCH with a Pause checkpoint. NO text appears while talking; speech becomes text only
// at a checkpoint (Pause) or on commit (Insert/Send). Pause stops the mic, transcribes the segment
// so far, appends it to the (editable) transcript, and Resume starts a fresh segment that appends.
// Insert commits the text WITHOUT submitting; Send commits AND submits (the host wires what those
// mean). The four buttons are Cancel / Pause-Resume / Insert / Send.
//
// Transcript integrity (CodingStyle.md s16): the server returns the user's words already corrected
// by the single dictionary-correction engine; this component only displays them, lets the user
// hand-edit, and joins segments with a single space. It never rewrites the words.

// "connecting" = the GETTING READY warm-up: the mic is opening but has not delivered audio yet, so
// the dialog is deliberately NOT in the red RECORDING look and plays no cue. It flips to "recording"
// (and plays the ready bloop) only when the first real audio chunk arrives, so neither the red state
// nor the sound ever precedes real audio - the desktop SpeakDialog does the same.
type Stage = "connecting" | "recording" | "transcribing" | "paused" | "error";

// Backstop for the GETTING READY state: if the mic never delivers a first chunk, fail loud with a
// "check your microphone" message rather than stranding the user on GETTING READY forever.
const READY_TIMEOUT_MS = 6000;

export interface DictationDialogProps {
  /** Commit the transcript WITHOUT submitting (drop into the view's text box for editing). Required
   *  only when Insert is shown; ignored when showInsert is false. */
  onInsert?: (text: string) => void;
  /** Commit the transcript AND submit it (the view's Send path). Used for the instant PAUSED-stage
   *  Send (the text is already transcribed) and as the fallback Send when onSendAudio is not wired. */
  onSend: (text: string) => void;
  /** Immediate (fire-and-forget) Send: when provided, hitting Send while still RECORDING captures the
   *  audio buffer, hands it here, and closes the dialog IMMEDIATELY - the host then transcodes,
   *  uploads, transcribes, and submits in the background (marking the session "Transcribing..."). This
   *  is what releases the screen the instant Send is pressed, since the result cannot be viewed here
   *  anyway. When omitted (e.g. the Voice-mode reply panel) Send falls back to the blocking
   *  transcribe-then-submit path via onSend. */
  onSendAudio?: (captured: CapturedUtterance) => void;
  /** Close the dialog (Cancel, or after a commit). Nothing is sent on Cancel. */
  onClose: () => void;
  /** Whether to offer the Insert button. The Voice mode "Respond" flow (issue #850, mockup F) sets
   *  this false so the reply panel is Cancel / Pause / Send only - Send goes straight into the
   *  session, there is no "drop into a box" target. Defaults true for the Terminal/Chat Speak flow. */
  showInsert?: boolean;
}

const BAR_COUNT = 9;

function formatElapsed(ms: number): string {
  const totalSeconds = Math.floor(ms / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${String(seconds).padStart(2, "0")}`;
}

export function DictationDialog({ onInsert, onSend, onSendAudio, onClose, showInsert = true }: DictationDialogProps) {
  const recorderRef = useRef<MicRecorder>(new MicRecorder());
  const accumulatedRef = useRef<string>(""); // committed segments; the box may be edited past this
  const busyRef = useRef<boolean>(false); // guards the transcribe window against double taps

  // Segment timing: the running total freezes during transcribing/paused and resumes on Resume.
  const segmentStartRef = useRef<number>(0);
  const elapsedBeforeRef = useRef<number>(0);

  const [stage, setStage] = useState<Stage>("connecting");
  // Mirror of the current stage for use inside timers/callbacks without stale closures.
  const stageRef = useRef<Stage>("connecting");
  useEffect(() => {
    stageRef.current = stage;
  }, [stage]);

  // Pending GETTING READY backstop timer (window.setTimeout handle), or null when disarmed.
  const readyTimeoutRef = useRef<number | null>(null);

  const [transcript, setTranscript] = useState<string>("");
  const [hint, setHint] = useState<string>("");
  const [errorText, setErrorText] = useState<string>("");
  const [elapsed, setElapsed] = useState<number>(0);
  const [levels, setLevels] = useState<number[]>(() => new Array(BAR_COUNT).fill(0));

  const clearReadyBackstop = useCallback(() => {
    if (readyTimeoutRef.current !== null) {
      window.clearTimeout(readyTimeoutRef.current);
      readyTimeoutRef.current = null;
    }
  }, []);

  const armReadyBackstop = useCallback(() => {
    clearReadyBackstop();
    readyTimeoutRef.current = window.setTimeout(() => {
      readyTimeoutRef.current = null;
      if (stageRef.current !== "connecting") return;
      setErrorText(
        "The microphone did not start capturing. Check that it is connected, not muted, and that this site is allowed to use it, then try again.",
      );
      setStage("error");
    }, READY_TIMEOUT_MS);
  }, [clearReadyBackstop]);

  // The honest "mic is now capturing your voice" moment (first real audio chunk). Anchor the
  // segment timer here, flip to RECORDING, and play the ready cue together - so the red and the
  // sound land exactly when audio starts flowing, never before. elapsedBeforeRef is left untouched
  // so a Resume keeps its accumulated time; only a fresh open resets it (in the mount effect).
  const onCaptureLive = useCallback(() => {
    clearReadyBackstop();
    if (stageRef.current !== "connecting") return;
    segmentStartRef.current = performance.now();
    setStage("recording");
    playReadyCue();
  }, [clearReadyBackstop]);

  // ----- equalizer + timer animation (display only) -----
  useEffect(() => {
    let raf = 0;
    const tick = () => {
      if (stage === "recording") {
        const now = performance.now();
        setElapsed(elapsedBeforeRef.current + (now - segmentStartRef.current));
        const level = recorderRef.current.level();
        const next = new Array(BAR_COUNT).fill(0).map((_, i) => {
          // Centre bars react most, so it reads as an equalizer rather than a flat bar.
          const centre = (BAR_COUNT - 1) / 2;
          const falloff = 1 - Math.abs(i - centre) / (centre + 1);
          return Math.min(1, level * (0.55 + falloff));
        });
        setLevels(next);
      }
      raf = window.requestAnimationFrame(tick);
    };
    raf = window.requestAnimationFrame(tick);
    return () => window.cancelAnimationFrame(raf);
  }, [stage]);

  // ----- start the first recording segment on open; tear the mic down on unmount -----
  useEffect(() => {
    const recorder = recorderRef.current;
    let cancelled = false;
    // Flip to RECORDING + play the cue only when the first real audio chunk arrives (not merely
    // when start() returns). The same handler serves every segment on this recorder instance.
    recorder.onCaptureLive = () => {
      if (cancelled) return;
      onCaptureLive();
    };
    (async () => {
      try {
        // A fresh open starts the running total from zero; hold GETTING READY until audio flows.
        elapsedBeforeRef.current = 0;
        armReadyBackstop();
        await recorder.start();
        // Intentionally do NOT flip to RECORDING here - onCaptureLive does, once audio is real.
      } catch (err) {
        if (cancelled) return;
        clearReadyBackstop();
        setErrorText(err instanceof Error ? err.message : "Could not start the microphone.");
        setStage("error");
      }
    })();
    return () => {
      cancelled = true;
      clearReadyBackstop();
      recorder.dispose();
    };
  }, [onCaptureLive, armReadyBackstop, clearReadyBackstop]);

  // Stop the current segment, transcode to WAV, and transcribe it. Returns the segment text, or
  // null when there was no audio / transcription failed (the reason is shown). The recorder is
  // consumed here, so a segment is never transcribed twice.
  const transcribeCurrentSegment = useCallback(async (): Promise<string | null> => {
    let wav: Blob;
    let health;
    try {
      const captured = await recorderRef.current.stop();
      const transcoded = await blobToWav16kMono(captured);
      wav = transcoded.wav;
      // Capture-health (issue #863): compare the recording wall-clock to the decoded audio
      // duration to detect audio dropped before transcription. Logged locally AND sent to the
      // Gateway so it persists into the same dictation session log every other surface writes.
      health = {
        recordedMs: recorderRef.current.lastRecordedMs,
        decodedSeconds: transcoded.decodedSeconds,
        sourceBytes: transcoded.sourceBytes,
      };
      logCaptureHealth("mobile", health);
    } catch (err) {
      setHint(err instanceof Error ? err.message : "Could not capture the recording.");
      return null;
    }
    try {
      const text = await transcribeUtterance(wav, health, undefined, (uploaded, total) => {
        // A short clip uploads in one chunk (no progress line needed); a long recording uploads in
        // several, so show which piece is going up before the "Transcribing..." wait.
        if (total > 1) setHint(`Uploading recording... ${uploaded} of ${total}`);
      });
      return text;
    } catch (err) {
      setHint(err instanceof Error ? err.message : "The transcription service had a problem and couldn't process your recording.");
      return null;
    }
  }, []);

  const onPauseResume = useCallback(async () => {
    if (busyRef.current) return;
    if (stage === "recording") {
      // Pause: checkpoint. Transcribe the segment so far, append it, park in PAUSED (editable).
      busyRef.current = true;
      elapsedBeforeRef.current += performance.now() - segmentStartRef.current;
      setStage("transcribing");
      setHint("Transcribing what you have said so far...");
      const segment = await transcribeCurrentSegment();
      if (segment !== null) {
        accumulatedRef.current = joinText(accumulatedRef.current, segment);
        setTranscript(accumulatedRef.current);
        setHint("");
      }
      setStage("paused");
      busyRef.current = false;
    } else if (stage === "paused") {
      // Resume: re-seed from the (possibly edited) box so edits survive, start a fresh segment.
      // Hold GETTING READY until the mic delivers audio again; onCaptureLive flips to RECORDING
      // (and re-cues), keeping the accumulated elapsed time.
      accumulatedRef.current = transcript;
      setHint("");
      setStage("connecting");
      armReadyBackstop();
      try {
        await recorderRef.current.start();
      } catch (err) {
        clearReadyBackstop();
        setHint("Could not resume - your text is kept. " + (err instanceof Error ? err.message : ""));
        setStage("paused");
      }
    }
  }, [stage, transcript, transcribeCurrentSegment, armReadyBackstop, clearReadyBackstop]);

  const commit = useCallback(
    async (action: (text: string) => void) => {
      if (busyRef.current) return;
      if (stage === "recording") {
        busyRef.current = true;
        elapsedBeforeRef.current += performance.now() - segmentStartRef.current;
        setStage("transcribing");
        setHint("Transcribing...");
        const segment = await transcribeCurrentSegment();
        if (segment === null) {
          // Do not commit partial/failed text - park in PAUSED with what we have.
          setStage("paused");
          busyRef.current = false;
          return;
        }
        accumulatedRef.current = joinText(accumulatedRef.current, segment);
        busyRef.current = false;
        action(accumulatedRef.current.trim());
        onClose();
      } else if (stage === "paused") {
        action(transcript.trim());
        onClose();
      }
    },
    [stage, transcript, transcribeCurrentSegment, onClose],
  );

  // Send. The whole point of this handler (mobile Speak should not hold the screen): when we are
  // still RECORDING and the host wired the fire-and-forget path (onSendAudio), grab the captured audio
  // buffer and hand it up, then close IMMEDIATELY - the host transcribes + submits in the background
  // and marks the session "Transcribing...". The user cannot see the transcript here anyway (Send
  // submits straight into the session), so waiting for it is pure dead time.
  //   * PAUSED: the text is already transcribed - submit it instantly, no audio round trip.
  //   * RECORDING without onSendAudio (Voice-mode reply panel): fall back to the blocking
  //     transcribe-then-submit commit so that surface is unchanged.
  const onSendClick = useCallback(async () => {
    if (busyRef.current) return;
    if (stage === "paused") {
      onSend(transcript.trim());
      onClose();
      return;
    }
    if (stage !== "recording") return;
    if (!onSendAudio) {
      await commit(onSend);
      return;
    }
    busyRef.current = true;
    elapsedBeforeRef.current += performance.now() - segmentStartRef.current;
    let captured: Blob;
    try {
      captured = await recorderRef.current.stop();
    } catch (err) {
      // Could not finalize the mic - keep the user's audio session alive in PAUSED rather than
      // dropping it, and surface why. (No text is lost; nothing was committed.)
      setHint(err instanceof Error ? err.message : "Could not capture the recording.");
      setStage("paused");
      busyRef.current = false;
      return;
    }
    onSendAudio({
      blob: captured,
      recordedMs: recorderRef.current.lastRecordedMs,
      prefixText: accumulatedRef.current,
    });
    onClose();
  }, [stage, transcript, onSend, onSendAudio, onClose, commit]);

  const onCancel = useCallback(() => {
    recorderRef.current.dispose();
    onClose();
  }, [onClose]);

  // Keyboard: Escape cancels; Enter sends except while editing the transcript box (newline there).
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        onCancel();
      }
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onCancel]);

  const isError = stage === "error";
  const isPaused = stage === "paused";
  const isTranscribing = stage === "transcribing";
  const isConnecting = stage === "connecting";
  // Nothing is captured yet while GETTING READY (and nothing is mid-transcription), so the
  // commit/checkpoint actions are not actionable then.
  const actionsDisabled = isTranscribing || isConnecting;
  const statusLabel =
    stage === "connecting"
      ? "GETTING READY"
      : stage === "recording"
        ? "RECORDING"
        : stage === "transcribing"
          ? "TRANSCRIBING"
          : stage === "paused"
            ? "PAUSED"
            : "ERROR";

  return (
    <div className="dictate-overlay" role="dialog" aria-modal="true" aria-label="Dictate">
      <div className="dictate-card">
        <div className="dictate-head">
          <span className={`dictate-status dictate-status-${stage}`}>{statusLabel}</span>
          <span className="dictate-timer">{formatElapsed(elapsed)}</span>
        </div>

        {!isError && (
          <div className="dictate-eq" aria-hidden="true">
            {levels.map((v, i) => (
              <span key={i} className="dictate-eq-well">
                <span
                  className={`dictate-eq-bar ${stage !== "recording" ? "dictate-eq-bar-idle" : ""}`}
                  style={{ height: `${8 + v * 84}%` }}
                />
              </span>
            ))}
          </div>
        )}

        <div className="dictate-hint" role="status">{hint}</div>

        {isError ? (
          <div className="dictate-error" role="alert">{errorText}</div>
        ) : (
          <textarea
            className="dictate-transcript"
            value={isPaused ? transcript : accumulatedRef.current}
            readOnly={!isPaused}
            onChange={(e) => setTranscript(e.target.value)}
            placeholder="Your words appear when you pause or finish - press Pause to see them so far."
          />
        )}

        <div className="dictate-actions">
          <button type="button" className="dictate-btn dictate-cancel" onClick={onCancel}>
            {isError ? "Close" : "Cancel"}
          </button>
          {!isError && (
            <button
              type="button"
              className="dictate-btn dictate-pause"
              onClick={onPauseResume}
              disabled={actionsDisabled}
            >
              {isPaused ? "Resume" : <span className="dictate-pause-glyph"><i /><i /></span>}
            </button>
          )}
          <span className="dictate-spacer" />
          {!isError && (
            <>
              {showInsert && onInsert && (
                <button
                  type="button"
                  className="dictate-btn dictate-insert"
                  onClick={() => commit(onInsert)}
                  disabled={actionsDisabled}
                >
                  Insert
                </button>
              )}
              <button
                type="button"
                className="dictate-btn dictate-send"
                onClick={onSendClick}
                disabled={actionsDisabled}
              >
                Send
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
