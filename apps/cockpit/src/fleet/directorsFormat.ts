// The pure display logic of the Directors registry table (issue #1246): classifying a Director's
// health into a status badge, turning "last seen" into a sortable epoch, and gathering the repository
// names a Director hosts for the search box. These are pure functions with no React and no DOM, so the
// table's behaviour is unit-tested directly (directorsFormat.test.ts). Everything here is ASCII.

import type { SessionDto } from "@devthrottle/client-core/api/client";
import {
  ENDPOINT_STATE_UNREACHABLE_BY_NAME,
  REACHABILITY_STOPPED,
  type DirectorReachability,
  type FleetDirector,
  type MachineError,
} from "@devthrottle/client-core/fleet/fleetClient";
import { directorStateLabel } from "./directorPresentation";
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
// answering, then a Gateway-to-Director reachability error, then a Director that was SHUT DOWN, then a
// terminal-stream failure, then OK.
//
// "Not running" sits between them on purpose, and it is not a fault. A Director that said goodbye is
// exactly where it should be - it is simply not there to be used, which is worth showing and worth
// sorting above OK, but is nothing to investigate. Without this branch a shut-down Director read "OK":
// the row it used to be judged by (machineErrors) no longer contains it, precisely because nothing
// failed. The WORD comes from the Gateway (directorStateLabel); the rank and the colour are this
// table's own sorting and styling.
export function directorStatus(
  d: FleetDirector,
  error: MachineError | undefined,
  reach?: DirectorReachability,
): DirectorStatus {
  if (d.advertisedEndpointState === ENDPOINT_STATE_UNREACHABLE_BY_NAME) {
    return { label: "UNREACHABLE BY NAME", className: "dstat-err", rank: 3, title: endpointTooltip(d) };
  }
  if (error !== undefined) {
    return { label: "UNREACHABLE", className: "dstat-warn", rank: 3, title: error.error };
  }
  if (reach?.state === REACHABILITY_STOPPED) {
    return {
      label: directorStateLabel(reach).toUpperCase(),
      className: "dstat-idle",
      rank: 1,
      title: "This Director told the Gateway it was shutting down. It is not running, and its registration is retired.",
    };
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

// The primary label of a Director row (devthrottle_internal#1176): the user-editable display name when
// the Director reports one, else the machine name, else - only when even that is blank - the raw id.
// This is THE precedence every director surface renders, so it lives in one tested place.
export function directorPrimaryLabel(d: {
  displayName?: string;
  machineName?: string;
  directorId: string;
}): string {
  const name = (d.displayName ?? "").trim();
  if (name.length > 0) return name;
  const machine = (d.machineName ?? "").trim();
  if (machine.length > 0) return machine;
  return d.directorId;
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
