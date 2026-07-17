import { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { holdSession, killSession, listSessions } from "@devthrottle/client-core/api/client";
import { classify, snoozeCountdown } from "@devthrottle/client-core/sessions/ordering";

// The session management verbs (Snooze/Unsnooze + Remove) for ONE session, hoisted out of the old
// SessionManageBar so two places can drive them from one copy of the state: the app bar's overflow
// menu (Remove, and Snooze on the screens with no room for it) and the Voice mode bottom bar (Snooze
// next to Respond). Owning this in a hook is what stops the two surfaces disagreeing about the live
// held state.
//
// The held state is polled from the SAME roster the Home page reads, so the session screen and the
// roster always agree, and a hold toggled elsewhere (the desktop) shows up here.

const POLL_INTERVAL_MS = 4000;

export interface SessionManage {
  onHold: boolean | null;
  held: boolean;
  // The FOLD's verdict for DISPLAY (classify === "onHold"), distinct from the raw `held` the toggle uses.
  // A Held session that has started working is blue "Working" and must NOT read "snoozed" - working wins in
  // the fold, so a display pill reads this, never the raw onHold flag.
  snoozed: boolean;
  // "wakes in 3h 48m" from the Gateway-owned snooze clock, or null when there is no running clock.
  holdCountdown: string | null;
  busy: boolean;
  error: string | null;
  setError: (message: string | null) => void;
  toggleHold: () => Promise<void>;
  removeSession: () => Promise<void>;
}

export function useSessionManage(sessionId: string | undefined): SessionManage {
  const navigate = useNavigate();
  const [onHold, setOnHold] = useState<boolean | null>(null);
  const [snoozed, setSnoozed] = useState(false);
  const [holdCountdown, setHoldCountdown] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // While a toggle is in flight the optimistic state must not be clobbered by a slower poll.
  const pendingRef = useRef(false);

  useEffect(() => {
    if (!sessionId) return;
    const controller = new AbortController();
    let cancelled = false;
    const refresh = async () => {
      try {
        const all = await listSessions(controller.signal);
        if (cancelled || pendingRef.current) return;
        const match = all.find((s) => s.sessionId === sessionId);
        if (match) {
          // The toggle needs the raw hold (what it will flip); the DISPLAY reads the fold (working wins).
          setOnHold(Boolean(match.onHold));
          setHoldCountdown(snoozeCountdown(match));
          // classify fails loud against a Gateway that did not stamp triageBucket; in this polling loop a
          // mixed-version blip must not throw the refresh, so keep the last verdict on that rare miss.
          try {
            setSnoozed(classify(match) === "onHold");
          } catch {
            /* keep the last-known snoozed verdict */
          }
        }
      } catch {
        /* keep the last-known held state; the actions surface their own errors */
      }
    };
    void refresh();
    const timer = window.setInterval(() => void refresh(), POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      controller.abort();
      window.clearInterval(timer);
    };
  }, [sessionId]);

  const toggleHold = useCallback(async () => {
    if (!sessionId || busy) return;
    const desired = !(onHold ?? false);
    setBusy(true);
    pendingRef.current = true;
    setError(null);
    try {
      const applied = await holdSession(sessionId, desired);
      setOnHold(applied);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Hold failed");
    } finally {
      pendingRef.current = false;
      setBusy(false);
    }
  }, [sessionId, busy, onHold]);

  const removeSession = useCallback(async () => {
    if (!sessionId || busy) return;
    setBusy(true);
    setError(null);
    try {
      await killSession(sessionId);
      // Return to the Home roster, where the session is now gone (the #545 pattern).
      navigate("/");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Remove failed");
      setBusy(false);
      throw err;
    }
  }, [sessionId, busy, navigate]);

  return {
    onHold,
    held: onHold === true,
    snoozed,
    holdCountdown,
    busy,
    error,
    setError,
    toggleHold,
    removeSession,
  };
}
