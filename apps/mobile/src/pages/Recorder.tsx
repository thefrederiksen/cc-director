import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  deleteRecording,
  getRecording,
  listChunks,
  listRecordings,
  recordingStoreAvailable,
  recoverInterrupted,
  saveChunk,
  saveRecording,
  type LocalRecording,
} from "@devthrottle/client-core/recorder/recordingStore";
import { SegmentRecorder, MAX_RECORDING_MS } from "@devthrottle/client-core/recorder/segmentRecorder";
import {
  driveRecordingUpload,
  resumePendingRecordingUploads,
  sha256Hex,
} from "@devthrottle/client-core/recorder/ingestUpload";
import { getInstallId } from "@devthrottle/client-core/auth/deviceKey";
import {
  getRecordings,
  getTranscript,
  recordingAudioUrl,
  type RecordingListItem,
} from "@devthrottle/client-core/recordings/recordingsClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";

// The Voice Recorder screen (issue #958) - the PWA successor of the retired native Android recorder,
// carrying its concepts over: rolling one-minute segments persisted durably AS THEY ARE CAPTURED (a
// crash loses at most the open segment), per-segment resume on upload, timestamped notes delivered on
// the complete call, a live level meter so you can see it is hearing you, a title editable until stop,
// and a library whose rows show TWO independent statuses - Uploaded and Transcribed - because a
// transcription failure never un-uploads the audio.
//
// The layout is the Android MainPage's, translated: one card with the big timer + state + segment
// count + level meter; the title entry; the big red Record button (Pause appears only while
// recording); the notes section; the library below with per-row determinate progress. Upload
// machinery stays out of the way of the recording controls.
//
// Recordings the user has sent are driven by client-core's durable upload driver and auto-retried on
// app load and when connectivity returns. Once the server acknowledges the complete call the local
// copy is deleted and the row is carried by the server's own list (GET /ingest/recordings), where its
// transcription state comes from - so the row reads the same before and after delivery.

const SERVER_POLL_MS = 5000;
const TICK_MS = 150;

/** hh:mm:ss, matching the Android recorder's big timer. */
function formatClock(ms: number): string {
  const totalS = Math.floor(ms / 1000);
  const h = Math.floor(totalS / 3600);
  const m = Math.floor((totalS % 3600) / 60);
  const s = totalS % 60;
  const two = (n: number) => String(n).padStart(2, "0");
  return `${two(h)}:${two(m)}:${two(s)}`;
}

function formatDuration(ms: number): string {
  const totalS = Math.round(ms / 1000);
  const m = Math.floor(totalS / 60);
  const s = totalS % 60;
  return m > 0 ? `${m} min ${s} s` : `${s} s`;
}

function formatWhen(iso: string): string {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? iso : d.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
}

function defaultTitle(): string {
  return `Recording ${new Date().toLocaleString([], { dateStyle: "medium", timeStyle: "short" })}`;
}

type Phase = "idle" | "starting" | "recording" | "paused" | "stopping";

/** One library row: a local (not yet delivered) recording OR a server-side one, never both - the
 *  local copy is deleted exactly when the server acknowledges it holds everything. */
interface LibraryRow {
  key: string;
  local?: LocalRecording;
  server?: RecordingListItem;
  startedAt: string;
}

/** SVG checkmark, drawn (not a text glyph) exactly like the Android recorder's Path. */
function Check({ on, tone }: { on: boolean; tone: "ok" | "dim" }) {
  return (
    <svg className={`rec-check ${on && tone === "ok" ? "rec-check-on" : ""}`} viewBox="0 0 13 11" aria-hidden="true">
      <path d="M1 5.5 L4.5 9 L12 1" fill="none" strokeWidth="2.5" strokeLinecap="round" />
    </svg>
  );
}

/** SVG red X for a failed transcription (the audio itself is safe on the Gateway). */
function Cross() {
  return (
    <svg className="rec-cross" viewBox="0 0 11 11" aria-hidden="true">
      <path d="M1 1 L10 10 M10 1 L1 10" fill="none" strokeWidth="2.5" strokeLinecap="round" />
    </svg>
  );
}

