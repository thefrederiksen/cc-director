import { useCallback, useEffect, useRef, useState, type ChangeEvent, type SyntheticEvent } from "react";
import {
  GatewayError,
  getWingmanVoice,
  listSessions,
  markVoiceAndExplain,
  sendPrompt,
  setVoiceMode,
  stopWingmanVoice,
  type SessionDto,
  type WingmanVoice,
} from "../api/client";
import { backgroundTranscribeAndSend, type CapturedUtterance } from "../dictation/backgroundSend";
import { ensureClip, getClipState, getVoiceMeta, saveVoiceMeta, stopPlayback, useVoiceClips, type ClipPhase } from "./clips";
import { positionFor, saveMark, wasAutoPlayed } from "./playbackPositions";
import { isAudioUnavailable } from "./voiceAvailability";
import { isWorking } from "../sessions/ordering";

// Session Voice mode (issue #850): the hands-free Wingman narration screen, the third session view
// alongside Terminal (#817) and Chat (#811). A read-only Wingman narrates every completed turn as
// audio; the Gateway renders + caches the clip at turn-end (proven path, GatewayHost turn-end
// watcher), and this screen downloads it to the phone before offering playback - the one new rule
// is "phone-ready, not just gateway-ready" (see voice/clips.ts and docs/architecture/mobile/voice-mode.html).
//
// Screen states: off (one "Switch to voice mode" button), working (Wingman reading + phone
// downloading), and speaking (clip plays, narrative shown, Respond). Voice mode is HANDS-FREE (issue
// #947): a waiting choice is NOT rendered as tappable option buttons - the Wingman speaks the question
// and its options in the narration and you answer by voice through Respond. Reply is the existing
// dictation interface with Cancel / Pause / Send and NO Insert; Send transcribes and goes straight
// into the session via the same POST /prompt path.
//
// The presentational shell (the mobile VoiceMode page, and the Cockpit Voice tab) is a thin view over
// this hook: it owns none of the state, the poll, the state-machine derivation, the playback handlers,
// or the clip management - all of that lives here so both shells render the identical screen from the
// same shared logic (issue #1213, plan phase 4).

const VOICE_POLL_MS = 3000; // a slightly faster poll than the 5s roster, only while this screen is open

// Issue #951: the 3s poll used to push a fresh object into state on every tick, re-rendering the whole
// Voice screen even when nothing had changed (flashing narration, a jumping player row). These compare
// only the fields the screen actually renders/derives from, so the poll can return the PREVIOUS state
// object when it is unchanged and React skips the re-render. Volatile fields the screen never uses
// (heartbeat timestamps, etc.) are intentionally ignored so they do not force a redraw.
function sameSessionForVoice(a: SessionDto | null, b: SessionDto | null): boolean {
  if (a === b) return true;
  if (a === null || b === null) return false;
  return a.sessionId === b.sessionId
    && a.name === b.name
    && Boolean(a.voiceMode) === Boolean(b.voiceMode)
    && Boolean(a.onHold) === Boolean(b.onHold)
    && Boolean(a.voiceGenerating) === Boolean(b.voiceGenerating)
    && Boolean(a.voiceAudioReady) === Boolean(b.voiceAudioReady)
    // The reason voice is unavailable is part of what the screen shows, so a CHANGE in it has to count
    // as a change. Leaving it out meant the poll could carry a fresh reason every 3 seconds and this
    // guard would declare the session identical, so the screen never re-rendered to show it.
    && (a.voiceUnavailable?.state ?? null) === (b.voiceUnavailable?.state ?? null)
    && a.statusColor === b.statusColor
    && a.assessedState === b.assessedState
    && a.activityState === b.activityState;
}

function sameVoice(a: WingmanVoice | null, b: WingmanVoice | null): boolean {
  if (a === b) return true;
  if (a === null || b === null) return false;
  return a.ready === b.ready && a.generatedAt === b.generatedAt
    && a.spoken === b.spoken && a.reply === b.reply;
}

