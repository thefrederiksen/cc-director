// The Assistant turn machine (fleet assistant build), shared by any shell that mounts the screen
// (the cockpit today, the phone later - issue #1213 pattern: logic in client-core, thin JSX views).
//
// Turn-taking is A BUTTON, deliberately: tap to talk, tap again to send. No silence detection, no
// end-phrase keyword - both were tried in Car Mode and the owner rejected them for the desk. The
// phases are linear and visible: listening -> transcribing -> thinking -> (voice mode) speaking.
//
// Reuse, do not rebuild: mic capture is the shared MicRecorder, transcode is the shared
// blobToWav16kMono, speech-to-text is POST /wingman/transcribe, the brain is POST /assistant/turn,
// and read-aloud is POST /wingman/tts played through the shared playClip discipline (one src
// assignment per element, never a clobber).

import { useCallback, useEffect, useRef, useState, type RefObject } from "react";
import { MicRecorder } from "../dictation/recorder";
import { blobToWav16kMono } from "../dictation/wav";
import { playClip } from "../carmode/audioPlayback";
import { postCarModeWarmup, speakCarModeText, transcribeCarModeAudio } from "../carmode/carModeApi";
import { gatewayErrorMessage } from "../api/client";
import { reportClientError } from "../errors/reportClientError";
import { assistantTurn } from "./assistantApi";
import { appendEntry, awaitingConfirmation, type AssistantEntry } from "./transcript";

/** Where the machine is in one turn. Idle between turns; the rest are the visible stages. */
export type AssistantPhase = "idle" | "listening" | "transcribing" | "thinking" | "speaking";

/** Chat mode types (replies stay silent); voice mode talks and reads every reply aloud. */
export type AssistantMode = "chat" | "voice";

/** The one in-flight microphone capture: its recorder, its generation (stale-settle detection),
 *  and the start() promise every teardown path must let settle before disposing. */
interface TalkCapture {
  recorder: MicRecorder;
  generation: number;
  started: Promise<void>;
}

export interface UseAssistant {
  entries: AssistantEntry[];
  phase: AssistantPhase;
  mode: AssistantMode;
  setMode: (mode: AssistantMode) => void;
  /** True while a turn is in flight (any phase but idle) - the composer disables itself on it. */
  busy: boolean;
  /** True when the brain is holding a destructive action; the screen offers Yes / Cancel. */
  confirmOffered: boolean;
  /** Send a typed question (chat mode's Send button / Enter). No-op on blank or while busy. */
  sendText: (text: string) => void;
  /** Start capturing a spoken question (the talk button). Throws never - a mic failure lands in
   *  the transcript as an error entry. */
  startTalk: () => void;
  /** Stop capturing and run the turn (the talk button again). */
  endTalk: () => void;
  /** Abandon the capture without sending (Escape / leaving the screen). */
  cancelTalk: () => void;
  /** Stop the read-aloud playback immediately ("Stop reading"). */
  stopSpeaking: () => void;
}

/**
 * Drive the Assistant conversation against the Gateway. The caller owns a permanently-mounted,
 * hidden `<audio>` element (the mobile Voice screen pattern) and hands it in by ref; read-aloud
 * plays on that element so mode switches and re-renders never orphan live audio.
 */
