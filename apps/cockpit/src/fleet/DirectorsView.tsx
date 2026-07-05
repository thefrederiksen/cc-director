import { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import {
  ENDPOINT_STATE_UNREACHABLE_BY_NAME,
  getFleetDirectors,
  getSessionsEnvelope,
  type FleetDirector,
  type MachineError,
} from "@devthrottle/client-core/fleet/fleetClient";
import { Pager } from "./Pager";
import { clockLabel, portLabel, relativeTime, uptime } from "./format";

// The Director registry table (issue #975) - the React port of the Blazor Directors.razor. A plain
// paged table over GET /directors, enriched with live session counts and unreachable flags from the
// roster envelope (GET /sessions?envelope=true). A row click opens the Director-detail page; the
// status cell distinguishes unreachable-by-name, fully unreachable, terminal-stream-down, and OK.
// Both reads are same-origin through the Gateway (client-core) - never a Director address.
const POLL_MS = 5000;
const PAGE_SIZE = 25;

export function DirectorsView() {
  const navigate = useNavigate();
  const [directors, setDirectors] = useState<FleetDirector[]>([]);
  const [sessions, setSessions] = useState<SessionDto[]>([]);
  const [machineErrors, setMachineErrors] = useState<MachineError[]>([]);
  const [lastError, setLastError] = useState<string | null>(null);
  const [lastRefresh, setLastRefresh] = useState<Date | null>(null);
  const [page, setPage] = useState(1);
  // Tick the "up <uptime>" / "last seen" cells each second between the 5s polls, so the relative
  // times stay live without re-fetching.
  const [, setNow] = useState(Date.now());
  const nowRef = useRef(0);
  nowRef.current = Date.now();

  const refresh = useCallback(async (signal?: AbortSignal) => {
    try {
      const [ds, env] = await Promise.all([getFleetDirectors(signal), getSessionsEnvelope(signal)]);
      setDirectors(ds);
      setSessions(env.sessions);
      setMachineErrors(env.machineErrors);
      setLastError(null);
      setLastRefresh(new Date());
    } catch (err) {
      if (signal?.aborted === true) return;
      setLastError(err instanceof Error ? err.message : "Failed to fetch directors");
    }
  }, []);

  useEffect(() => {
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

  const ordered = [...directors].sort((a, b) => {
    const byName = (a.machineName ?? "").toLowerCase().localeCompare((b.machineName ?? "").toLowerCase());
    if (byName !== 0) return byName;
    return String(a.startedAt ?? "").localeCompare(String(b.startedAt ?? ""));
  });
  const pageCount = Math.max(1, Math.ceil(ordered.length / PAGE_SIZE));
  const clampedPage = Math.min(page, pageCount);
  const pageItems = ordered.slice((clampedPage - 1) * PAGE_SIZE, clampedPage * PAGE_SIZE);

  const sessionCount = (d: FleetDirector) =>
    sessions.filter((s) => (s.directorId ?? "").toLowerCase() === d.directorId.toLowerCase()).length;
  const errorFor = (d: FleetDirector): MachineError | undefined =>
    machineErrors.find((e) => (e.directorId ?? "").toLowerCase() === d.directorId.toLowerCase());

  return (
    <div className="dpage">
      <header className="dpage-head">
        <h1 className="dpage-h1">Directors</h1>
        <span className="dpage-sub">{directors.length} registered director{directors.length === 1 ? "" : "s"}</span>
        <span className="dpage-refreshed">
          {lastRefresh === null ? "connecting..." : `updated ${clockLabel(lastRefresh)}`}
        </span>
      </header>

      {lastError !== null && <div className="dpage-error">Gateway error: {lastError}</div>}

      {directors.length === 0 && lastError === null && lastRefresh !== null ? (
        <div className="dtbl-empty">
          No Directors registered with this Gateway. A Director appears here when it starts on this machine,
          or registers over HTTP with <code>gateway.url</code> configured.
        </div>
      ) : directors.length > 0 ? (
        <>
          <div className="dtbl-scroll">
            <table className="dtbl">
              <thead>
                <tr>
                  <th>Machine</th>
                  <th>Director</th>
                  <th>Version</th>
                  <th>Discovery</th>
                  <th>Endpoint</th>
                  <th>Started</th>
                  <th>Last seen</th>
                  <th>Sessions</th>
                  <th>Status</th>
                </tr>
              </thead>
              <tbody>
                {pageItems.map((d) => {
                  const err = errorFor(d);
                  const endpoint = d.tailnetEndpoint ?? d.controlEndpoint ?? "";
                  return (
                    <tr key={d.directorId} className="dtbl-rowlink" title="Director details"
                        onClick={() => navigate(`/directors/${encodeURIComponent(d.directorId)}`)}>
                      <td><span className="dcell-name">{d.machineName}</span> <span className="ddim">{d.user}</span></td>
                      <td className="dmono" title={d.directorId}>
                        {portLabel(d.controlEndpoint, d.tailnetEndpoint, d.directorId)} <span className="ddim">pid {d.pid}</span>
                      </td>
                      <td className="dmono">{d.version}</td>
                      <td className="ddim">{d.source === "http" ? "push (http)" : "local (file)"}</td>
                      <td className="dmono dcell-ellipsis" title={endpoint}>{endpoint}</td>
                      <td className="ddim" title={d.startedAt}>
                        {relativeTime(d.startedAt, { withAgo: true, now: nowRef.current })} <span className="ddim">(up {uptime(d.startedAt, nowRef.current)})</span>
                      </td>
                      <td className="ddim" title={d.lastSeen ?? undefined}>{relativeTime(d.lastSeen, { withAgo: true, now: nowRef.current })}</td>
                      <td>{sessionCount(d)}</td>
                      <td>
                        {d.advertisedEndpointState === ENDPOINT_STATE_UNREACHABLE_BY_NAME ? (
                          <span className="dstat-err" title={endpointTooltip(d)}>UNREACHABLE BY NAME</span>
                        ) : err !== undefined ? (
                          <span className="dstat-warn" title={err.error}>UNREACHABLE</span>
                        ) : (d.streamVerifyError ?? null) !== null ? (
                          <span className="dstat-err" title={d.streamVerifyError ?? undefined}>TERMINAL STREAM DOWN</span>
                        ) : (
                          <span className="dstat-ok">OK</span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <Pager page={clampedPage} pageSize={PAGE_SIZE} totalCount={ordered.length} onPageChange={setPage} />
        </>
      ) : null}
    </div>
  );
}

// The unreachable-by-name tooltip (issue #325): the Director is alive (heartbeating) - it is the
// advertised NAME that stopped answering - plus since-when and why.
function endpointTooltip(d: FleetDirector): string {
  const since = (d.advertisedEndpointUnreachableSince ?? "").length > 0
    ? ` (${relativeTime(d.advertisedEndpointUnreachableSince, { withAgo: true })})`
    : "";
  return `Director is alive (heartbeating) but its advertised endpoint stopped answering${since}: ${d.advertisedEndpointError ?? ""}`;
}
