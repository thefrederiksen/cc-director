// The pure display logic of the Directors registry table (issue #1246): classifying a Director's
// health into a status badge, turning "last seen" into a sortable epoch, and gathering the repository
// names a Director hosts for the search box. These are pure functions with no React and no DOM, so the
// table's behaviour is unit-tested directly (directorsFormat.test.ts). Everything here is ASCII.

import type { SessionDto } from "@devthrottle/client-core/api/client";
import {
  ENDPOINT_STATE_UNREACHABLE_BY_NAME,
  type FleetDirector,
  type MachineError,
} from "@devthrottle/client-core/fleet/fleetClient";
import { relativeTime, repoBasename } from "./format";

/** A Director's health, computed once and reused by the status cell and the column sort. A higher rank
 *  is less healthy, so sorting the Status column descending pulls the unhealthy Directors to the top. */
export interface DirectorStatus {
  label: string;
  className: string;
  rank: number;
  title?: string;
}

// Classify a Director's health. Precedence matches the original table: an advertised name that stopped
// answering, then a Gateway-to-Director reachability error, then a terminal-stream failure, then OK.
export function directorStatus(d: FleetDirector, error: MachineError | undefined): DirectorStatus {
  if (d.advertisedEndpointState === ENDPOINT_STATE_UNREACHABLE_BY_NAME) {
    return { label: "UNREACHABLE BY NAME", className: "dstat-err", rank: 3, title: endpointTooltip(d) };
  }
  if (error !== undefined) {
    return { label: "UNREACHABLE", className: "dstat-warn", rank: 3, title: error.error };
  }
  if ((d.streamVerifyError ?? null) !== null) {
    return {
      label: "TERMINAL STREAM DOWN",
      className: "dstat-err",
      rank: 2,
      title: d.streamVerifyError ?? undefined,
    };
  }
  return { label: "OK", className: "dstat-ok", rank: 0 };
}

// The unreachable-by-name tooltip (issue #325): the Director is alive (heartbeating) - it is the
// advertised NAME that stopped answering - plus since-when and why.
export function endpointTooltip(d: FleetDirector): string {
  const since =
    (d.advertisedEndpointUnreachableSince ?? "").length > 0
      ? ` (${relativeTime(d.advertisedEndpointUnreachableSince, { withAgo: true })})`
      : "";
  return `Director is alive (heartbeating) but its advertised endpoint stopped answering${since}: ${
    d.advertisedEndpointError ?? ""
  }`;
}

// A sortable epoch for the "last seen" column: the parsed milliseconds, or 0 when absent so a Director
// the Gateway has never heard from sorts to the bottom of a newest-first sort.
export function epochOf(iso: string | null | undefined): number {
  if (iso === null || iso === undefined || iso.length === 0) return 0;
  const parsed = Date.parse(iso);
  return Number.isNaN(parsed) ? 0 : parsed;
}

// The distinct repository names running on a Director, from a list of its sessions. A Director has no
// repository of its own; it runs sessions that each open one, and this is what the search box matches
// so an operator can find the machine hosting a given repository. The empty "(no repo)" placeholder is
// dropped so it never becomes a false search hit.
export function repoNamesOf(sessions: SessionDto[]): string[] {
  const names = new Set<string>();
  for (const s of sessions) {
    const repo = repoBasename(s.repoPath);
    if (repo.length > 0 && repo !== "(no repo)") names.add(repo);
  }
  return Array.from(names);
}
