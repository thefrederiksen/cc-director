import { useEffect, useState } from "react";
import { gatewayFetch, authHeaders } from "../api/client";

// The browser's copy of the user's snooze lengths and default, read FROM the Gateway.
//
// Two facts force this shape, and they are the same two the desktop's SnoozeOptionsCache faces. The
// setting is Gateway-owned and per-user, so it must be asked for rather than assumed. And a session menu
// must open instantly: EVERY rail card renders its own menu, so a fetch per menu would mean a burst of
// identical requests and a menu that pops in late.
//
// So: one module-level cache shared by every menu on the page, one in-flight request at a time, and a
// re-read only when the value is older than STALE_AFTER_MS - long enough that opening menus is never
// chatty, short enough that editing the lengths in Settings shows up without a reload.
//
// The cache is null until the first successful read. That is not a gap to paper over: null means the
// browser does not know the user's lengths, and the menu then offers only the plain Snooze - which still
// works, because a hold with no length makes the Gateway apply the default. It never invents lengths.

export interface SnoozeOptions {
  presets: number[];
  defaultMinutes: number;
  maxPresets: number;
}

const STALE_AFTER_MS = 5 * 60 * 1000;

let cached: SnoozeOptions | null = null;
let fetchedAt = 0;
let inFlight: Promise<SnoozeOptions | null> | null = null;
const listeners = new Set<(o: SnoozeOptions | null) => void>();

// GET /gateway/snooze-presets - the lengths every Snooze menu offers and which one is the default.
async function fetchSnoozeOptions(): Promise<SnoozeOptions | null> {
  const res = await gatewayFetch("/gateway/snooze-presets", {
    headers: { Accept: "application/json", ...authHeaders() },
  });
  if (!res.ok) return null;
  const body = (await res.json()) as Partial<SnoozeOptions>;
  if (!Array.isArray(body.presets) || body.presets.length === 0) return null;
  return {
    presets: body.presets.map(Number),
    defaultMinutes: Number(body.defaultMinutes ?? 60),
    maxPresets: Number(body.maxPresets ?? 5),
  };
}

/**
 * Refresh the shared cache unless a refresh is already running. Never throws: an unreachable Gateway
 * leaves the last-known value in place (or null if there never was one) rather than breaking a menu. This
 * is the ONE place that decides that, and it never substitutes a made-up list.
 */
export async function refreshSnoozeOptions(): Promise<SnoozeOptions | null> {
  if (inFlight !== null) return inFlight;
  inFlight = (async () => {
    try {
      const options = await fetchSnoozeOptions();
      if (options !== null) {
        cached = options;
        fetchedAt = Date.now();
        listeners.forEach((l) => l(cached));
      }
      return cached;
    } catch {
      return cached;
    } finally {
      inFlight = null;
    }
  })();
  return inFlight;
}

/** The cached lengths without touching the network. Null when never successfully read. */
export function peekSnoozeOptions(): SnoozeOptions | null {
  return cached;
}

/** Test seam: drop the cache so a test starts from "never read". */
export function resetSnoozeOptionsCache(): void {
  cached = null;
  fetchedAt = 0;
  inFlight = null;
}

/**
 * The user's snooze lengths, for a menu. Returns the cached value immediately (null on a cold page) and
 * re-renders when the first read lands. Every menu on the page shares one cache and one request.
 */
export function useSnoozeOptions(): SnoozeOptions | null {
  const [options, setOptions] = useState<SnoozeOptions | null>(cached);

  useEffect(() => {
    listeners.add(setOptions);
    if (cached === null || Date.now() - fetchedAt > STALE_AFTER_MS) {
      void refreshSnoozeOptions();
    }
    return () => {
      listeners.delete(setOptions);
    };
  }, []);

  return options;
}
