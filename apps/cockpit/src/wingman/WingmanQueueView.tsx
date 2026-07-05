import { useCallback, useEffect, useState } from "react";
import {
  getWingmanQueue,
  type WingmanQueueSnapshot,
} from "@devthrottle/client-core/wingman/queueClient";
import { clockLabel } from "../fleet/format";

// The fleet-level Wingman Pipeline page (issue #976, epic #967) - the React port of the Blazor
// Cockpit WingmanQueue.razor (issue #239). A READ-ONLY window onto the one-brain stamping machine:
// the in-flight session, the ordered queue behind it, recent briefs, and brain health. It renders
// the GET /wingman/queue snapshot (same-origin through the Gateway front door via client-core) and
// refreshes on a 3s cadence. No control here mutates queue state - read-only by design. This is a
// DISTINCT surface from the per-session composer "Queue" tab.
//
// Since issue #549 retired the always-on stamping machine, current Gateways answer an honest idle
// snapshot with brain.status "Disabled"; the page renders that faithfully (idle, empty queue).
const POLL_MS = 3000;

export function WingmanQueueView() {
  const [snapshot, setSnapshot] = useState<WingmanQueueSnapshot | null>(null);
  const [lastRefresh, setLastRefresh] = useState<Date | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async (signal?: AbortSignal) => {
    try {
      const fresh = await getWingmanQueue(signal);
      setSnapshot(fresh);
      setError(null);
      setLastRefresh(new Date());
    } catch (err) {
      if (signal?.aborted === true) return;
      setError(err instanceof Error ? err.message : "Failed to fetch the wingman pipeline");
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void refresh(controller.signal);
    const timer = window.setInterval(() => void refresh(controller.signal), POLL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [refresh]);

  const degradedCount = snapshot?.recent.filter((r) => r.degraded).length ?? 0;

  return (
    <div className="wq">
      <header className="wq-head">
        <h1 className="wq-title">Wingman Pipeline</h1>
        <span className="wq-sub">the one warm brain, read-only</span>
        <span className="wq-refreshed">
          {lastRefresh === null ? "connecting..." : `updated ${clockLabel(lastRefresh)}`}
        </span>
      </header>

      {error !== null ? (
        <div className="wq-banner-error">Gateway error: {error}</div>
      ) : snapshot === null ? (
        <div className="wq-loading">Loading pipeline...</div>
      ) : (
        <>
          <div className="wq-grid">
            {/* ---- in flight ---- */}
            <section className="wq-card">
              <div className="wq-card-h">In flight</div>
              {snapshot.inFlight ? (
                <div className="wq-inflight">
                  <span className={`wq-kind ${kindClass(snapshot.inFlight.kind)}`}>
                    {kindLabel(snapshot.inFlight.kind)}
                  </span>
                  <span className="wq-sid mono">{shortId(snapshot.inFlight.sessionId)}</span>
                  <span className="wq-elapsed">reading {elapsedText(snapshot.inFlight.elapsedSeconds)}</span>
                </div>
              ) : (
                <div className="wq-idle">Idle - the brain is not reading any session right now.</div>
              )}
            </section>

            {/* ---- queue ---- */}
            <section className="wq-card">
              <div className="wq-card-h">
                Queue <span className="wq-count">{snapshot.queue.length}</span>
              </div>
              {snapshot.queue.length === 0 ? (
                <div className="wq-idle">Nothing waiting.</div>
              ) : (
                <ol className="wq-queue">
                  {snapshot.queue.map((q, i) => (
                    <li key={`${q.kind}/${q.sessionId}/${i}`}>
                      <span className="wq-pos">{i + 1}</span>
                      <span className={`wq-kind ${kindClass(q.kind)}`}>{kindLabel(q.kind)}</span>
                      <span className="wq-sid mono">{shortId(q.sessionId)}</span>
                    </li>
                  ))}
                </ol>
              )}
            </section>

            {/* ---- brain health ---- */}
            <section className="wq-card">
              <div className="wq-card-h">Brain health</div>
              <div className="wq-brain">
                <div className="wq-brow">
                  <span className="wq-blbl">Status</span>
                  <span className="wq-bval">
                    <span className={`wq-dot ${snapshot.brain.alive ? "ok" : "off"}`} />
                    {snapshot.brain.alive ? "alive" : "not running"} ({snapshot.brain.status})
                  </span>
                </div>
                <div className="wq-brow">
                  <span className="wq-blbl">Model</span>
                  <span className="wq-bval mono">
                    {snapshot.brain.model.trim().length === 0 ? "(unset)" : snapshot.brain.model}
                  </span>
                </div>
                <div className="wq-brow">
                  <span className="wq-blbl">PID</span>
                  <span className="wq-bval mono">{snapshot.brain.pid > 0 ? snapshot.brain.pid : "-"}</span>
                </div>
                <div className="wq-brow">
                  <span className="wq-blbl">Consecutive rejections</span>
                  <span className={`wq-bval ${rejectionClass(snapshot)}`}>
                    {snapshot.brain.consecutiveRejections} / {snapshot.brain.rejectionThreshold}
                    {snapshot.brain.consecutiveRejections >= snapshot.brain.rejectionThreshold && (
                      <span className="wq-warn">poisoned - restarting</span>
                    )}
                  </span>
                </div>
                <div className="wq-brow">
                  <span className="wq-blbl">Recovery</span>
                  <span className="wq-bval">
                    {snapshot.brain.recoveryInFlight ? "restart in flight" : "idle"}
                  </span>
                </div>
                <div className="wq-brow">
                  <span className="wq-blbl">Degraded (recent)</span>
                  <span className={`wq-bval ${degradedCount > 0 ? "amber" : ""}`}>
                    {degradedCount} of {snapshot.recent.length}
                  </span>
                </div>
              </div>
            </section>

            {/* ---- recent briefs ---- */}
            <section className="wq-card wq-card-wide">
              <div className="wq-card-h">
                Recent briefs <span className="wq-count">{snapshot.recent.length}</span>
              </div>
              {snapshot.recent.length === 0 ? (
                <div className="wq-idle">No briefs generated yet.</div>
              ) : (
                <table className="wq-recent">
                  <thead>
                    <tr>
                      <th>Session</th>
                      <th>Turn</th>
                      <th>Generated</th>
                      <th>Model</th>
                      <th>Quality</th>
                    </tr>
                  </thead>
                  <tbody>
                    {snapshot.recent.map((r, i) => (
                      <tr key={`${r.sessionId}/${r.turnNumber}/${i}`} className={r.degraded ? "degraded" : ""}>
                        <td className="mono">{shortId(r.sessionId)}</td>
                        <td>{r.turnNumber}</td>
                        <td>{generatedTime(r.generatedAtUtc)}</td>
                        <td className="mono">{r.model}</td>
                        <td>
                          {r.degraded ? (
                            <span className="wq-q bad">degraded</span>
                          ) : (
                            <span className="wq-q ok">ok</span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </section>
          </div>

          <div className="wq-note">
            One warm brain serves the whole fleet, reading one session at a time. This page is
            read-only - it shows the pipeline, it never changes it. The per-session "Queue" tab in a
            session view is a different thing (the prompt composer queue).
          </div>
        </>
      )}
    </div>
  );
}

// ---- display helpers (faithful ports of the Blazor private helpers) ----

function rejectionClass(snapshot: WingmanQueueSnapshot): string {
  const b = snapshot.brain;
  if (b.consecutiveRejections >= b.rejectionThreshold) return "bad";
  return b.consecutiveRejections > 0 ? "amber" : "";
}

function kindLabel(kind: string): string {
  return kind.toLowerCase() === "explain" ? "EXPLAIN" : "BRIEF";
}

function kindClass(kind: string): string {
  return kind.toLowerCase() === "explain" ? "explain" : "brief";
}

function elapsedText(seconds: number): string {
  if (seconds < 1) return "just now";
  if (seconds < 60) return `${Math.floor(seconds)}s`;
  return `${Math.floor(seconds / 60)}m ${Math.floor(seconds % 60)}s`;
}

function shortId(value: string): string {
  return value.length <= 8 ? value : value.slice(0, 8);
}

// The brief's local wall-clock "HH:mm:ss", matching the Blazor GeneratedAtUtc.ToLocalTime() render.
function generatedTime(iso: string): string {
  if (iso.length === 0) return "-";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "-";
  return clockLabel(d);
}
