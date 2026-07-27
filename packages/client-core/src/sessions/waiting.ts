// Waiting-time formatting + a live ticking clock for the roster's "needs you" cards (issue #844).
// A faithful port of the desktop SessionRail Waiting() ladder
// (src/CcDirector.Cockpit/Components/SessionRail.razor), adapted for the mobile card: the label
// reads "just now" for a sub-minute wait, otherwise "waiting <dur>" where <dur> climbs the ladder
// minutes -> "12m", hours -> "1h 4m", days -> "2d 3h". Kept as a pure function so it is unit-
// testable and so the ticking hook stays a thin re-render trigger that recomputes from the held
// needsYouSince (no roster refetch just to tick - issue #844 scope).
import { useEffect, useState } from "react";

// Bare elapsed-duration label from an ISO 8601 UTC stamp, climbing the same ladder the desktop
// SessionRail uses: "just now" for a sub-minute span, otherwise "12m" -> "1h 4m" -> "2d 3h". No
// prefix, so callers can render it as "waiting <dur>" (the roster) or "up <dur>" (the New-session
// machine picker, issue #848) from one source of truth.
//   sinceIso - the ISO 8601 stamp (UTC); now - current epoch milliseconds (caller's ticking clock).
// Returns "" when sinceIso is empty or unparseable so the caller can omit the segment entirely.
export function durationLabel(sinceIso: string, now: number): string {
  const trimmed = (sinceIso ?? "").trim();
  if (trimmed.length === 0) return "";
  const since = Date.parse(trimmed);
  if (Number.isNaN(since)) return "";

  const ms = now - since;
  if (Math.floor(Math.max(0, ms) / 60000) < 1) return "just now";
  return durationFromMs(ms);
}

// The ladder itself, from a bare millisecond span: "0m" -> "12m" -> "1h 4m" -> "2d 3h". THE one
// duration ladder every card stat shares (durationLabel above, the supervision stats line) - a
// second copy of these breakpoints is how two surfaces come to say the same span two different
// ways. Sub-minute reads "0m" here; the "just now" wording belongs to durationLabel's contract.
export function durationFromMs(ms: number): string {
  if (ms < 0 || !Number.isFinite(ms)) ms = 0;
  const totalMinutes = Math.floor(ms / 60000);

  const days = Math.floor(totalMinutes / 1440);
  const hours = Math.floor((totalMinutes % 1440) / 60);
  const minutes = totalMinutes % 60;

  if (days >= 1) return `${days}d ${hours}h`;
  if (hours >= 1) return `${hours}h ${minutes}m`;
  return `${minutes}m`;
}

// Compact elapsed-waiting label from a needs-you session's needsYouSince UTC stamp.
//   sinceIso - the ISO 8601 needsYouSince value (UTC) from the /sessions payload.
//   now      - the current epoch milliseconds (passed in so the caller's ticking clock drives it).
// Returns "" when sinceIso is empty or unparseable so the caller renders nothing. A sub-minute wait
// reads "just now"; anything longer is prefixed "waiting " onto the shared duration ladder.
export function waitingLabel(sinceIso: string, now: number): string {
  const bare = durationLabel(sinceIso, now);
  if (bare === "" || bare === "just now") return bare;
  return `waiting ${bare}`;
}

// A re-render trigger that updates on a fixed interval, returning the current epoch milliseconds.
// Used by the needs-you cards so their waiting label ticks up live without refetching the roster.
export function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(timer);
  }, [intervalMs]);
  return now;
}
