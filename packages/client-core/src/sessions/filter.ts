// Client-side roster filtering by machine and repo (the mobile "Filter sessions" panel). The roster
// can grow large across a fleet of machines and repositories; this lets the phone narrow it to one
// machine and/or one set of repositories. The model is deliberately simple and pure so it is fully
// unit-tested and the UI (the full-screen panel) stays a thin renderer over it.
import type { SessionDto } from "../api/client";
import { repoLeaf } from "./ordering";

// The active selection. An empty list for a facet means "all" - no restriction on that facet - so the
// default (no filter) is two empty lists. Selections combine across facets (machine AND repo) and
// union within a facet (machine A OR machine B).
export interface SessionFilter {
  machines: string[];
  repos: string[];
}

export const EMPTY_FILTER: SessionFilter = { machines: [], repos: [] };

// One selectable value in a facet, with the count of sessions that carry it in the full roster. The
// count is always computed from the UNFILTERED roster so the panel shows every machine/repo you could
// pick and how many sessions each holds, regardless of what is currently selected.
export interface FacetOption {
  value: string;
  count: number;
}

// The machine a session runs on, trimmed. Empty string when the Gateway did not stamp a machine name
// (older sessions); such a session is grouped under "(unknown)" in the panel so it stays selectable.
export function machineName(s: SessionDto): string {
  return (s.machineName ?? "").trim();
}

// True when the filter restricts the roster at all (either facet has a selection). The app bar shows
// the "filter active" state and the summary strip appears only when this is true.
export function filterIsActive(f: SessionFilter): boolean {
  return f.machines.length > 0 || f.repos.length > 0;
}

// Keep a session when it matches EVERY active facet: its machine is among the selected machines (or no
// machine is selected) AND its repo is among the selected repos (or no repo is selected).
export function sessionMatchesFilter(s: SessionDto, f: SessionFilter): boolean {
  if (f.machines.length > 0 && !f.machines.includes(machineName(s))) return false;
  if (f.repos.length > 0 && !f.repos.includes(repoLeaf(s))) return false;
  return true;
}

// The roster narrowed to the sessions that match the filter, preserving input order (the caller has
// already ordered it). An inactive filter returns the roster unchanged.
export function applyFilter(sessions: SessionDto[], f: SessionFilter): SessionDto[] {
  if (!filterIsActive(f)) return sessions;
  return sessions.filter((s) => sessionMatchesFilter(s, f));
}

// The distinct machine values in the roster with per-value counts, sorted by name. Built from the full
// roster so the panel always offers every machine, whatever is selected.
export function machineFacet(sessions: SessionDto[]): FacetOption[] {
  return countBy(sessions, machineName);
}

// The distinct repo leaves in the roster with per-value counts, sorted by name.
export function repoFacet(sessions: SessionDto[]): FacetOption[] {
  return countBy(sessions, repoLeaf);
}

// A short human summary of what is selected, for the app-bar summary strip - e.g.
// "SOREN_NORTH, devthrottle". Machines first, then repos, in selection order. Empty when inactive.
export function filterSummary(f: SessionFilter): string {
  return [...f.machines, ...f.repos].filter((v) => v.length > 0).join(", ");
}

// Toggle one value in a facet list immutably: add it if absent, remove it if present. Used by the
// panel's checkboxes without the panel owning any set logic.
export function toggleValue(values: string[], value: string): string[] {
  return values.includes(value) ? values.filter((v) => v !== value) : [...values, value];
}

// Drop any selected value that no longer exists in the live roster, so a filter pinned to a machine or
// repo that has gone away does not silently hide the whole list forever. Returns the same reference
// when nothing changed so callers can skip a needless state update.
export function pruneFilter(f: SessionFilter, sessions: SessionDto[]): SessionFilter {
  const machines = new Set(sessions.map(machineName));
  const repos = new Set(sessions.map(repoLeaf));
  const keptMachines = f.machines.filter((m) => machines.has(m));
  const keptRepos = f.repos.filter((r) => repos.has(r));
  if (keptMachines.length === f.machines.length && keptRepos.length === f.repos.length) return f;
  return { machines: keptMachines, repos: keptRepos };
}

// Count sessions by a string key, dropping empty keys, sorted alphabetically by value.
function countBy(sessions: SessionDto[], key: (s: SessionDto) => string): FacetOption[] {
  const counts = new Map<string, number>();
  for (const s of sessions) {
    const value = key(s);
    if (value.length === 0) continue;
    counts.set(value, (counts.get(value) ?? 0) + 1);
  }
  return [...counts.entries()]
    .map(([value, count]) => ({ value, count }))
    .sort((a, b) => a.value.localeCompare(b.value));
}
