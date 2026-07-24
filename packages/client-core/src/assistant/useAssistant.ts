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
import { assistantTurn } from "./assistantApi";
import { appendEntry, awaitingConfirmation, type AssistantEntry } from "./transcript";

/** Where the machine is in one turn. Idle between turns; the rest are the visible stages. */
export type AssistantPhase = "idle" | "listening" | "transcribing" | "thinking" | "speaking";

/** Chat mode types (replies stay silent); voice mode talks and reads every reply aloud. */
export type AssistantMode = "chat" | "voice";

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

  const recorderRef = useRef<MicRecorder | null>(null);
  const stopPlaybackRef = useRef<() => void>(() => {});
  // The mode at the moment a turn RUNS decides whether its reply is read aloud; a ref avoids the
  // stale-closure trap when the owner flips the toggle mid-turn.
  const modeRef = useRef<AssistantMode>("chat");
  modeRef.current = mode;

  // Warm the hosted model + text-to-speech the moment the screen opens, so the first question does
  // not pay the cold start (the same keep-warm door Car Mode uses; best-effort, never throws).
  useEffect(() => {
    void postCarModeWarmup();
    return () => {
      recorderRef.current?.dispose();
      recorderRef.current = null;
      stopPlaybackRef.current();
    };
  }, []);

  const push = useCallback((entry: AssistantEntry) => {
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
        stopPlaybackRef.current = stop;
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
    if (phase !== "idle") return;
    const recorder = new MicRecorder();
    recorderRef.current = recorder;
    setPhase("listening");
    void recorder.start().catch((err: unknown) => {
      recorderRef.current = null;
      setPhase("idle");
      push({ role: "error", text: err instanceof Error ? err.message : "The microphone could not be opened." });
    });
  }, [phase, push]);

  const endTalk = useCallback(() => {
    const recorder = recorderRef.current;
    if (recorder === null || phase !== "listening") return;
    recorderRef.current = null;
    setPhase("transcribing");
    void (async () => {
      try {
        const captured = await recorder.stop();
        const { wav } = await blobToWav16kMono(captured);
        const transcript = await transcribeCarModeAudio(wav);
        if (transcript.length === 0) {
          setPhase("idle");
          push({ role: "error", text: "Nothing was heard. Tap to talk and try again." });
          return;
        }
        await runTurn(transcript);
      } catch (err) {
        setPhase("idle");
        push({ role: "error", text: gatewayErrorMessage(err) });
      }
    })();
  }, [phase, push, runTurn]);

  const cancelTalk = useCallback(() => {
    if (phase !== "listening") return;
    recorderRef.current?.dispose();
    recorderRef.current = null;
    setPhase("idle");
  }, [phase]);

  const stopSpeaking = useCallback(() => {
    stopPlaybackRef.current();
  }, []);

  return {
    entries,
    phase,
    mode,
    setMode,
    busy: phase !== "idle",
    confirmOffered: awaitingConfirmation(entries) && phase === "idle",
    sendText,
    startTalk,
    endTalk,
    cancelTalk,
    stopSpeaking,
  };
}
