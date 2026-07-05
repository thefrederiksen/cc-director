import { useCallback, useEffect, useRef, useState } from "react";
import {
  generateRecap,
  getRecap,
  getTurnSummaries,
  type RecapResponse,
  type TurnSummary,
} from "@devthrottle/client-core/awareness/awarenessClient";

// The awareness panes for the React desktop Cockpit (issue #974): the "what's happening" RECAP and the
// TURN RAIL - the arc of the session at a glance. A port of the Blazor Cockpit's "What's happening"
// awareness surface (Cockpit.razor) and its DirectorClient GetRecap / GenerateRecap / GetTurnSummaries
// reads, rebuilt as a session-main tab beside the live terminal.
//
// It is layered as a tab; the live terminal stays MOUNTED (hidden, not torn down) behind it. The recap
// read, the turn-summaries poll, and - critically - the SLOW recap generation all run over their own
// client-side fetches, entirely independent of the terminal's WebSocket, so nothing here ever blocks or
// freezes the live terminal (that is a separate stream). Mounting only while the tab is shown means the
// polls never run while hidden.
//
// Turn rail: one card per completed turn, ordered oldest -> newest with the NEWEST HIGHLIGHTED. It
// re-polls turn-summaries on an interval so new turns land in the rail as the session works.
//
// Recap: the cached recap reads instantly (GET). "Generate fresh" is a Director-side opus call (~90s):
// the button shows live progress (an elapsed timer) and is disabled while it runs; because the POST is
// a plain client fetch, the terminal keeps streaming the whole time.

const TURN_POLL_MS = 3000;

interface AwarenessPaneProps {
  sessionId: string | undefined;
}

// A short relative-clock label for a turn's generated time (HH:MM:SS local), mirroring the Blazor
// "@t.GeneratedAt.ToLocalTime().ToString("HH:mm:ss")".
function clockLabel(iso: string | null | undefined): string {
  if (!iso) return "";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  return d.toLocaleTimeString(undefined, { hour12: false });
}

export function AwarenessPane({ sessionId }: AwarenessPaneProps) {
  const [recap, setRecap] = useState<RecapResponse | null>(null);
  const [turns, setTurns] = useState<TurnSummary[]>([]);
  const [error, setError] = useState<string | null>(null);

  // Generation is slow (~90s). Track it separately from the read state so the read never waits on it,
  // and count elapsed seconds so the progress label is honest ("generating 12s / ~90s").
  const [generating, setGenerating] = useState(false);
  const [genElapsed, setGenElapsed] = useState(0);
  const genCtl = useRef<AbortController | null>(null);

  // The cached recap is read ONCE per session (mirroring the Blazor awareness surface, which loads it
  // when the panel opens), plus again whenever a generation completes. It is deliberately NOT on the
  // turn-summaries poll: a steady not_cached read must never clobber a just-generated recap or its
  // generation error. A session switch resets it.
  useEffect(() => {
    if (!sessionId) return;
    setRecap(null);
    setError(null);
    const controller = new AbortController();
    getRecap(sessionId, controller.signal)
      .then((r) => setRecap(r))
      .catch((err) => {
        if (!controller.signal.aborted) setError(err instanceof Error ? err.message : "Failed to read recap");
      });
    return () => controller.abort();
  }, [sessionId]);

  // Live poll of the turn summaries so the turn rail picks up new turns as the session works. Fast/free
  // reads, independent of the terminal stream and of the recap. A session switch resets the rail.
  useEffect(() => {
    if (!sessionId) return;
    setTurns([]);
    const controller = new AbortController();
    let cancelled = false;

    const refresh = async () => {
      try {
        const ts = await getTurnSummaries(sessionId, controller.signal);
        if (!cancelled) setTurns(ts.summaries);
      } catch (err) {
        if (cancelled || controller.signal.aborted) return;
        setError(err instanceof Error ? err.message : "Failed to load turn summaries");
      }
    };

    void refresh();
    const timer = window.setInterval(() => void refresh(), TURN_POLL_MS);
    return () => {
      cancelled = true;
      controller.abort();
      window.clearInterval(timer);
    };
  }, [sessionId]);

  // Elapsed-seconds ticker while a generation is in flight (drives the progress label).
  useEffect(() => {
    if (!generating) return;
    setGenElapsed(0);
    const started = Date.now();
    const timer = window.setInterval(() => setGenElapsed(Math.floor((Date.now() - started) / 1000)), 1000);
    return () => window.clearInterval(timer);
  }, [generating]);

  // Cancel any in-flight generation if the pane unmounts or the session changes.
  useEffect(() => {
    return () => genCtl.current?.abort();
  }, [sessionId]);

  const onGenerate = useCallback(async () => {
    if (!sessionId || generating) return;
    genCtl.current?.abort();
    const ctl = new AbortController();
    genCtl.current = ctl;
    setGenerating(true);
    setError(null);
    try {
      const fresh = await generateRecap(sessionId, ctl.signal);
      if (!ctl.signal.aborted) setRecap(fresh);
    } catch (err) {
      if (!ctl.signal.aborted) setError(`recap failed: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      if (!ctl.signal.aborted) setGenerating(false);
    }
  }, [sessionId, generating]);

  // The newest turn is the last one (the endpoint returns oldest -> newest); highlight it.
  const newestIndex = turns.length - 1;

  return (
    <div className="aware">
      <div className="aware-section">
        <div className="aware-h">
          <span>RECAP</span>
          <button type="button" className="aware-gen" onClick={() => void onGenerate()} disabled={generating}>
            {generating ? `generating ${genElapsed}s / ~90s...` : "Generate fresh"}
          </button>
        </div>

        {generating && (
          <div className="aware-genbar" role="status" aria-live="polite">
            <span className="aware-genspin" aria-hidden="true" />
            Generating a fresh recap (a Director-side opus call, about 90 seconds). The live terminal
            keeps running - this never blocks it.
          </div>
        )}

        {recap === null ? (
          <div className="aware-muted">Loading...</div>
        ) : recap.status === "ok" && recap.recap.trim().length > 0 ? (
          <>
            <div className="aware-recap">{recap.recap}</div>
            <div className="aware-meta">
              {recap.model} &middot; {recap.isStale ? "stale (new turns since)" : "current"} &middot; generated{" "}
              {clockLabel(recap.generatedAt)}
            </div>
          </>
        ) : (
          <div className="aware-muted">{recap.error ?? "No recap yet. Click Generate fresh."}</div>
        )}
      </div>

      <div className="aware-section">
        <div className="aware-h">
          <span>TURN SUMMARIES ({turns.length})</span>
        </div>
        {turns.length === 0 ? (
          <div className="aware-muted">No turn summaries yet for this session.</div>
        ) : (
          <div className="turn-rail">
            {turns.map((t, i) => (
              <div className={`turn ${i === newestIndex ? "turn-newest" : ""}`} key={i}>
                <div className="turn-headline">
                  {t.needsUser !== "no" && <span className="turn-needs">needs you</span>}
                  {t.headline}
                </div>
                {t.filesTouched.length > 0 && (
                  <div className="turn-meta">files: {t.filesTouched.slice(0, 6).join(", ")}</div>
                )}
                <div className="turn-time">{clockLabel(t.generatedAt)}</div>
              </div>
            ))}
          </div>
        )}
      </div>

      {error !== null && <div className="aware-error">{error}</div>}
    </div>
  );
}
