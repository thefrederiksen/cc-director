import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  buildStartSlot,
  deleteSlot,
  getExes,
  killDirector,
  type ExesDirector,
  type ExesList,
  type ExesSession,
} from "@devthrottle/client-core/exes/exesClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";

// The Exes management page (issue #977, epic #967) - the React port of the Blazor Cockpit
// Exes.razor(.css) (#183). It lists the Directors running on THIS computer + their sessions and the
// 1-4 build slots, refreshes on a 3s timer that never fires over an in-flight build, and offers
// Kill / Build & start / Delete against the same Gateway endpoints. It reads and drives same-origin
// through the Gateway front door (client-core) - never a Director address.
const REFRESH_MS = 3000;

export function ExesView() {
  const [data, setData] = useState<ExesList | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false); // never refresh over an in-flight build (mirrors state.busy)
  const [buildingSlot, setBuildingSlot] = useState<number | null>(null);

  // busyRef mirrors busy so the interval callback reads the latest value without re-subscribing.
  const busyRef = useRef(false);
  busyRef.current = busy;

  const refresh = useCallback(async (signal?: AbortSignal) => {
    if (busyRef.current) return; // never refresh over an in-flight build
    try {
      const fresh = await getExes(signal);
      setData(fresh);
      setError(null);
    } catch (err) {
      if (signal?.aborted === true) return;
      setError(`Failed to load: ${gatewayErrorMessage(err)}`);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void refresh(controller.signal);
    const timer = window.setInterval(() => void refresh(controller.signal), REFRESH_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [refresh]);

  const hasRepoRoot = data !== null && data.repoRoot.trim().length > 0;
  const statsText =
    data === null
      ? "loading..."
      : `${data.directors.length} director${data.directors.length === 1 ? "" : "s"} on ${data.machineName}`;

  // ---- actions ----
  const killDir = async (dir: ExesDirector) => {
    const label = dir.slot != null ? `slot ${dir.slot}` : `PID ${dir.pid}`;
    const ok = window.confirm(
      `Kill Director ${label} (PID ${dir.pid})?\n\nThis terminates the process and ALL of its running sessions. Unsaved work in those sessions will be lost.`,
    );
    if (!ok) return;
    try {
      await killDirector(dir.directorId);
    } catch (err) {
      window.alert(`Kill failed: ${gatewayErrorMessage(err)}`);
    }
    await new Promise((r) => window.setTimeout(r, 600));
    await refresh();
  };

  const removeSlot = async (n: number) => {
    const ok = window.confirm(
      `Delete the slot ${n} build?\n\nThis removes local_builds/cc-director${n}.exe from disk. You can rebuild it with "Build & start".`,
    );
    if (!ok) return;
    try {
      await deleteSlot(n);
    } catch (err) {
      window.alert(`Delete failed: ${gatewayErrorMessage(err)}`);
    }
    await refresh();
  };

  const buildStart = async (n: number) => {
    const ok = window.confirm(
      `Build slot ${n} and launch it?\n\nThis runs the build script (about a minute) and then starts cc-director${n}.exe. The slot must not already be running.`,
    );
    if (!ok) return;
    setBusy(true);
    busyRef.current = true;
    setBuildingSlot(n);
    try {
      const result = await buildStartSlot(n);
      window.alert(`Slot ${n} built and started (PID ${result.pid}).`);
    } catch (err) {
      window.alert(`Build & start failed:\n\n${gatewayErrorMessage(err)}`);
    } finally {
      setBusy(false);
      busyRef.current = false;
      setBuildingSlot(null);
    }
    await refresh();
  };

  return (
    <div className="ex-root">
      <header className="ex-header">
        <h1>DEVTHROTTLE &middot; EXES</h1>
        <span className="ex-stats">{statsText}</span>
        <span className="ex-spacer" />
        <Link className="ex-link ex-link-accent" to="/">
          sessions
        </Link>
        <Link className="ex-link ex-link-accent" to="/transcripts">
          transcripts
        </Link>
      </header>

      <main className="ex-main">
        {error !== null && <div className="ex-error">{error}</div>}

        {data === null ? (
          <div className="ex-empty">Loading...</div>
        ) : (
          <>
            {!hasRepoRoot && (
              <div className="ex-notice">
                Slot management is unavailable: the Gateway is not running from inside the cc-director
                repo, so build scripts and local_builds cannot be located.
              </div>
            )}

            {/* ----- running directors ----- */}
            <h2 className="ex-section">
              Running directors on this computer
              {hasRepoRoot && <span className="ex-repo-root"> &middot; {data.repoRoot}</span>}
            </h2>

            {data.directors.length === 0 ? (
              <div className="ex-empty">No Director processes are running on this computer.</div>
            ) : (
              data.directors.map((dir) => (
                <div className="ex-dir" key={dir.directorId}>
                  <div className="ex-head">
                    <span className={`ex-badge ${dir.slot == null ? "ex-gray" : ""}`}>
                      {dir.slot == null ? "no slot" : `slot ${dir.slot}`}
                    </span>
                    <span className="ex-meta">
                      PID <b>{dir.pid}</b> &middot; port <b>{portOf(dir.controlEndpoint)}</b> &middot; v
                      {dir.version ?? "?"} &middot; up {relativeTime(dir.startedAt)}
                    </span>
                    <span className="ex-spacer" />
                    {dir.directorUrl && dir.directorUrl.trim().length > 0 && (
                      <a className="ex-btn" href={dir.directorUrl}>
                        Director &rarr;
                      </a>
                    )}
                    <button className="ex-btn ex-danger" onClick={() => void killDir(dir)} disabled={busy}>
                      Kill
                    </button>
                  </div>
                  {dir.exePath.trim().length > 0 && (
                    <div className="ex-exe" title={dir.exePath}>
                      {dir.exePath}
                    </div>
                  )}
                  <div className="ex-sessions">
                    {dir.sessionError && dir.sessionError.trim().length > 0 ? (
                      <div className="ex-none">sessions unavailable: {dir.sessionError}</div>
                    ) : dir.sessions.length === 0 ? (
                      <div className="ex-none">No sessions.</div>
                    ) : (
                      dir.sessions.map((s) => (
                        <div className="ex-sess" key={s.sessionId}>
                          <span className={`ex-dot ${colorClass(s)}`} />
                          {!s.name || s.name.trim().length === 0 ? (
                            <span className="ex-sname ex-unnamed">(unnamed)</span>
                          ) : (
                            <span className="ex-sname">{s.name}</span>
                          )}
                          <span className="ex-agent-pill">
                            {!s.agent || s.agent.trim().length === 0 ? "?" : s.agent}
                          </span>
                          <span className="ex-sstate">
                            {repoBasename(s.repoPath)} &middot; {humanizeState(s.activityState)}
                          </span>
                        </div>
                      ))
                    )}
                  </div>
                </div>
              ))
            )}

            {/* ----- build slots ----- */}
            <h2 className="ex-section">Build slots (1-4)</h2>
            {!hasRepoRoot ? (
              <div className="ex-empty">Unavailable (see notice above).</div>
            ) : (
              <div className="ex-slots">
                {data.slots.map((sl) => {
                  const running = sl.running != null;
                  const statusCls = running ? "ex-running" : sl.exists ? "ex-built" : "ex-missing";
                  const statusTxt = running
                    ? `running (PID ${sl.running?.pid})`
                    : sl.exists
                    ? "built, stopped"
                    : "not built";
                  return (
                    <div className="ex-slot" key={sl.slot}>
                      <div className="ex-shead">
                        <span className="ex-stitle">Slot {sl.slot}</span>
                        <span className="ex-spacer" />
                        <span className={`ex-status ${statusCls}`}>{statusTxt}</span>
                      </div>
                      {sl.exists ? (
                        <div className="ex-sub">
                          {fmtSize(sl.sizeBytes)} &middot; built {relativeTime(sl.lastBuiltUtc)} ago
                        </div>
                      ) : (
                        <div className="ex-sub">no exe in local_builds</div>
                      )}
                      <div className="ex-actions">
                        <button className="ex-btn" onClick={() => void buildStart(sl.slot)} disabled={running || busy}>
                          {buildingSlot === sl.slot ? "Building..." : "Build & start"}
                        </button>
                        <button
                          className="ex-btn ex-danger"
                          onClick={() => void removeSlot(sl.slot)}
                          disabled={!sl.exists || running || busy}
                        >
                          Delete
                        </button>
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </>
        )}
      </main>
    </div>
  );
}

// ---- display helpers (mirroring exes.html / Exes.razor exactly) ----
function portOf(endpoint: string | null | undefined): string {
  if (!endpoint || endpoint.trim().length === 0) return "?";
  const m = endpoint.match(/:(\d+)\/?$/);
  return m ? m[1] : "?";
}

function repoBasename(path: string | null | undefined): string {
  if (!path || path.trim().length === 0) return "(no repo)";
  const norm = path.replace(/\\/g, "/").replace(/\/+$/, "");
  const i = norm.lastIndexOf("/");
  return i >= 0 ? norm.slice(i + 1) : norm;
}

function colorClass(s: ExesSession): string {
  const c = (s.statusColor ?? "").toLowerCase();
  return c === "red" || c === "yellow" || c === "green" || c === "blue" ? c : "unknown";
}

function humanizeState(state: string | null | undefined): string {
  switch (state) {
    case "WaitingForInput":
      return "Waiting for input";
    case "WaitingForPerm":
      return "Waiting for permission";
    case "Idle":
      return "Idle";
    case "Working":
      return "Working";
    case "Starting":
      return "Starting";
    case "Exited":
      return "Exited";
    case null:
    case undefined:
    case "":
      return "-";
    default:
      return state;
  }
}

function fmtSize(bytes: number): string {
  if (bytes === 0) return "0 B";
  const mb = bytes / (1024 * 1024);
  if (mb >= 1) return `${mb.toFixed(1)} MB`;
  return `${Math.round(bytes / 1024)} KB`;
}

// Relative time from a UTC timestamp, matching exes.html's relativeTime(): seconds / minutes /
// "Hh Mm" / days.
function relativeTime(utc: string | null | undefined): string {
  if (!utc || utc.trim().length === 0) return "-";
  const t = new Date(utc).getTime();
  if (Number.isNaN(t)) return "-";
  const sec = Math.max(0, Math.floor((Date.now() - t) / 1000));
  if (sec < 60) return `${sec}s`;
  const m = Math.floor(sec / 60);
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ${m % 60}m`;
  return `${Math.floor(h / 24)}d`;
}
