import { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { holdSession, killSession, listSessions } from "@devthrottle/client-core/api/client";
import { classify, isDeferredHold, isWorking, snoozeCountdown } from "@devthrottle/client-core/sessions/ordering";
import {
  isSnoozing,
  optimisticHoldToggle,
  reconcileHoldToggle,
  type HoldToggleOutcome,
  type HoldUiState,
} from "@devthrottle/client-core/sessions/snoozeAction";

// The session management verbs (Snooze/Unsnooze + Remove) for ONE session, hoisted out of the old
// SessionManageBar so two places can drive them from one copy of the state: the app bar's overflow
// menu (Remove, and Snooze on the screens with no room for it) and the Voice mode bottom bar (Snooze
// next to Respond). Owning this in a hook is what stops the two surfaces disagreeing about the live
// held state.
//
// The held state is polled from the SAME roster the Home page reads, so the session screen and the
// roster always agree, and a hold toggled elsewhere (the desktop) shows up here.
//
// SNOOZE MUST FEEL INSTANT (owner's #1 annoyance). Two things make it so, in every case:
//   - a hold has a TRI-STATE (held / deferred / none), not a boolean. Snoozing a WORKING session DEFERS
//     (its clock starts when the work ends - owner ruling), which the Gateway returns as
//     { onHold: false, pending: true }. The old client read only onHold, so a deferred snooze showed NO
//     change and read as "the button does nothing". `deferred` carries that state to the surfaces.
//   - the tap updates the UI OPTIMISTICALLY (before the server answers) and then triggers an IMMEDIATE
//     roster re-sync, so neither the button nor the pill wait up to a poll interval to reflect the snooze.

const POLL_INTERVAL_MS = 4000;

export interface SessionManage {
  onHold: boolean | null;
  held: boolean;
  // A DEFERRED snooze: asked for while the agent was working, so it arms when the work ends. Distinct
  // from `held` (armed now) - a caller that read only `held` would show nothing for a deferred snooze.
  deferred: boolean;
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
  const [deferred, setDeferred] = useState(false);
  const [working, setWorking] = useState(false);
  const [snoozed, setSnoozed] = useState(false);
  const [holdCountdown, setHoldCountdown] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // While a toggle is in flight the optimistic state must not be clobbered by a slower poll.
  const pendingRef = useRef(false);

  // Hoisted out of the effect so toggleHold can call it for an IMMEDIATE re-sync after a snooze, instead
  // of leaving the button/pill stale until the next interval. Reads the SAME roster the Home page reads.
  const refresh = useCallback(async (signal?: AbortSignal) => {
    if (!sessionId) return;
    try {
      const all = await listSessions(signal);
      if (pendingRef.current) return;
      const match = all.find((s) => s.sessionId === sessionId);
      if (match) {
        // The toggle needs the raw hold (what it will flip); the DISPLAY reads the fold (working wins).
        setOnHold(Boolean(match.onHold));
        // The Gateway-owned tri-state: DeferredHold is a real snooze that has not armed yet.
        setDeferred(isDeferredHold(match));
        // Whether snoozing NOW would defer (working) or arm (settled) - drives the optimistic affordance.
        setWorking(isWorking(match));
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
  }, [sessionId]);

  useEffect(() => {
    if (!sessionId) return;
    const controller = new AbortController();
    void refresh(controller.signal);
    const timer = window.setInterval(() => void refresh(controller.signal), POLL_INTERVAL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [sessionId, refresh]);

  const toggleHold = useCallback(async () => {
    if (!sessionId || busy) return;
    // The pre-tap state: both what the toggle flips FROM and what a FAILED /hold must roll back to.
    const preTap: HoldUiState = { held: onHold === true, deferred };
    const desired = !isSnoozing(preTap);
    // Optimistic: flip the UI the instant the tap lands, so a snooze is NEVER silent. A working session
    // shows the deferred affordance immediately; a settled one shows held. Settled below from the TRUE
    // outcome - the server's authoritative answer on success, or a rollback to pre-tap on failure.
    const optimistic = optimisticHoldToggle(preTap, working);
    setBusy(true);
    pendingRef.current = true;
    setError(null);
    setOnHold(optimistic.held);
    setDeferred(optimistic.deferred);
    let outcome: HoldToggleOutcome;
    try {
      outcome = { ok: true, response: await holdSession(sessionId, desired) };
    } catch (err) {
      // /hold FAILED: the snooze did NOT happen. Surface the error; the rollback below returns the UI to
      // its pre-tap state so the button never falsely reads "Snoozed"/"Snoozing when it finishes".
      outcome = { ok: false };
      setError(err instanceof Error ? err.message : "Hold failed");
    } finally {
      pendingRef.current = false;
      setBusy(false);
    }
    // Settle on the TRUE server outcome: the authoritative tri-state on success, or the pre-tap state on
    // failure (never a false success). Only on success re-sync from the roster fold NOW, killing the
    // up-to-a-poll-interval lag so the countdown, the snoozed pill and the armed/deferred split land
    // immediately. On FAILURE we must NOT refresh: a roster blip could re-assert the optimistic snooze,
    // or a failed refresh would keep the last-known (optimistic) state - either way a false "Snoozed".
    const settled = reconcileHoldToggle(preTap, outcome);
    setOnHold(settled.held);
    setDeferred(settled.deferred);
    if (outcome.ok) void refresh();
  }, [sessionId, busy, onHold, deferred, working, refresh]);

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
    deferred,
    snoozed,
    holdCountdown,
    busy,
    error,
    setError,
    toggleHold,
    removeSession,
  };
}
