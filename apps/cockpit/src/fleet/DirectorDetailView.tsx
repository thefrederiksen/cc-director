import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { gatewayErrorMessage, getRepos, type RepoInfo, type SessionDto } from "@devthrottle/client-core/api/client";
import { dotColor, effectiveColor, inDesktopOrder } from "@devthrottle/client-core/sessions/ordering";
import {
  getDirectorSettings,
  getFleetDirectors,
  getSessionsEnvelope,
  putDirectorSettings,
  type FleetDirector,
  type MachineError,
} from "@devthrottle/client-core/fleet/fleetClient";
import { clockLabel, humanizeState, portLabel, relativeTime, repoBasename, uptime } from "./format";

// The standalone Director page (issue #975) - the React port of the Blazor DirectorDetail.razor:
// registration facts, health, the Director's live sessions, and the repositories it offers for new
// sessions. It also carries the Director-scoped settings editor (raw JSON GET/PUT
// /directors/{id}/settings, routed by Director id) that the Blazor Cockpit exposed as a modal - so
// the whole per-machine read/write surface lands here. Every read/write is same-origin through the
// Gateway (client-core), never a Director address.
const POLL_MS = 5000;
const REPO_EVERY_TICKS = 6; // the repo list proxies to the Director itself - slower, every 30s.

