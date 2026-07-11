import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { gatewayErrorMessage, type SessionDto } from "@devthrottle/client-core/api/client";
import {
  getFleetDirectors,
  getSessionsEnvelope,
  type FleetDirector,
  type MachineError,
} from "@devthrottle/client-core/fleet/fleetClient";
import { DataTable, PageHeader, type DataTableColumn } from "../components";
import { clockLabel, relativeTime } from "./format";
import { directorStatus, epochOf, repoNamesOf } from "./directorsFormat";

// The Director registry table (issue #975; rebuilt on the shared DataTable in #1246) - the React view
// over GET /directors, enriched with live session counts and unreachable flags from the roster
// envelope (GET /sessions?envelope=true). It now uses the shared sortable, searchable DataTable
// (issue #1245) so the registry fits a laptop without horizontal scrolling: only the five columns that
// answer "which machine, is it healthy, how busy, what version, when last seen" stay here; every other
// registration fact (process id, discovery, endpoints, start time) lives on the Director detail page's
// Registration panel, one click away. Both reads are same-origin through the Gateway - never a Director
// address.
const POLL_MS = 5000;

export function DirectorsView() {
  const navigate = useNavigate();
  const [directors, setDirectors] = useState<FleetDirector[]>([]);
  const [sessions, setSessions] = useState<SessionDto[]>([]);
  const [machineErrors, setMachineErrors] = useState<MachineError[]>([]);
  const [lastError, setLastError] = useState<string | null>(null);
  const [lastRefresh, setLastRefresh] = useState<Date | null>(null);
  // Tick the "last seen" cell each second between the 5s polls, so the relative times stay live
  // without re-fetching.
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
      setLastError(gatewayErrorMessage(err));
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

  // Sessions grouped by their owning Director, so a session count and the repositories a Director hosts
  // are one lookup each rather than a full scan per row.
  const sessionsByDirector = useMemo(() => {
    const map = new Map<string, SessionDto[]>();
    for (const s of sessions) {
      const key = (s.directorId ?? "").toLowerCase();
      const list = map.get(key);
      if (list === undefined) map.set(key, [s]);
      else list.push(s);
    }
    return map;
  }, [sessions]);

  const errorByDirector = useMemo(() => {
    const map = new Map<string, MachineError>();
    for (const e of machineErrors) map.set((e.directorId ?? "").toLowerCase(), e);
    return map;
  }, [machineErrors]);

  const sessionCount = useCallback(
    (d: FleetDirector) => sessionsByDirector.get(d.directorId.toLowerCase())?.length ?? 0,
    [sessionsByDirector],
  );

  const statusOf = useCallback(
    (d: FleetDirector) => directorStatus(d, errorByDirector.get(d.directorId.toLowerCase())),
    [errorByDirector],
  );

  // The searchable text of a Director row: machine name, the user, the version, and the repositories it
  // hosts - so the search box finds a Director by its machine or by a repository running on it.
  const searchableText = useCallback(
    (d: FleetDirector): string => {
      const repos = repoNamesOf(sessionsByDirector.get(d.directorId.toLowerCase()) ?? []);
      return [d.machineName ?? "", d.directorId, d.user ?? "", d.version ?? "", ...repos].join(" ");
    },
    [sessionsByDirector],
  );

  // Five columns only, so the registry fits a 1366-wide window with no horizontal scroll. Everything
  // else moved to the Director detail page's Registration panel.
  const columns = useMemo<DataTableColumn<FleetDirector>[]>(
    () => [
      {
        key: "machine",
        header: "Machine",
        sortable: true,
        sortValue: (d) => (d.machineName ?? d.directorId).toLowerCase(),
        render: (d) => (
          <span className="dcell-machine">
            <span className="dcell-name">
              {(d.machineName ?? "").trim().length > 0 ? d.machineName : d.directorId}
            </span>
            {(d.user ?? "").trim().length > 0 && <span className="ddim"> {d.user}</span>}
          </span>
        ),
      },
      {
        key: "status",
        header: "Status",
        width: "220px",
        sortable: true,
        sortValue: (d) => statusOf(d).rank,
        render: (d) => {
          const status = statusOf(d);
          return (
            <span className={status.className} title={status.title}>
              {status.label}
            </span>
          );
        },
      },
      {
        key: "sessions",
        header: "Sessions",
        width: "100px",
        align: "right",
        sortable: true,
        sortValue: (d) => sessionCount(d),
        render: (d) => sessionCount(d),
      },
      {
        key: "version",
        header: "Version",
        width: "120px",
        className: "dmono",
        sortable: true,
        sortValue: (d) => d.version ?? "",
        render: (d) => d.version ?? "-",
      },
      {
        key: "lastseen",
        header: "Last seen",
        width: "150px",
        className: "ddim",
        sortable: true,
        sortValue: (d) => epochOf(d.lastSeen),
        render: (d) => (
          <span title={d.lastSeen ?? undefined}>
            {relativeTime(d.lastSeen, { withAgo: true, now: nowRef.current })}
          </span>
        ),
      },
    ],
    [statusOf, sessionCount],
  );

  return (
    <div className="dpage">
      <PageHeader
        title="Directors"
        subtitle={`${directors.length} registered director${directors.length === 1 ? "" : "s"} across the fleet. Click a row for the full registration and its sessions.`}
      />

      {lastError !== null && <div className="dpage-error">{lastError}</div>}

      {lastRefresh !== null && (
        <DataTable<FleetDirector>
          columns={columns}
          rows={directors}
          rowKey={(d) => d.directorId}
          searchableText={searchableText}
          searchPlaceholder="Search by machine or repository"
          defaultSort={{ columnKey: "machine", direction: "asc" }}
          emptyMessage={
            <>
              No Directors registered with this Gateway. A Director appears here when it starts on this
              machine, or registers over HTTP with <code>gateway.url</code> configured.
            </>
          }
          toolbarExtra={
            <span className="dpage-refreshed">
              {lastRefresh === null ? "connecting..." : `updated ${clockLabel(lastRefresh)}`}
            </span>
          }
          onRowActivate={(d) => navigate(`/directors/${encodeURIComponent(d.directorId)}`)}
        />
      )}
    </div>
  );
}
