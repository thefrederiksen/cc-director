// The Assistant turn machine (fleet assistant build), shared by any shell that mounts the screen
// (the cockpit and the phone - issue #1213 pattern: logic in client-core, thin JSX views).
//
// Turn-taking is A BUTTON, deliberately: you press to speak and press Send when you are done. No
// silence detection, no end-phrase keyword - both were tried in Car Mode and the owner rejected them
// for the desk. The phases this machine owns are linear and visible: thinking -> (voice) speaking.
//
// SPEAKING A QUESTION IS THE SHARED DICTATION DIALOG, NOT A PATH IN HERE. This machine used to open
// its own microphone (startTalk / endTalk) behind a plain round button: no level meter, no timer, no
// pause, and no way out once it was listening except sending. That is a second, poorer microphone
// owner sitting beside the one the rest of the product uses. The screens now mount the shared
// DictationDialog (packages/client-core/dictation/DictationDialog) - equalizer bars, elapsed timer,
// Pause checkpoint, editable transcript, Cancel and Send - and hand its finished text to sendText().
// One microphone owner, one dictation look, everywhere.
//
// Reuse, do not rebuild: the brain is POST /assistant/turn, and read-aloud is POST /wingman/tts
// played through the shared playClip discipline (one src assignment per element, never a clobber).

import { useCallback, useEffect, useRef, useState, type RefObject } from "react";
import { playClip } from "../fleetbrain/audioPlayback";
import { postBrainWarmup, speakText } from "../fleetbrain/brainApi";
import { gatewayErrorMessage } from "../api/client";
import { reportClientError } from "../errors/reportClientError";
import { assistantTurn } from "./assistantApi";
import { appendEntry, awaitingConfirmation, type AssistantEntry } from "./transcript";

/** Where the machine is in one turn. Idle between turns; the rest are the visible stages. The mic
 *  stages (listening, transcribing) are NOT here - the dictation dialog owns capture and shows its
 *  own status, and it only reaches this machine once it has text. */
export type AssistantPhase = "idle" | "thinking" | "speaking";

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
  /** Run a turn on this text - chat mode's Send button / Enter, and the dictation dialog's Send.
   *  No-op on blank or while busy. */
  sendText: (text: string) => void;
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

  const stopPlaybackRef = useRef<() => void>(() => {});
  // The mode at the moment a turn RUNS decides whether its reply is read aloud; a ref avoids the
  // stale-closure trap when the owner flips the toggle mid-turn.
  const modeRef = useRef<AssistantMode>("chat");
  modeRef.current = mode;

  // Warm the hosted model + text-to-speech the moment the screen opens, so the first question does
  // not pay the cold start (the same keep-warm door Car Mode uses; best-effort, never throws).
  // Leaving the screen silences any read-aloud in flight; the microphone belongs to the dictation
  // dialog, which disposes its own recorder on unmount.
  useEffect(() => {
    void postBrainWarmup();
    return () => {
      stopPlaybackRef.current();
    };
  }, []);

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
    const clip = await speakText(text);
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
    stopSpeaking,
  };
}
