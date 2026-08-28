import { useCallback, useEffect, useRef, useState } from "react";
import { clockFace } from "./timerParse";
import {
  matchTimersByName,
  remainingSeconds,
  sayAmbiguous,
  sayList,
  sayNotFound,
  sayStarted,
  sayStopped,
  sayStoppedAll,
  type StoredTimer,
} from "./timerLogic";

// Timers, kept by the page itself.
//
// No server, no notification, no background worker. The page holds them and counts them down, which
// means they only run while this app is on screen. That is a real limitation and it is stated plainly
// on screen rather than discovered when dinner burns: a browser cannot reliably wake itself up, and
// pretending otherwise would be the same sin as claiming to have set a timer that was never set.
//
// Every method returns the sentence to say. The sentence is built from what actually happened here,
// never predicted by the model that asked for the action.

export interface TimerView extends StoredTimer {
  readonly remaining: number;
  readonly face: string;
}

export interface Timers {
  readonly timers: TimerView[];
  readonly anyRinging: boolean;
  start(seconds: number, name: string | null): string;
  stopNamed(name: string | null): string;
  stopAll(): string;
  list(): string;
  /** Silence whatever is ringing without touching timers that are still counting down. */
  silence(): boolean;
  /** What the model needs to resolve "the pasta one". */
  snapshot(): Array<{ name: string | null; remainingSeconds: number; ringing: boolean }>;
}

/** A short, unmistakable double beep. Repeats until somebody deals with it. */
function beep(): void {
  try {
    const context = new AudioContext();
    const now = context.currentTime;
    [0, 0.22].forEach((offset) => {
      const oscillator = context.createOscillator();
      const gain = context.createGain();
      oscillator.type = "square";
      oscillator.frequency.value = 880;
      const at = now + offset;
      gain.gain.setValueAtTime(0, at);
      gain.gain.linearRampToValueAtTime(0.25, at + 0.01);
      gain.gain.setValueAtTime(0.25, at + 0.14);
      gain.gain.exponentialRampToValueAtTime(0.001, at + 0.19);
      oscillator.connect(gain).connect(context.destination);
      oscillator.start(at);
      oscillator.stop(at + 0.2);
    });
    window.setTimeout(() => void context.close(), 900);
  } catch {
    // A silent alarm is bad, but it is not a reason to stop the timer from finishing.
  }
}

export function useTimers(onFinished: (timer: StoredTimer) => void): Timers {
  const [timers, setTimers] = useState<StoredTimer[]>([]);
  const [, setTick] = useState(0);
  const nextId = useRef(1);
  const finishedRef = useRef(onFinished);
  const timersRef = useRef<StoredTimer[]>([]);
  finishedRef.current = onFinished;
  timersRef.current = timers;

  useEffect(() => {
    const interval = window.setInterval(() => {
      setTick((t) => t + 1);
      setTimers((previous) => {
        let changed = false;
        const next = previous.map((timer) => {
          if (!timer.ringing && Date.now() >= timer.endsAt) {
            changed = true;
            finishedRef.current(timer);
            return { ...timer, ringing: true };
          }
          return timer;
        });
        return changed ? next : previous;
      });
    }, 250);
    return () => window.clearInterval(interval);
  }, []);

  const ringing = timers.some((t) => t.ringing);
  useEffect(() => {
    if (!ringing) {
      return;
    }
    beep();
    const interval = window.setInterval(beep, 1400);
    return () => window.clearInterval(interval);
  }, [ringing]);

  const start = useCallback((seconds: number, name: string | null): string => {
    const made: StoredTimer = {
      id: nextId.current,
      name: name === null || name.trim().length === 0 ? null : name.trim().toLowerCase(),
      totalSeconds: seconds,
      endsAt: Date.now() + seconds * 1000,
      ringing: false,
    };
    nextId.current += 1;
    setTimers((previous) => [...previous, made]);
    return sayStarted(made);
  }, []);

  const stopNamed = useCallback((name: string | null): string => {
    const current = timersRef.current;
    const match = matchTimersByName(current, name);
    if (match.problem === "ambiguous") {
      return sayAmbiguous(current.filter((t) => !t.ringing));
    }
    if (match.matched.length === 0) {
      return sayNotFound(name);
    }
    const ids = new Set(match.matched.map((t) => t.id));
    setTimers((previous) => previous.filter((t) => !ids.has(t.id)));
    return sayStopped(match.matched);
  }, []);

  const stopAll = useCallback((): string => {
    const count = timersRef.current.length;
    setTimers([]);
    return sayStoppedAll(count);
  }, []);

  const list = useCallback((): string => sayList(timersRef.current, Date.now()), []);

  // Silencing removes what is ringing and leaves anything still counting down alone, which is what
  // "stop" means while an alarm is going: make this noise end, not cancel my other timer.
  const silence = useCallback((): boolean => {
    const had = timersRef.current.some((t) => t.ringing);
    if (had) {
      setTimers((previous) => previous.filter((t) => !t.ringing));
    }
    return had;
  }, []);

  const snapshot = useCallback(
    () =>
      timersRef.current.map((t) => ({
        name: t.name,
        remainingSeconds: remainingSeconds(t, Date.now()),
        ringing: t.ringing,
      })),
    [],
  );

  const view: TimerView[] = timers.map((timer) => {
    const remaining = remainingSeconds(timer, Date.now());
    return { ...timer, remaining, face: clockFace(remaining) };
  });

  return { timers: view, anyRinging: ringing, start, stopNamed, stopAll, list, silence, snapshot };
}