// mm:ss for the playback clock (display only).
export function formatClock(seconds: number): string {
  if (!isFinite(seconds) || seconds < 0) return "0:00";
  const total = Math.floor(seconds);
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}:${String(s).padStart(2, "0")}`;
}

// Everything the presentational Voice view needs to render the identical screen: the derived
// state-machine booleans, the display fields, and every handler. The view owns only JSX and class
// names; all behavior lives behind these members.
export interface VoiceModeView {
  voiceOn: boolean;
  speaking: boolean;
  working: boolean;
  audioUnavailable: boolean;
  /** WHY voice is unavailable, as the Gateway reported it (null when it has nothing to say). Render
   *  this instead of guessing - the guess was wrong during a real outage. */
  unavailableReason: { state?: string; text?: string; ctaLabel?: string; ctaAction?: string; ctaUrl?: string | null } | null;
  /** The hosted service is down: not the user's fault, nothing for them to press, already retrying. */
  unavailableIsServiceDown: boolean;
  gatewayPreparing: boolean;
  phoneDownloadPending: boolean;
  agentWorking: boolean;
  pollDone: boolean;
  narrative: string;
  title: string;
  name: string | null;
  error: string | null;
  resumed: boolean;
  playing: boolean;
  pos: number;
  dur: number;
  enabling: boolean;
  regenerating: boolean;
  enableNote: string;
  responding: boolean;
  setResponding: (value: boolean) => void;
  setPlaying: (value: boolean) => void;
  /** A playable object URL for the locally-stored clip bytes, or null when nothing is held. */
  clipUrl: string | null;
  /** The current clip download phase, so the view can show the download-failed recovery copy. */
  clipPhase: ClipPhase;
  onSwitchOn: () => Promise<void>;
  onSwitchOff: () => Promise<void>;
  onGenerateNow: () => Promise<void>;
  setAudioEl: (el: HTMLAudioElement | null) => void;
  onLoadedMeta: (e: SyntheticEvent<HTMLAudioElement>) => void;
  onTimeUpdate: (e: SyntheticEvent<HTMLAudioElement>) => void;
  onEndedAudio: (e: SyntheticEvent<HTMLAudioElement>) => void;
  onSeek: (e: ChangeEvent<HTMLInputElement>) => void;
  onRestart: () => void;
  onTogglePlay: () => void;
  onRespondSend: (text: string) => Promise<void>;
  onRespondSendAudio: (captured: CapturedUtterance) => void;
}

export function useVoiceMode(
  sessionId: string | undefined,
  opts?: { seededVoiceOn?: boolean; autoSwitchOn?: boolean },
): VoiceModeView {
  const sid = sessionId ?? "";

  // The roster hands the known voice-mode state on navigation (issue #1015), so the screen renders
  // the right state on the FIRST paint instead of flashing OFF while its first poll resolves. The
  // view reads this seed from its router location and passes it in - the hook stays router-free.
  const seededVoiceOn = opts?.seededVoiceOn;
  // Arriving from "Switch to voice mode" in another screen's overflow menu: turn voice on for this
  // session on arrival, so the menu item does the whole job. The menu deliberately does NOT make those
  // calls itself - onSwitchOn below is the ONE implementation of that verb, and a second copy in a menu
  // handler would be free to drift from it.
  const autoSwitchOn = opts?.autoSwitchOn;

  const [name, setName] = useState<string | null>(null);
  const [session, setSession] = useState<SessionDto | null>(null);
  // Seed the narration state + text from the on-device cache so it shows instantly (issue #1015).
  const [voice, setVoice] = useState<WingmanVoice | null>(() => getVoiceMeta(sid));
  const [error, setError] = useState<string | null>(null);
  // Whether a real poll has resolved the true state yet. Until it has, we never paint the OFF card -
  // the screen starts blank (or ON when the roster seeded it) and only shows OFF once confirmed.
  // NOTE: this is knowledge of the SESSION only. It says nothing about the voice - it is set as soon
  // as listSessions resolves, while the getWingmanVoice call below has not even been made yet. The
  // unavailable verdict used to read it as if it meant both, which is why the screen announced "no
  // narration" and then played the narration; voiceKnown is the fact it actually needed.
  const [pollDone, setPollDone] = useState(false);
  // Whether the getWingmanVoice fetch has RESOLVED for this session - i.e. we have actually asked
  // about the voice and been answered. Only then may the screen say voice is unavailable.
  const [voiceKnown, setVoiceKnown] = useState(false);

  // A different session is a different question: forget what we knew about the last one's voice, so
  // its answer can never be used to declare THIS session's narration missing.
  useEffect(() => {
    setVoiceKnown(false);
  }, [sid]);

  // Optimistic "this session is now a voice session" the instant Switch is tapped, so the screen
  // moves to the working state immediately (responsive UI) before the roster reflects voiceMode.
  const [localEnabled, setLocalEnabled] = useState(false);
  const localEnabledRef = useRef(false);
  useEffect(() => {
    localEnabledRef.current = localEnabled;
  }, [localEnabled]);

  const [enabling, setEnabling] = useState(false);
  const [enableNote, setEnableNote] = useState<string>(""); // "nothing to summarize yet" message
  const [responding, setResponding] = useState(false);
  const [regenerating, setRegenerating] = useState(false); // the manual "Generate narration now" recovery is running

  // Re-render this screen when a clip download completes (phone-ready).
  useVoiceClips();
  const clip = getClipState(sid);

  // Warm the clip from the on-device cache (Cache Storage, no network) so a cold entry can show and
  // start playback instantly when the roster already prefetched it (issue #1015).
  useEffect(() => {
    const meta = getVoiceMeta(sid);
    if (meta?.ready && meta.generatedAt) void ensureClip(sid, meta.generatedAt);
  }, [sid]);

  // ----- playback element + progress (display only) -----
  const audioRef = useRef<HTMLAudioElement | null>(null);
  // A ref that keeps the LAST audio element even after unmount (React nulls audioRef on unmount), so
  // the cleanup effect below can still stop playback when you navigate away from this screen.
  const liveAudioRef = useRef<HTMLAudioElement | null>(null);
  const setAudioEl = useCallback((el: HTMLAudioElement | null) => {
    audioRef.current = el;
    if (el !== null) liveAudioRef.current = el;
  }, []);
  // The generatedAt whose saved position we have already restored onto the current audio element, so
  // we seek to the remembered spot exactly once per (session, clip) mount and never fight the user's
  // own seeking/restart afterwards (issue #1003 per-session resume).
  const restoredForRef = useRef<string>("");
  const [pos, setPos] = useState(0);
  const [dur, setDur] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [resumed, setResumed] = useState(false); // showed "picked up where you left off" this mount

  // Stop the narration when you LEAVE the voice screen (roster, another view tab, the drawer) so it
  // never keeps talking after you have moved on - the bug this fixes. Runs once, on unmount.
  useEffect(() => {
    return () => {
      // Stop BOTH audio sinks on leave: this screen's own element and any roster clip playing through
      // the shared clip player - so nothing keeps talking after you navigate away.
      stopPlayback();
      const el = liveAudioRef.current;
      if (el !== null) {
        try {
          el.pause();
        } catch {
          /* element already torn down */
        }
      }
    };
  }, []);

  // ----- the single poll that drives every state (session flags + voice) -----
  const poll = useCallback(
    async (signal: AbortSignal) => {
      try {
        const all = await listSessions(signal);
        const match = all.find((s) => s.sessionId === sid);

        if (!match) {
          // The session is momentarily ABSENT from /sessions - almost always the owning computer is
          // briefly unreachable, not a real "voice off" (issue #1333). Keep the last-known session and
          // whatever is playing instead of nulling it, which would collapse the Voice screen to the off
          // card mid-listen. A successful poll whose list simply omits this session is a transient gap,
          // NOT authority to turn voice off - only the branch below (the session IS reported, with
          // voiceMode=false) does that. Surface a soft reconnecting note; the next good poll clears it.
          setPollDone(true);
          setError("Reconnecting to this session's computer...");
          return;
        }

        // Only re-render when a field the screen uses actually changed (issue #951).
        setSession((prev) => (sameSessionForVoice(prev, match) ? prev : match));
        if (match.name && match.name.trim()) setName(match.name.trim());
        setPollDone(true); // the true state is now known - OFF may render from here on if applicable

        const on = localEnabledRef.current || Boolean(match.voiceMode);
        if (!on) {
          setVoice(null);
          setError(null);
          return;
        }

        const v = await getWingmanVoice(sid, signal);
        setVoice((prev) => (sameVoice(prev, v) ? prev : v));
        setVoiceKnown(true); // we have now ASKED about the voice, so a "no narration" verdict is allowed
        saveVoiceMeta(sid, v); // keep the cached state + text fresh for the next instant entry (#1015)
        // Kick the phone-side download the moment a (new) clip is ready on the Gateway.
        if (v.ready && v.generatedAt) void ensureClip(sid, v.generatedAt);
        setError(null);
      } catch (err) {
        if (signal.aborted) return;
        // Background poll: keep the last-known view on screen and surface a soft note; the next tick
        // retries. (Mirrors the roster's keep-last-known behavior - not a degraded fallback.)
        setError(err instanceof Error ? err.message : "Voice update failed");
      }
    },
    [sid],
  );

  useEffect(() => {
    const controller = new AbortController();
    void poll(controller.signal);
    const timer = window.setInterval(() => void poll(controller.signal), VOICE_POLL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [poll]);

  const generatedAt = voice?.generatedAt ?? "";
  const phoneReady =
    generatedAt.length > 0 && clip.phase === "ready" && clip.generatedAt === generatedAt && clip.url !== null;

  // Whether the agent has resumed working (blue) - the instant it does, the finished-turn narration is
  // stale and must not be spoken or offered, exactly like the roster play-triangle (Home.tsx). A null
  // session (not yet loaded) is treated as not-working.
  const agentWorking = session !== null && isWorking(session);

  // Retire a stale narration the moment the agent starts working again: stop this screen's own
  // playback and any shared roster clip. The speaking-state play-triangle is hidden below while
  // working, so a stale clip can neither keep talking nor be replayed until the next turn parks the
  // session again.
  useEffect(() => {
    if (!agentWorking) return;
    try {
      audioRef.current?.pause();
    } catch {
      /* nothing playing */
    }
    stopPlayback();
  }, [agentWorking]);

  // Auto-play a freshly downloaded clip exactly once, and only while the voice screen is foreground
  // (decision 4: never auto-play from the list, never while the app is hidden). Never auto-play while
  // the agent is working again - that narration is stale (the same rule the roster triangle follows).
  useEffect(() => {
    if (agentWorking) return;
    // NEVER speak while the listener is answering. This effect fires when a NEW clip finishes
    // downloading, and it had no idea the Respond dialog was open - so a turn that landed mid-dictation
    // played straight over the owner while their microphone was live, which is both maddening and an
    // echo source. Answering is a conversation: the other party stops talking.
    if (responding) return;
    if (!phoneReady || generatedAt.length === 0) return;
    if (typeof document !== "undefined" && document.hidden) return;
    // Only auto-play a genuinely NEW clip: one this device has not auto-played AND that has no
    // remembered position for this session. A clip you already listened part-way is restored to its
    // saved spot (onLoadedMeta) and waits for you to press play - returning to a session, or flipping
    // back to it, must never restart the narration from the top (issue #1003).
    if (wasAutoPlayed(sid, generatedAt)) return;
    if (positionFor(sid, generatedAt) > 0) return;
    const el = audioRef.current;
    if (el) {
      stopPlayback(); // never overlap a roster clip with this screen's playback
      el.currentTime = 0;
      saveMark(sid, { generatedAt, pos: 0, dur: el.duration || 0, autoPlayed: true });
      void el.play().catch(() => {
        /* autoplay policy may require a gesture; the play-triangle covers it */
      });
    }
  }, [phoneReady, generatedAt, agentWorking, sid, responding]);

  // Tapping Respond SILENCES whatever is already speaking, at once. The guard above stops a new clip
  // from starting, but a clip already mid-sentence when Respond is tapped would otherwise keep talking
  // into the open microphone. Both halves are needed: one stops it starting, this one stops it
  // continuing. Both audio sinks are stopped - this screen's element and the shared roster player -
  // because either can be the one talking.
  useEffect(() => {
    if (!responding) return;
    try {
      audioRef.current?.pause();
    } catch {
      /* nothing playing */
    }
    stopPlayback();
  }, [responding]);

  // A new turn's clip resets the per-mount resume guards so its saved position (0) restores cleanly.
  useEffect(() => {
    lastSavedSecRef.current = -1;
    setResumed(false);
  }, [generatedAt]);

  const onSwitchOn = useCallback(async () => {
    if (sid.length === 0 || enabling) return;
    setEnabling(true);
    setError(null);
    setLocalEnabled(true); // show the working screen immediately (responsive UI)
    // Voice was just switched on, so nothing has asked about THIS session's narration yet. Without
    // this the old answer ("off", hence no voice) survived the switch and the screen flashed the red
    // unavailable banner over the top of the narration it had just asked the Gateway to make.
    setVoiceKnown(false);
    try {
      // Two steps, matching the native phone app's enter-voice flow: first mark the session a Voice
      // session on the owning Director (ViewMode=Voice) so SessionDto.VoiceMode flips true and the
      // state persists across navigation and shows on the roster; then explain on the Gateway, which
      // marks its turn-end re-narration set and reads the first turn (caching the spoken text + audio).
      await setVoiceMode(sid, true);
      const explained = await markVoiceAndExplain(sid);
      // A fresh/text-only session has nothing to read yet - show its truthful note in the working
      // card instead of spinning forever waiting for audio that will not come until the next turn.
      setEnableNote(explained.nothingYet ? explained.spoken : "");
    } catch (err) {
      setLocalEnabled(false); // the enable did not take - fall back to the off screen, no half state
      setError(err instanceof Error ? err.message : "Could not switch to voice mode");
    } finally {
      setEnabling(false);
    }
  }, [sid, enabling]);

  const onSwitchOff = useCallback(async () => {
    if (sid.length === 0) return;
    // Stop any narration that is playing right now, then revert the screen to off immediately
    // (responsive UI) and tell the owning Director to leave voice (ViewMode=Text) - the same call the
    // native app's ClearVoiceMode makes. The optimistic session edit flips voiceOn false now; the next
    // poll confirms it.
    try {
      audioRef.current?.pause();
    } catch {
      /* nothing playing */
    }
    stopPlayback(); // stop any roster clip too
    setLocalEnabled(false);
    setVoice(null);
    setVoiceKnown(false);
    setEnableNote("");
    setSession((prev) => (prev ? { ...prev, voiceMode: false } : prev));
    restoredForRef.current = "";
    setResumed(false);
    try {
      // Two calls, matching the on path's two: tell the Director to leave voice (roster flag) AND
      // tell the Gateway to stop keeping voice (stops the per-turn Opus + text-to-speech, issue #859).
      await setVoiceMode(sid, false);
      await stopWingmanVoice(sid);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not turn voice off");
    }
  }, [sid]);

  // Manual recovery when the screen is stuck on "Voice unavailable": generate the narration on
  // demand. This is the SAME proven server path entering voice mode uses (POST /wingman/explain) -
  // the Gateway reads this session's latest completed turn, translates it, synthesizes the audio,
  // and caches it, so the next poll finds it ready and plays it. Nothing is sent into the session.
  // It exists because the automatic turn-end/idle-sweep generation can silently produce nothing -
  // the owning Director was unreachable, the session had not produced a turn yet, or a transient
  // text-to-speech failure - and there was no way to ask for it again (see the voice-unavailable
  // troubleshooting issue).
  const onGenerateNow = useCallback(async () => {
    if (sid.length === 0 || regenerating) return;
    setRegenerating(true);
    setError(null);
    try {
      const explained = await markVoiceAndExplain(sid);
      // Nothing to narrate yet (a fresh/text-only session): show the truthful note, which moves the
      // screen to the working card. Otherwise the clip is now cached and the poll picks it up.
      setEnableNote(explained.nothingYet ? explained.spoken : "");
    } catch (err) {
      // A 402 (out of credits / no key) already raised the shared app-level credits notice and its
      // message is the shared copy. An offline owning Director resolves to 404 here - say so plainly
      // instead of leaking the raw status, because it is the most common reason generation fails.
      //
      // 502 means the same thing and was NOT handled: the Gateway reaches a session's Director down its
      // stream, and when that Director is not stream-connected the endpoint answers 502 (issue #1177).
      // Measured across the live fleet on 2026-07-15: of 16 sessions, one answered 502 and two answered
      // 404 - all three the same story, "that computer is not reachable" - but only the 404s got the
      // plain-English line and the 502 leaked a raw message. Same cause, same sentence.
      if (err instanceof GatewayError && (err.status === 404 || err.status === 502)) {
        setError("This session's computer looks offline. Voice can't be generated until it reconnects.");
      } else {
        setError(err instanceof Error ? err.message : "Could not generate narration");
      }
    } finally {
      setRegenerating(false);
    }
  }, [sid, regenerating]);

  // The whole second we last persisted, so onTimeUpdate saves the position roughly once a second
  // rather than on every ~4Hz tick (issue #1003 per-session resume).
  const lastSavedSecRef = useRef<number>(-1);

  // Persist how far this session has listened. `started` marks the clip as having begun on this
  // device, so returning to the session resumes (never auto-restarts). autoPlayed, once set, stays set.
  const persist = useCallback(
    (el: HTMLAudioElement, opts?: { started?: boolean }) => {
      if (generatedAt.length === 0) return;
      const autoPlayed = wasAutoPlayed(sid, generatedAt) || Boolean(opts?.started);
      saveMark(sid, { generatedAt, pos: el.currentTime, dur: el.duration || 0, autoPlayed });
    },
    [sid, generatedAt],
  );

  // Metadata is loaded: publish the duration and, exactly once per (session, clip), restore the
  // remembered position so you pick up where you left off. Guarded so it never fights a later seek.
  const onLoadedMeta = useCallback(
    (e: SyntheticEvent<HTMLAudioElement>) => {
      const el = e.currentTarget;
      setDur(el.duration || 0);
      if (generatedAt.length === 0 || restoredForRef.current === generatedAt) return;
      restoredForRef.current = generatedAt;
      const saved = positionFor(sid, generatedAt);
      if (saved > 0 && el.duration > 0 && saved < el.duration - 0.5) {
        el.currentTime = saved;
        setPos(saved);
        setResumed(true);
      }
    },
    [sid, generatedAt],
  );

  // Playback progressed: reflect it and save the position about once per second.
  const onTimeUpdate = useCallback(
    (e: SyntheticEvent<HTMLAudioElement>) => {
      const el = e.currentTarget;
      setPos(el.currentTime);
      const sec = Math.floor(el.currentTime);
      if (sec !== lastSavedSecRef.current) {
        lastSavedSecRef.current = sec;
        persist(el);
      }
    },
    [persist],
  );

  const onEndedAudio = useCallback(
    (e: SyntheticEvent<HTMLAudioElement>) => {
      const el = e.currentTarget;
      setPos(el.duration || 0);
      setPlaying(false);
      persist(el); // remember it was listened to the end
    },
    [persist],
  );

  // Drag the slider to listen from anywhere.
  const onSeek = useCallback(
    (e: ChangeEvent<HTMLInputElement>) => {
      const el = audioRef.current;
      if (!el) return;
      const t = Number(e.target.value);
      el.currentTime = t;
      setPos(t);
      setResumed(false);
      persist(el);
    },
    [persist],
  );

  // Restart the clip from the beginning ("listen again from scratch").
  const onRestart = useCallback(() => {
    const el = audioRef.current;
    if (!el) return;
    stopPlayback();
    el.currentTime = 0;
    setPos(0);
    setResumed(false);
    persist(el, { started: true });
    void el.play().catch(() => {
      /* ignore - a tap already gestured */
    });
  }, [persist]);

  // Play/pause toggle - the clear "stop the speech" control the person asked for. Tapping while it is
  // speaking pauses it immediately; tapping when paused resumes from where it left off (or replays
  // from the start once it has ended).
  const onTogglePlay = useCallback(() => {
    const el = audioRef.current;
    if (!el) return;
    if (!el.paused) {
      el.pause();
      stopPlayback(); // also stop any roster clip playing through the shared player
      persist(el);
      return;
    }
    stopPlayback(); // never let two clips play at once
    if (el.ended || (el.duration > 0 && el.currentTime >= el.duration)) el.currentTime = 0;
    persist(el, { started: true });
    void el.play().catch(() => {
      /* ignore - a tap already gestured */
    });
  }, [persist]);

  const onRespondSend = useCallback(
    async (text: string) => {
      setResponding(false);
      const trimmed = text.trim();
      if (sid.length === 0 || trimmed.length === 0) return;
      try {
        // Same write path the Send button uses; the transcript is already dictionary-corrected by
        // the Gateway and is sent verbatim (transcript integrity, CodingStyle s16).
        await sendPrompt(sid, trimmed, true);
      } catch (err) {
        setError(err instanceof Error ? err.message : "Send failed");
      }
    },
    [sid],
  );

  // Issue #949: the fast fire-and-forget Send - the SAME path the Terminal/Chat Speak use. When Send is
  // pressed while still recording, the dialog hands up the captured audio and closes immediately; the
  // transcode/upload/transcribe/submit then runs in the background (the roster shows the session
  // orange), so dictating a reply in voice mode is as fast as dictating anywhere else instead of
  // blocking on the transcription. The PAUSED-stage Send still uses onRespondSend (text already in hand).
  const onRespondSendAudio = useCallback(
    (captured: CapturedUtterance) => {
      setResponding(false);
      if (sid.length === 0) return;
      void backgroundTranscribeAndSend(sid, captured, {
        onError: (message) => setError(message),
        // Record the session's terminal-byte position now, so a clip resumed later is not injected
        // into a session that has moved on (issue #1006 guard).
        baselineBufferBytes: Number(session?.totalBufferBytes ?? 0),
      });
    },
    [sid, session],
  );

  // Until the first poll resolves, trust the roster's seed so a voice session drops straight into the
  // ON state; once the poll has spoken, the real session flag governs (a stale seed cannot stick).
  const voiceOn = localEnabled || Boolean(session?.voiceMode) || (!pollDone && seededVoiceOn === true);
  // The speaking state (and its play-triangle) is suppressed while the agent is working again: the
  // finished-turn narration is stale, so the screen falls back to the working card instead of
  // offering a replay of it.
  const speaking = voiceOn && phoneReady && (voice?.spoken.length ?? 0) > 0 && !agentWorking;
  const gatewayPreparing = voiceOn && !speaking && !agentWorking && Boolean(session?.voiceGenerating);
  const phoneDownloadPending =
    voiceOn && !speaking && !agentWorking && Boolean(session?.voiceAudioReady) && clip.phase !== "error";
  // "Voice unavailable" is a POSITIVE finding - something looked and reported no narration - never the
  // gap left when no other state matched. The rule and the two windows it closes are documented in
  // voiceAvailability.ts; the short version is that this screen used to say "no narration is ready to
  // play" while it was still fetching the answer, and then play the narration. Notably clipDownloading
  // asks THIS PHONE what it holds, where phoneDownloadPending only relays what the GATEWAY holds.
  const audioUnavailable = isAudioUnavailable({
    voiceOn,
    sessionKnown: pollDone,
    voiceKnown,
    speaking,
    agentWorking,
    gatewayPreparing,
    phoneDownloadPending,
    clipDownloading: clip.phase === "downloading",
    hasEnableNote: enableNote.length > 0,
  });
  const working = voiceOn && !speaking && !audioUnavailable;
  const narrative = voice?.spoken ?? "";
  const title = session?.number ? `${session.number} ${name ?? "Session"}` : name ?? "Session";

  // "Switch to voice mode" from another screen's overflow menu navigated here asking for voice ON.
  // Run the screen's own onSwitchOn exactly once, and only once a poll has told us the session is not
  // already a voice session - so arriving at a session that is ALREADY in voice mode is a no-op rather
  // than a pointless re-narration of the turn you came to listen to.
  const autoSwitchedRef = useRef(false);
  useEffect(() => {
    if (autoSwitchOn !== true || autoSwitchedRef.current) return;
    if (!pollDone) return; // we do not know yet whether it is already on; the next tick decides
    autoSwitchedRef.current = true;
    if (voiceOn) return;
    void onSwitchOn();
  }, [autoSwitchOn, pollDone, voiceOn, onSwitchOn]);

  // WHY voice is unavailable, straight from the Gateway. This field has been arriving on every poll and
  // being thrown away: it had ZERO readers, while the screens hardcoded a guess ("the Gateway has not
  // made one, or this session's computer is offline") that was simply speculation - and during the
  // 2026-07-15 speech outage it was flatly false on both counts. Surface the real reason, and let the
  // views render it instead of inventing one. Null when the Gateway has nothing to say.
  const unavailableReason = session?.voiceUnavailable ?? null;
  // A service outage is not the user's fault and there is nothing for them to press: the Gateway is
  // already backing off and retrying. Offering a "Generate narration now" button here would be a lie.
  const unavailableIsServiceDown = unavailableReason?.state === "ServiceDown";

  return {
    voiceOn,
    speaking,
    working,
    audioUnavailable,
    unavailableReason,
    unavailableIsServiceDown,
    gatewayPreparing,
    phoneDownloadPending,
    agentWorking,
    pollDone,
    narrative,
    title,
    name,
    error,
    resumed,
    playing,
    pos,
    dur,
    enabling,
    regenerating,
    enableNote,
    responding,
    setResponding,
    setPlaying,
    clipUrl: clip.url,
    clipPhase: clip.phase,
    onSwitchOn,
    onSwitchOff,
    onGenerateNow,
    setAudioEl,
    onLoadedMeta,
    onTimeUpdate,
    onEndedAudio,
    onSeek,
    onRestart,
    onTogglePlay,
    onRespondSend,
    onRespondSendAudio,
  };
}