export function Recorder() {
  const storeOk = recordingStoreAvailable();

  const [phase, setPhase] = useState<Phase>("idle");
  const [elapsedMs, setElapsedMs] = useState(0);
  const [segmentCount, setSegmentCount] = useState(0);
  const [level, setLevel] = useState(0);
  const [title, setTitle] = useState("");
  const [noteText, setNoteText] = useState("");
  const [activeNotes, setActiveNotes] = useState<{ tMs: number; text: string }[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [autoStopped, setAutoStopped] = useState(false);

  const [localRecordings, setLocalRecordings] = useState<LocalRecording[]>([]);
  const [serverRecordings, setServerRecordings] = useState<RecordingListItem[]>([]);
  const [serverError, setServerError] = useState<string | null>(null);

  const [playingId, setPlayingId] = useState<string | null>(null);
  const [transcriptFor, setTranscriptFor] = useState<{ id: string; text: string } | null>(null);
  const [confirmDiscard, setConfirmDiscard] = useState<string | null>(null);

  const recorderRef = useRef<SegmentRecorder | null>(null);
  const activeRef = useRef<LocalRecording | null>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const playTokenRef = useRef(0);
  const titleRef = useRef("");
  titleRef.current = title;

  const refreshLocal = useCallback(async () => {
    setLocalRecordings(await listRecordings());
  }, []);

  const refreshServer = useCallback(async (signal?: AbortSignal) => {
    try {
      const items = await getRecordings(signal);
      setServerRecordings(items);
      setServerError(null);
    } catch (err) {
      if (signal?.aborted) return;
      setServerError(gatewayErrorMessage(err));
    }
  }, []);

  // Load: recover interrupted captures, list the library, resume any upload the user already asked
  // for (the durable resume-on-open pattern), and keep the server list fresh while the screen is up.
  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      await recoverInterrupted();
      await refreshLocal();
      await refreshServer(controller.signal);
      void resumePendingRecordingUploads(() => void refreshLocal()).then(() => {
        void refreshLocal();
        void refreshServer(controller.signal);
      });
    })();

    const poll = setInterval(() => void refreshServer(controller.signal), SERVER_POLL_MS);
    const onOnline = () => {
      void resumePendingRecordingUploads(() => void refreshLocal()).then(() => {
        void refreshLocal();
        void refreshServer(controller.signal);
      });
    };
    window.addEventListener("online", onOnline);
    return () => {
      controller.abort();
      clearInterval(poll);
      window.removeEventListener("online", onOnline);
      // Leaving the screen mid-capture stops cleanly: every finalized segment is already durable,
      // and stop() finalizes the open one before releasing the microphone.
      const rec = recorderRef.current;
      if (rec !== null) void rec.stop();
      const audio = audioRef.current;
      if (audio !== null) audio.pause();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // The live tick: timer, level meter, segment count - only while capturing.
  useEffect(() => {
    if (phase !== "recording" && phase !== "paused") return;
    const t = setInterval(() => {
      const rec = recorderRef.current;
      if (rec === null) return;
      setElapsedMs(rec.elapsedMs);
      setSegmentCount(rec.segmentCount);
      setLevel(rec.level());
    }, TICK_MS);
    return () => clearInterval(t);
  }, [phase]);

  const finalizeActive = useCallback(
    async (recovered: boolean) => {
      const rec = activeRef.current;
      if (rec === null) return;
      const fresh = await getRecording(rec.recordingId);
      if (fresh !== null) {
        if (fresh.segments === 0) {
          // Nothing was captured (stopped within the first second) - an empty recording can never
          // pass the server's completeness gate, so it is removed rather than shown as sendable.
          await deleteRecording(fresh.recordingId);
        } else {
          fresh.state = "ready";
          fresh.endedAt = new Date().toISOString();
          fresh.title = titleRef.current.trim() || fresh.title;
          if (recovered) fresh.recovered = true;
          await saveRecording(fresh);
        }
      }
      activeRef.current = null;
      recorderRef.current = null;
      setPhase("idle");
      setElapsedMs(0);
      setSegmentCount(0);
      setLevel(0);
      setActiveNotes([]);
      setTitle("");
      await refreshLocal();
    },
    [refreshLocal],
  );

  const startRecording = useCallback(async () => {
    if (!storeOk) return;
    setError(null);
    setAutoStopped(false);
    setPhase("starting");
    const recordingId = crypto.randomUUID();
    const rec: LocalRecording = {
      recordingId,
      title: titleRef.current.trim() || defaultTitle(),
      deviceId: getInstallId(),
      startedAt: new Date().toISOString(),
      endedAt: null,
      codec: "webm-opus",
      sampleRateHz: 48000,
      channels: 1,
      state: "recording",
      completed: false,
      segments: 0,
      durationMs: 0,
      notes: [],
      createdAt: Date.now(),
    };
    try {
      // The durable shell exists BEFORE the microphone opens - from here on, every finalized
      // segment lands in IndexedDB the moment the recorder rotates past it.
      await saveRecording(rec);
      activeRef.current = rec;
      setTitle(rec.title);
      setActiveNotes([]);

      const recorder = new SegmentRecorder({
        onSegment: async (seg) => {
          const sha = await sha256Hex(seg.blob);
          await saveChunk({
            recordingId,
            index: seg.index,
            blob: seg.blob,
            startMs: seg.startMs,
            durationMs: seg.durationMs,
            bytes: seg.blob.size,
            sha256: sha,
            uploaded: false,
          });
          const fresh = await getRecording(recordingId);
          if (fresh !== null) {
            fresh.segments = Math.max(fresh.segments, seg.index + 1);
            fresh.durationMs += seg.durationMs;
            fresh.title = titleRef.current.trim() || fresh.title;
            await saveRecording(fresh);
          }
        },
        onError: (message) => {
          setError(`Recording stopped: ${message}`);
          void finalizeActive(false);
        },
        onAutoStop: () => {
          setAutoStopped(true);
          void finalizeActive(false);
        },
      });
      recorderRef.current = recorder;
      await recorder.start();
      // Now that the browser has chosen the container, stamp the real codec + sample rate.
      rec.codec = recorder.codecLabel;
      rec.sampleRateHz = recorder.sampleRateHz;
      await saveRecording(rec);
      setPhase("recording");
    } catch (err) {
      recorderRef.current?.dispose();
      recorderRef.current = null;
      activeRef.current = null;
      await deleteRecording(recordingId);
      setError(err instanceof Error ? err.message : String(err));
      setPhase("idle");
    }
  }, [storeOk, finalizeActive]);

  const pauseResume = useCallback(async () => {
    const recorder = recorderRef.current;
    if (recorder === null) return;
    if (phase === "recording") {
      await recorder.pause();
      setPhase("paused");
    } else if (phase === "paused") {
      try {
        await recorder.resume();
        setPhase("recording");
      } catch (err) {
        setError(err instanceof Error ? err.message : String(err));
      }
    }
  }, [phase]);

  const stopRecording = useCallback(async () => {
    const recorder = recorderRef.current;
    if (recorder === null) return;
    setPhase("stopping");
    await recorder.stop();
    await finalizeActive(false);
  }, [finalizeActive]);

  const addNote = useCallback(async () => {
    const text = noteText.trim();
    const recorder = recorderRef.current;
    const active = activeRef.current;
    if (text === "" || recorder === null || active === null) return;
    const note = { tMs: Math.round(recorder.elapsedMs), text };
    const fresh = await getRecording(active.recordingId);
    if (fresh !== null) {
      fresh.notes = [...fresh.notes, note];
      await saveRecording(fresh);
      setActiveNotes(fresh.notes);
    }
    setNoteText("");
  }, [noteText]);

  const persistTitle = useCallback(async () => {
    const active = activeRef.current;
    if (active === null) return;
    const fresh = await getRecording(active.recordingId);
    if (fresh !== null) {
      fresh.title = titleRef.current.trim() || fresh.title;
      await saveRecording(fresh);
    }
  }, []);

  const sendRecording = useCallback(
    async (recordingId: string) => {
      const rec = await getRecording(recordingId);
      if (rec === null) return;
      rec.state = "queued";
      rec.lastError = undefined;
      await saveRecording(rec);
      await refreshLocal();
      void driveRecordingUpload(recordingId, () => void refreshLocal()).then(() => {
        void refreshLocal();
        void refreshServer();
      });
    },
    [refreshLocal, refreshServer],
  );

  const discardRecording = useCallback(
    async (recordingId: string) => {
      await deleteRecording(recordingId);
      setConfirmDiscard(null);
      await refreshLocal();
    },
    [refreshLocal],
  );

  const stopPlayback = useCallback(() => {
    playTokenRef.current += 1;
    const audio = audioRef.current;
    if (audio !== null) {
      audio.pause();
      audio.removeAttribute("src");
    }
    setPlayingId(null);
  }, []);

  /** Play a recording's segments in order - local blobs before delivery, Gateway audio after. */
  const playRecording = useCallback(
    async (row: LibraryRow) => {
      stopPlayback();
      const token = ++playTokenRef.current;
      const audio = audioRef.current ?? new Audio();
      audioRef.current = audio;
      setPlayingId(row.key);

      const sources: string[] = [];
      const revoke: string[] = [];
      if (row.local !== undefined) {
        const chunks = await listChunks(row.local.recordingId);
        for (const c of chunks) {
          const url = URL.createObjectURL(c.blob);
          sources.push(url);
          revoke.push(url);
        }
      } else if (row.server !== undefined) {
        for (let i = 0; i < row.server.segments; i++) {
          sources.push(recordingAudioUrl(row.server.recordingId, i));
        }
      }

      const cleanup = () => {
        for (const url of revoke) URL.revokeObjectURL(url);
      };
      let index = 0;
      const playNext = () => {
        if (playTokenRef.current !== token || index >= sources.length) {
          cleanup();
          if (playTokenRef.current === token) setPlayingId(null);
          return;
        }
        audio.src = sources[index];
        index += 1;
        audio.onended = playNext;
        audio.onerror = () => {
          cleanup();
          if (playTokenRef.current === token) {
            setPlayingId(null);
            setError("Playback failed - this segment could not be decoded.");
          }
        };
        void audio.play().catch(() => {
          cleanup();
          if (playTokenRef.current === token) setPlayingId(null);
        });
      };
      playNext();
    },
    [stopPlayback],
  );

  const toggleTranscript = useCallback(
    async (recordingId: string) => {
      if (transcriptFor?.id === recordingId) {
        setTranscriptFor(null);
        return;
      }
      const text = await getTranscript(recordingId);
      setTranscriptFor({ id: recordingId, text: text ?? "The transcript could not be loaded." });
    },
    [transcriptFor],
  );

  // ===== library rows ====================================================================

  const serverOnly = serverRecordings.filter(
    (s) => !localRecordings.some((l) => l.recordingId === s.recordingId),
  );
  const rows: LibraryRow[] = [
    ...localRecordings
      .filter((l) => l.state !== "recording")
      .map((l) => ({ key: l.recordingId, local: l, startedAt: l.startedAt })),
    ...serverOnly.map((s) => ({ key: s.recordingId, server: s, startedAt: s.startedAt })),
  ].sort((a, b) => (a.startedAt < b.startedAt ? 1 : -1));

  const recording = phase === "recording" || phase === "paused";
  const remainingMs = Math.max(0, MAX_RECORDING_MS - elapsedMs);

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Voice Recorder</h1>
      </header>

      {!storeOk && (
        <div className="banner banner-error" role="alert">
          This browser cannot store recordings durably (no IndexedDB), so recording is disabled here.
          Open DevThrottle in a normal browser tab to record.
        </div>
      )}

      {error !== null && (
        <div className="banner banner-error" role="alert">
          {error}
        </div>
      )}

      {autoStopped && (
        <div className="banner rec-banner-info" role="status">
          Recording reached the 30 minute limit and was stopped. Everything captured is saved below.
        </div>
      )}

      {/* Timer + state + segments + live level, one card - the Android recorder's layout. */}
      <section className="rec-card" aria-label="Recording status">
        <div className="rec-timer">{formatClock(elapsedMs)}</div>
        <div className={`rec-state ${recording && phase !== "paused" ? "rec-state-live" : ""}`}>
          {phase === "idle" && "Idle"}
          {phase === "starting" && "Opening microphone..."}
          {phase === "recording" && "Recording"}
          {phase === "paused" && "Paused"}
          {phase === "stopping" && "Saving..."}
        </div>
        <div className="rec-segments">
          {segmentCount} segment{segmentCount === 1 ? "" : "s"} captured
          {recording && remainingMs < 5 * 60_000 && ` - ${formatDuration(remainingMs)} left`}
        </div>
        <div className="rec-level" aria-hidden="true">
          <div className="rec-level-fill" style={{ width: `${Math.round(level * 100)}%` }} />
        </div>
      </section>

      <input
        className="rec-title"
        type="text"
        placeholder="Title (optional, edit anytime until you stop)"
        value={title}
        onChange={(e) => setTitle(e.target.value)}
        onBlur={() => void persistTitle()}
        disabled={phase === "stopping"}
      />

      {!recording ? (
        <button
          type="button"
          className="rec-record-btn"
          onClick={() => void startRecording()}
          disabled={!storeOk || phase === "starting" || phase === "stopping"}
        >
          {phase === "starting" ? "Starting..." : "Record"}
        </button>
      ) : (
        <div className="rec-controls">
          <button type="button" className="rec-pause-btn" onClick={() => void pauseResume()}>
            {phase === "paused" ? "Resume" : "Pause"}
          </button>
          <button type="button" className="rec-stop-btn" onClick={() => void stopRecording()}>
            Stop
          </button>
        </div>
      )}

      {recording && (
        <section className="rec-notes" aria-label="Notes while recording">
          <div className="rec-section-title">Notes while recording</div>
          <div className="rec-note-row">
            <input
              className="rec-note-input"
              type="text"
              placeholder="Type a note, hit Add"
              value={noteText}
              onChange={(e) => setNoteText(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") void addNote();
              }}
            />
            <button type="button" className="rec-note-add" onClick={() => void addNote()} disabled={noteText.trim() === ""}>
              Add
            </button>
          </div>
          {activeNotes.length > 0 && (
            <ul className="rec-note-list">
              {activeNotes.map((n, i) => (
                <li key={i}>
                  <span className="rec-note-t">{formatClock(n.tMs)}</span> {n.text}
                </li>
              ))}
            </ul>
          )}
        </section>
      )}

      <section className="rec-library" aria-label="Recordings">
        <div className="rec-section-title">Recordings</div>
        <div className="rec-section-hint">
          Kept on your phone until the Gateway confirms delivery, then transcribed there. Each row shows
          its own progress; transcripts appear on the Cockpit&apos;s Voice Recorder page too.
        </div>

        {serverError !== null && (
          <div className="banner banner-error" role="alert">
            {serverError}
          </div>
        )}

        {rows.length === 0 && serverError === null && (
          <div className="rec-empty">No recordings yet. Hit Record, talk, stop, and send.</div>
        )}

        {rows.map((row) => {
          const l = row.local;
          const s = row.server;
          const titleText = l?.title ?? s?.title ?? "";
          const when = formatWhen(row.startedAt);
          const durMs = l?.durationMs ?? s?.durationMs ?? 0;
          const segs = l?.segments ?? s?.segments ?? 0;
          const uploading = l?.state === "uploading";
          const uploadedOn = s !== undefined; // a server row exists only after full delivery
          const transcribedOn = s !== undefined && (s.state === "transcribed" || s.state === "filed");
          const transcribeFailed = s !== undefined && s.state === "error";
          const transcribing =
            s !== undefined && (s.state === "queued" || s.state === "transcribing" || s.state === "cleaning");
          const isPlaying = playingId === row.key;

          return (
            <div className="rec-row" key={row.key}>
              <div className="rec-row-title">{titleText}</div>
              <div className="rec-row-sub">
                {when}
                {durMs > 0 && ` - ${formatDuration(durMs)}`}
                {segs > 0 && ` - ${segs} segment${segs === 1 ? "" : "s"}`}
                {l?.recovered && " - recovered after the app closed while recording"}
              </div>

              {l !== undefined && l.state === "ready" && (
                <div className="rec-row-note">Saved on this phone - ready to send.</div>
              )}
              {l !== undefined && l.state === "queued" && <div className="rec-row-note">Waiting to send...</div>}
              {uploading && (
                <>
                  <div className="rec-row-note">
                    Sending segment {Math.min((l?.uploadCurrent ?? 0) + 1, l?.uploadTotal ?? 1)}/{l?.uploadTotal ?? 0}
                  </div>
                  <div className="rec-progress">
                    <div
                      className="rec-progress-fill"
                      style={{
                        width: `${(l?.uploadTotal ?? 0) > 0 ? Math.round(((l?.uploadCurrent ?? 0) / (l?.uploadTotal ?? 1)) * 100) : 0}%`,
                      }}
                    />
                  </div>
                </>
              )}
              {l !== undefined && l.state === "uploaded" && !l.completed && (
                <div className="rec-row-note">Audio uploaded - finishing delivery...</div>
              )}
              {l !== undefined && l.state === "retry" && (
                <div className="rec-row-err">
                  {l.lastError ?? "Send failed - the recording is saved on this phone and will be retried."}
                </div>
              )}
              {transcribeFailed && (
                <div className="rec-row-err">
                  Transcription failed on the Gateway. The audio is safely uploaded and can be retried there.
                </div>
              )}

              {/* Two independent statuses, never conflated (Android principle #3). */}
              <div className="rec-row-status">
                <span className="rec-status-item">
                  <Check on={uploadedOn} tone="ok" />
                  Uploaded
                </span>
                <span className="rec-status-item">
                  {transcribeFailed ? <Cross /> : <Check on={transcribedOn} tone="ok" />}
                  {transcribing ? "Transcribing..." : "Transcribed"}
                </span>
              </div>

              <div className="rec-row-actions">
                {(l !== undefined || (s !== undefined && s.segments > 0)) && (
                  <button
                    type="button"
                    className="rec-row-btn"
                    onClick={() => (isPlaying ? stopPlayback() : void playRecording(row))}
                  >
                    {isPlaying ? "Stop playback" : "Play"}
                  </button>
                )}
                {l !== undefined && l.state === "ready" && (
                  <button type="button" className="rec-row-btn rec-row-btn-primary" onClick={() => void sendRecording(l.recordingId)}>
                    Send
                  </button>
                )}
                {l !== undefined && l.state === "retry" && (
                  <button type="button" className="rec-row-btn rec-row-btn-primary" onClick={() => void sendRecording(l.recordingId)}>
                    Retry
                  </button>
                )}
                {l !== undefined && (l.state === "ready" || l.state === "retry") && (
                  confirmDiscard === l.recordingId ? (
                    <>
                      <button type="button" className="rec-row-btn rec-row-btn-danger" onClick={() => void discardRecording(l.recordingId)}>
                        Really discard
                      </button>
                      <button type="button" className="rec-row-btn" onClick={() => setConfirmDiscard(null)}>
                        Keep
                      </button>
                    </>
                  ) : (
                    <button type="button" className="rec-row-btn" onClick={() => setConfirmDiscard(l.recordingId)}>
                      Discard
                    </button>
                  )
                )}
                {transcribedOn && (
                  <button type="button" className="rec-row-btn" onClick={() => void toggleTranscript(row.key)}>
                    {transcriptFor?.id === row.key ? "Hide transcript" : "View transcript"}
                  </button>
                )}
              </div>

              {transcriptFor?.id === row.key && <div className="rec-transcript">{transcriptFor.text}</div>}
            </div>
          );
        })}
      </section>
    </div>
  );
}