export function useAssistant(audioRef: RefObject<HTMLAudioElement | null>): UseAssistant {
  const [entries, setEntries] = useState<AssistantEntry[]>([]);
  const [phase, setPhase] = useState<AssistantPhase>("idle");
  const [mode, setMode] = useState<AssistantMode>("chat");

  // One capture in flight, identified by its generation number (Codex review finding 3). The
  // microphone permission prompt makes MicRecorder.start() genuinely asynchronous: a cancel, a
  // quick second tap, or an unmount can all arrive while getUserMedia is still pending, and a
  // recorder disposed at that moment would come alive afterwards as an invisible open microphone.
  // Every settle path therefore checks its generation against the current one and disposes the
  // recorder AFTER its start() promise settles - never before.
  const talkRef = useRef<TalkCapture | null>(null);
  const generationRef = useRef(0);

  const stopPlaybackRef = useRef<() => void>(() => {});
  // The mode at the moment a turn RUNS decides whether its reply is read aloud; a ref avoids the
  // stale-closure trap when the owner flips the toggle mid-turn.
  const modeRef = useRef<AssistantMode>("chat");
  modeRef.current = mode;

  /** Abandon the in-flight capture, disposing the recorder only after its start() settles. */
  const abandonCapture = useCallback(() => {
    const talk = talkRef.current;
    if (talk === null) return;
    talkRef.current = null;
    void talk.started.catch(() => {}).then(() => talk.recorder.dispose());
  }, []);

  // Warm the hosted model + text-to-speech the moment the screen opens, so the first question does
  // not pay the cold start (the same keep-warm door Car Mode uses; best-effort, never throws).
  useEffect(() => {
    void postCarModeWarmup();
    return () => {
      abandonCapture();
      stopPlaybackRef.current();
    };
  }, [abandonCapture]);

  const push = useCallback((entry: AssistantEntry) => {
    // Enterprise logging rule: every error this screen SHOWS is also REPORTED - the on-screen text and
    // the Gateway log can never disagree about what the user saw, and no one has to read a screen back.
    if (entry.role === "error") {
      reportClientError("assistant", typeof window !== "undefined" ? window.location.pathname : "", entry.text);
    }
    setEntries((cur) => appendEntry(cur, entry));
  }, []);

  const speak = useCallback(async (text: string) => {
    const audio = audioRef.current;
    if (audio === null) return;
    setPhase("speaking");
    const clip = await speakCarModeText(text);
    const url = URL.createObjectURL(clip);
    try {
      await playClip(audio, url, (stop) => {
        // Codex review finding 4: playClip's stop resolves the play promise but does NOT silence the
        // element - pausing here is what actually stops the sound. Both halves belong to the one
        // registered stop so every caller (the Stop reading button, a mode switch, unmount) gets both.
        stopPlaybackRef.current = () => {
          audio.pause();
          stop();
        };
      });
    } finally {
      URL.revokeObjectURL(url);
      stopPlaybackRef.current = () => {};
    }
  }, [audioRef]);

  const runTurn = useCallback(async (text: string) => {
    push({ role: "user", text });
    setPhase("thinking");
    try {
      const result = await assistantTurn(text, crypto.randomUUID());
      push({
        role: "assistant",
        text: result.spoken,
        actions: result.actions,
        pendingConfirmation: result.pendingConfirmation,
      });
      if (modeRef.current === "voice" && result.spoken.length > 0) {
        // A read-aloud failure must not eat the answer that is already on screen: report it as its
        // own error entry instead of failing the turn.
        try {
          await speak(result.spoken);
        } catch (err) {
          push({ role: "error", text: `The reply could not be read aloud: ${gatewayErrorMessage(err)}` });
        }
      }
    } catch (err) {
      push({ role: "error", text: gatewayErrorMessage(err) });
    } finally {
      setPhase("idle");
    }
  }, [push, speak]);

  const sendText = useCallback((text: string) => {
    const trimmed = text.trim();
    if (trimmed.length === 0 || phase !== "idle") return;
    void runTurn(trimmed);
  }, [phase, runTurn]);

  const startTalk = useCallback(() => {
    if (phase !== "idle" || talkRef.current !== null) return;
    const generation = ++generationRef.current;
    const recorder = new MicRecorder();
    const started = recorder.start();
    talkRef.current = { recorder, generation, started };
    setPhase("listening");
    started.catch((err: unknown) => {
      // Settle path for a start that failed (permission denied, no device). Act only if THIS capture
      // is still the current one - endTalk/cancelTalk may have claimed it already, and their own
      // settle handling owns the outcome then. A stale failure only disposes its own recorder.
      if (talkRef.current?.generation !== generation) {
        recorder.dispose();
        return;
      }
      talkRef.current = null;
      setPhase("idle");
      push({ role: "error", text: err instanceof Error ? err.message : "The microphone could not be opened." });
    });
  }, [phase, push]);

  const endTalk = useCallback(() => {
    const talk = talkRef.current;
    if (talk === null || phase !== "listening") return;
    // Claim the capture: from here the start-failure handler above sees a stale generation and
    // stands down, so this path owns every outcome exactly once.
    talkRef.current = null;
    setPhase("transcribing");
    void (async () => {
      try {
        // Let start() finish before stopping - stop() on a recorder whose getUserMedia is still
        // pending would throw AND leave the microphone to open later, unowned (finding 3).
        await talk.started;
        const captured = await talk.recorder.stop();
        const { wav } = await blobToWav16kMono(captured);
        const transcript = await transcribeCarModeAudio(wav);
        if (transcript.length === 0) {
          setPhase("idle");
          push({ role: "error", text: "Nothing was heard. Tap to talk and try again." });
          return;
        }
        await runTurn(transcript);
      } catch (err) {
        talk.recorder.dispose();
        setPhase("idle");
        push({ role: "error", text: gatewayErrorMessage(err) });
      }
    })();
  }, [phase, push, runTurn]);

  const cancelTalk = useCallback(() => {
    if (phase !== "listening") return;
    abandonCapture();
    setPhase("idle");
  }, [abandonCapture, phase]);

  const stopSpeaking = useCallback(() => {
    stopPlaybackRef.current();
  }, []);

  // Leaving voice mode mid-readback removes the Stop reading button, so the mode switch itself
  // must stop the audio (finding 4) - otherwise the reply keeps sounding with no way to silence it.
  const setModeStopping = useCallback((next: AssistantMode) => {
    if (next === "chat") stopPlaybackRef.current();
    setMode(next);
  }, []);

  return {
    entries,
    phase,
    mode,
    setMode: setModeStopping,
    busy: phase !== "idle",
    confirmOffered: awaitingConfirmation(entries) && phase === "idle",
    sendText,
    startTalk,
    endTalk,
    cancelTalk,
    stopSpeaking,
  };
}