export function DirectorDetailView() {
  const { directorId = "" } = useParams();
  const navigate = useNavigate();

  const [director, setDirector] = useState<FleetDirector | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [sessions, setSessions] = useState<SessionDto[]>([]);
  const [machineError, setMachineError] = useState<MachineError | null>(null);
  const [repos, setRepos] = useState<RepoInfo[] | null>(null);
  const [reposError, setReposError] = useState<string | null>(null);
  const [lastError, setLastError] = useState<string | null>(null);
  const [lastRefresh, setLastRefresh] = useState<Date | null>(null);
  const [, setNow] = useState(Date.now());
  const nowRef = useRef(0);
  nowRef.current = Date.now();
  const tickRef = useRef(0);

  const refresh = useCallback(async (signal?: AbortSignal) => {
    try {
      const [ds, env] = await Promise.all([getFleetDirectors(signal), getSessionsEnvelope(signal)]);
      const d = ds.find((x) => x.directorId.toLowerCase() === directorId.toLowerCase()) ?? null;
      setDirector(d);
      setNotFound(d === null);
      setSessions(env.sessions);
      setMachineError(env.machineErrors.find((e) => (e.directorId ?? "").toLowerCase() === directorId.toLowerCase()) ?? null);
      setLastError(null);
      setLastRefresh(new Date());

      const tick = tickRef.current;
      if (d !== null && env.machineErrors.every((e) => (e.directorId ?? "").toLowerCase() !== directorId.toLowerCase()) && tick % REPO_EVERY_TICKS === 0) {
        try {
          setRepos(await getRepos(directorId, signal));
          setReposError(null);
        } catch (err) {
          if (signal?.aborted !== true) setReposError(gatewayErrorMessage(err));
        }
      }
      tickRef.current = tick + 1;
    } catch (err) {
      if (signal?.aborted === true) return;
      setLastError(gatewayErrorMessage(err));
    }
  }, [directorId]);

  useEffect(() => {
    // A new director id starts a clean load (Blazor OnParametersSetAsync reset).
    setDirector(null);
    setNotFound(false);
    setRepos(null);
    setReposError(null);
    tickRef.current = 0;
    const controller = new AbortController();
    void refresh(controller.signal);
    const poll = window.setInterval(() => void refresh(controller.signal), POLL_MS);
    const clock = window.setInterval(() => setNow(Date.now()), 1000);
    return () => {
      controller.abort();
      window.clearInterval(poll);
      window.clearInterval(clock);
    };
  }, [refresh]);

  const mine = inDesktopOrder(sessions.filter((s) => (s.directorId ?? "").toLowerCase() === directorId.toLowerCase()));

  if (notFound) {
    return (
      <div className="dpage">
        {lastError !== null && <div className="dpage-error">{lastError}</div>}
        <header className="dpage-head"><h1 className="dpage-h1">Director</h1></header>
        <div className="dtbl-empty">
          No Director with id <span className="dmono">{directorId}</span> is registered with this Gateway - it has
          shut down and deregistered, or its registration aged out.{" "}
          <Link className="ddet-link" to="/directors">Back to all directors</Link>
        </div>
      </div>
    );
  }

  if (director === null) {
    // The initial load has not produced a Director yet. Distinguish "still loading" from "the load
    // failed" (issue #1028): a failed initial fetch used to fall through to a permanent "loading..."
    // sub-label under an error banner. When lastError is set and we have no Director, show an explicit
    // error state (the page keeps polling, so it recovers on its own once the Gateway answers).
    if (lastError !== null) {
      return (
        <div className="dpage">
          <header className="dpage-head"><h1 className="dpage-h1">Director</h1><span className="dpage-sub">unavailable</span></header>
          <div className="dpage-error">{lastError}</div>
          <div className="dtbl-empty">
            Could not load this Director from the Gateway. It will keep retrying automatically.{" "}
            <Link className="ddet-link" to="/directors">Back to all directors</Link>
          </div>
        </div>
      );
    }
    return (
      <div className="dpage">
        <header className="dpage-head"><h1 className="dpage-h1">Director</h1><span className="dpage-sub">loading...</span></header>
      </div>
    );
  }

  const d = director;
  const unreachable = machineError !== null;

  return (
    <div className="dpage">
      {lastError !== null && <div className="dpage-error">{lastError}</div>}

      <header className="dpage-head ddet-head">
        <h1 className="dpage-h1">{d.machineName}</h1>
        <span className="dmono dpage-sub" title={d.directorId}>{portLabel(d.controlEndpoint, d.tailnetEndpoint, d.directorId)}</span>
        <span className="dpage-sub">v{d.version}</span>
        {unreachable ? (
          <span className="ddet-chip dstat-warn" title={machineError?.error}>UNREACHABLE</span>
        ) : (
          <span className="ddet-chip dstat-ok">OK</span>
        )}
        <span className="dpage-refreshed">{lastRefresh === null ? "" : `updated ${clockLabel(lastRefresh)}`}</span>
      </header>

      {unreachable && (
        <div className="dpage-error">
          The Gateway cannot reach this Director right now: {machineError?.error}. Registration facts below may be
          stale; its sessions are missing.
        </div>
      )}

      <div className="ddet-cols">
        <div className="ddet-main">
          <section className="ddet-sec">
            <div className="ddet-sec-head">
              <h2>Sessions</h2>
            </div>
            {mine.length === 0 ? (
              <div className="ddet-quiet">No sessions running on this Director.</div>
            ) : (
              <div className="dtbl-scroll">
                <table className="dtbl">
                  <thead>
                    <tr><th>Session</th><th>Repo</th><th>State</th><th>Last activity</th><th /></tr>
                  </thead>
                  <tbody>
                    {mine.map((s) => {
                      const sid = s.sessionId ?? "";
                      const briefing = s.briefingState === "Briefing" && (s.statusColor ?? "").toLowerCase() === "red";
                      return (
                        <tr key={sid} className="dtbl-rowlink" title="Session details"
                            onClick={() => navigate(`/session/${encodeURIComponent(sid)}`)}>
                          <td>
                            <span className="dcell-dot" style={{ background: dotColor(effectiveColor(s)) }} title={s.lastStatusReason ?? undefined} />
                            <span className="dcell-name">{(s.name ?? "").trim().length === 0 ? repoBasename(s.repoPath) : s.name}</span>
                            {s.onHold && <span className="dtag dtag-hold">HOLD</span>}
                            {briefing ? (
                              <div className="dcell-sub dcell-briefing">wingman reading...</div>
                            ) : (s.railLine ?? "").trim().length > 0 ? (
                              <div className="dcell-sub">{s.railLine}</div>
                            ) : null}
                          </td>
                          <td className="dcell-ellipsis" title={s.repoPath ?? undefined}>{repoBasename(s.repoPath)}</td>
                          <td className="ddim">{humanizeState(s.assessedState ?? s.activityState)}</td>
                          <td className="ddim" title={s.lastActivityAt ?? undefined}>{relativeTime(s.lastActivityAt, { withAgo: true, now: nowRef.current })}</td>
                          <td>
                            <Link className="ddet-link" to={`/session/${encodeURIComponent(sid)}`} onClick={(e) => e.stopPropagation()}>drive &rarr;</Link>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          <section className="ddet-sec">
            <div className="ddet-sec-head">
              <h2>Repositories</h2>
              <span className="ddet-sec-meta">{repos === null ? "" : `${repos.length} registered`}</span>
            </div>
            {reposError !== null ? (
              <div className="ddet-quiet">Could not list repositories: {reposError}</div>
            ) : repos === null ? (
              <div className="ddet-quiet">Loading...</div>
            ) : repos.length === 0 ? (
              <div className="ddet-quiet">No repositories registered on this Director.</div>
            ) : (
              <div className="dtbl-scroll">
                <table className="dtbl">
                  <thead><tr><th>Name</th><th>Path</th><th>Last used</th></tr></thead>
                  <tbody>
                    {[...repos].sort((a, b) => String(b.lastUsed ?? "").localeCompare(String(a.lastUsed ?? ""))).map((r) => (
                      <tr key={r.path}>
                        <td className="dcell-name">{r.name}</td>
                        <td className="dmono dcell-ellipsis" title={r.path}>{r.path}</td>
                        <td className="ddim" title={r.lastUsed ?? undefined}>{relativeTime(r.lastUsed, { withAgo: true, now: nowRef.current })}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          <DirectorSettings directorId={d.directorId} reachable={!unreachable} />
        </div>

        <div className="ddet-side">
          <section className="ddet-sec">
            <div className="ddet-sec-head"><h2>Registration</h2></div>
            <dl className="ddet-kv">
              <dt>Director id</dt><dd className="dmono">{d.directorId}</dd>
              <dt>Machine</dt><dd>{d.machineName}</dd>
              <dt>User</dt><dd>{d.user}</dd>
              <dt>Process id</dt><dd className="dmono">{d.pid}</dd>
              <dt>Version</dt><dd className="dmono">{d.version}</dd>
              <dt>Discovery</dt><dd>{d.source === "http" ? "push (HTTP registration)" : "local (file watch)"}</dd>
              <dt>Control endpoint</dt><dd className="dmono">{d.controlEndpoint}</dd>
              {(d.tailnetEndpoint ?? "").trim().length > 0 && (
                <>
                  <dt>Tailnet endpoint</dt><dd className="dmono">{d.tailnetEndpoint}</dd>
                </>
              )}
              <dt>Started</dt><dd title={d.startedAt}>{relativeTime(d.startedAt, { withAgo: true, now: nowRef.current })} (up {uptime(d.startedAt, nowRef.current)})</dd>
              <dt>Last seen</dt><dd title={d.lastSeen ?? undefined}>{relativeTime(d.lastSeen, { withAgo: true, now: nowRef.current })}</dd>
              <dt>Terminal stream</dt>
              {(d.streamVerifyError ?? null) !== null ? (
                <dd className="dstat-err" title={d.streamVerifyError ?? undefined}>DOWN - WebSocket stream unreachable</dd>
              ) : (d.streamVerifiedAt ?? null) !== null ? (
                <dd className="dstat-ok" title={d.streamVerifiedAt ?? undefined}>OK (verified {relativeTime(d.streamVerifiedAt, { withAgo: true, now: nowRef.current })})</dd>
              ) : (
                <dd className="ddim">not verified</dd>
              )}
            </dl>
          </section>
        </div>
      </div>
    </div>
  );
}

// The Director-scoped settings editor: a raw-JSON GET/PUT round-trip (routed by Director id through
// the Gateway). It matches the Blazor Cockpit's settings modal - a free-form JSON editor, not a
// structured form, because the Director's settings body is an opaque object the Director owns. Load
// pretty-prints the JSON; Save validates (JSON.parse) before writing so a malformed edit never
// reaches the wire, and the Director re-applies live.
function DirectorSettings({ directorId, reachable }: { directorId: string; reachable: boolean }) {
  const [text, setText] = useState<string>("");
  const [loaded, setLoaded] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    setBusy(true);
    setError(null);
    setStatus(null);
    try {
      const raw = await getDirectorSettings(directorId);
      // Pretty-print for editing; keep the raw text if it is not valid JSON so nothing is lost.
      let pretty = raw;
      try {
        pretty = JSON.stringify(JSON.parse(raw), null, 2);
      } catch {
        pretty = raw;
      }
      setText(pretty);
      setLoaded(true);
      setStatus("Loaded");
    } catch (err) {
      setError(gatewayErrorMessage(err));
    } finally {
      setBusy(false);
    }
  }, [directorId]);

  const save = async () => {
    setError(null);
    setStatus(null);
    // Validate the edit before sending it (fail loud on a malformed body, never write it).
    let normalized: string;
    try {
      normalized = JSON.stringify(JSON.parse(text));
    } catch {
      setError("Settings are not valid JSON - fix the text before saving.");
      return;
    }
    setBusy(true);
    try {
      await putDirectorSettings(directorId, normalized);
      setStatus("Saved - the Director re-applied it live.");
    } catch (err) {
      setError(gatewayErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="ddet-sec">
      <div className="ddet-sec-head">
        <h2>Settings</h2>
        <span className="ddet-sec-meta">GET/PUT /directors/{"{id}"}/settings</span>
      </div>
      {!loaded ? (
        <div className="ddet-settings-load">
          <p className="ddet-quiet">The Director's raw settings JSON, read and written by Director id through the Gateway.</p>
          <button type="button" className="ddet-btn" onClick={() => void load()} disabled={busy || !reachable}>
            {busy ? "Loading..." : "Load settings"}
          </button>
          {!reachable && <span className="ddet-settings-status">Director is unreachable - settings cannot be read right now.</span>}
        </div>
      ) : (
        <div className="ddet-settings">
          <textarea
            className="ddet-settings-box"
            value={text}
            spellCheck={false}
            onChange={(e) => setText(e.target.value)}
          />
          <div className="ddet-settings-actions">
            <button type="button" className="ddet-btn ddet-btn-primary" onClick={() => void save()} disabled={busy}>
              {busy ? "Saving..." : "Save"}
            </button>
            <button type="button" className="ddet-btn" onClick={() => void load()} disabled={busy}>Reload</button>
            {status !== null && <span className="ddet-settings-status">{status}</span>}
          </div>
        </div>
      )}
      {error !== null && <div className="ddet-settings-error">{error}</div>}
    </section>
  );
}
