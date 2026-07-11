import { useCallback, useState } from "react";
import { type SessionFilter, EMPTY_FILTER } from "@devthrottle/client-core/sessions/filter";

// The roster filter, persisted to localStorage so a chosen machine/repo filter survives navigating into
// a session and back, and app restarts. Stored under one key as JSON; a malformed or missing value
// falls back to the empty (no) filter. The setter both updates React state and writes through to
// storage in one call so the app bar, summary strip, and roster stay in lockstep.
const STORAGE_KEY = "devthrottle.sessionFilter";

function readStored(): SessionFilter {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return EMPTY_FILTER;
    const parsed = JSON.parse(raw) as Partial<SessionFilter>;
    return {
      machines: Array.isArray(parsed.machines) ? parsed.machines.map(String) : [],
      repos: Array.isArray(parsed.repos) ? parsed.repos.map(String) : [],
    };
  } catch {
    return EMPTY_FILTER;
  }
}

export function useSessionFilter(): [SessionFilter, (next: SessionFilter) => void] {
  const [filter, setFilterState] = useState<SessionFilter>(readStored);

  const setFilter = useCallback((next: SessionFilter) => {
    setFilterState(next);
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
    } catch {
      // Storage can be unavailable (private mode / quota); the in-memory filter still applies for the
      // session, so this is a non-fatal best-effort persistence, not a feature gate.
    }
  }, []);

  return [filter, setFilter];
}
