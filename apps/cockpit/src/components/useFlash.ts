import { useCallback, useEffect, useRef, useState } from "react";

// A small transient-status helper for action results (issue #1244). Several pages hand-rolled the same
// "show a short result message, then clear it after a few seconds" logic with their own timers (the
// action bar's flash, the Voice Recorder's clearLater). This hook is that one helper: it holds a single
// transient message and clears it automatically, cancelling any pending clear on unmount so a timer
// never fires into an unmounted component.

/** How long a flashed message stays up before it clears itself, unless the caller overrides it. */
const DEFAULT_FLASH_MS = 5000;

export interface Flash {
  /** The kind of result, so StatusMessage can colour it; defaults to "info". */
  kind: "info" | "success" | "error";
  /** The message text. */
  text: string;
}

export interface FlashController {
  /** The current flash, or null when nothing is showing. */
  flash: Flash | null;
  /** Show a message; it clears itself after `ms` (default five seconds). */
  show: (text: string, kind?: Flash["kind"], ms?: number) => void;
  /** Clear the current message immediately. */
  clear: () => void;
}

export function useFlash(): FlashController {
  const [flash, setFlash] = useState<Flash | null>(null);
  const timerRef = useRef<number | null>(null);

  const clearTimer = () => {
    if (timerRef.current !== null) {
      window.clearTimeout(timerRef.current);
      timerRef.current = null;
    }
  };

  const clear = useCallback(() => {
    clearTimer();
    setFlash(null);
  }, []);

  const show = useCallback((text: string, kind: Flash["kind"] = "info", ms: number = DEFAULT_FLASH_MS) => {
    clearTimer();
    setFlash({ kind, text });
    timerRef.current = window.setTimeout(() => {
      timerRef.current = null;
      setFlash(null);
    }, ms);
  }, []);

  // Cancel any pending clear when the owning component unmounts.
  useEffect(() => clearTimer, []);

  return { flash, show, clear };
}
