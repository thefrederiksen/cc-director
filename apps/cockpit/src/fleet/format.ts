// Display helpers shared by the Fleet, Directors, and Director-detail pages (issue #975). These are
// faithful ports of the private helpers in the Blazor Fleet.razor / Directors.razor /
// DirectorDetail.razor pages, kept pure so every fleet page formats a machine, a time, and a repo
// the same way. Session color/order helpers are NOT re-implemented here - those live in the shared
// client-core/sessions/ordering module and are reused directly.

// The leaf repo name for a card/table cell, e.g. "C:\\repos\\devthrottle" -> "devthrottle". This is the
// WORKING-TREE identity: every worktree and every clone of one repository has a different leaf, so a
// worktree cut at "D:\\ReposFred\\devthrottle-enroll-fix" reads as its own thing here, distinct from the
// repository it belongs to.
export function repoBasename(path: string | null | undefined): string {
  if (path === null || path === undefined || path.trim().length === 0) return "(no repo)";
  const norm = path.replace(/\\/g, "/").replace(/\/+$/, "");
  const i = norm.lastIndexOf("/");
  return i >= 0 ? norm.slice(i + 1) : norm;
}

// The GITHUB REPOSITORY identity for a session: the "owner/repo" name the owning Director resolved from
// the checkout's git origin remote (SessionDto.repoName). Every worktree and every per-machine clone of
// one repository shares this, so grouping by it folds a worktree back into the repository it was cut
// from - unlike repoBasename, which splits them. When the checkout is on no host we recognize (a
// local-only repo with no GitHub/Azure origin), repoName is empty and we fall back to the working-tree
// leaf so the session still lands somewhere named rather than in one anonymous bucket. This is the exact
// rule the DevThrottle Stats repo rollup uses (GatewayInputStatsAggregator: repoName ?? repo leaf), kept
// identical here so the Fleet Map and the Repos page agree on what "a repository" is.
export function repoIdentity(repoName: string | null | undefined, repoPath: string | null | undefined): string {
  if (repoName !== null && repoName !== undefined && repoName.trim().length > 0) return repoName.trim();
  return repoBasename(repoPath);
}

// There is deliberately NO humanizeState here. It was a port of the Blazor HumanizeState vocabulary
// that turned a raw activity/assessed state into words, and its last caller (the Director-detail
// table's State cell) now renders the Gateway's stamped stateLabel instead - the same fold that picks
// the row's dot colour. A helper that turns raw sensor fields into a state word IS the re-derive this
// module's header forbids, so it is gone rather than left lying around for the next agent to reach
// for. If you want a session's state in words, call stateLabel() from client-core/sessions/ordering.
//
// Note ActivityState.Idle is also a dead state - nothing in production has ever assigned it - so half
// that switch was answering about a world that does not exist.

// Seconds since an ISO timestamp, clamped at zero. Returns null when the input is absent/unparseable
// so callers can render "-".
function secondsSince(iso: string | null | undefined, now: number): number | null {
  if (iso === null || iso === undefined || iso.length === 0) return null;
  const then = Date.parse(iso);
  if (Number.isNaN(then)) return null;
  return Math.max(0, Math.floor((now - then) / 1000));
}

// A compact relative time ("just now", "5s", "3m", "2h", "4d"). `withAgo` appends " ago" (the
// Directors / Director-detail vocabulary); the Fleet cards use the bare form. `now` is injectable so
// a page that ticks a clock renders consistently without re-reading Date.now per call.
export function relativeTime(
  iso: string | null | undefined,
  opts?: { withAgo?: boolean; now?: number },
): string {
  const now = opts?.now ?? Date.now();
  const seconds = secondsSince(iso, now);
  if (seconds === null) return "-";
  const ago = opts?.withAgo === true ? " ago" : "";
  if (seconds < 5) return "just now";
  if (seconds < 60) return `${seconds}s${ago}`;
  const mins = Math.floor(seconds / 60);
  if (mins < 60) return `${mins}m${ago}`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h${ago}`;
  return `${Math.floor(hours / 24)}d${ago}`;
}

// How long a Director has been up, from its startedAt ("15m", "3h 20m", "2d 4h"). Matches the Blazor
// Uptime helper.
export function uptime(startedAtIso: string | null | undefined, now: number = Date.now()): string {
  if (startedAtIso === null || startedAtIso === undefined || startedAtIso.length === 0) return "-";
  const started = Date.parse(startedAtIso);
  if (Number.isNaN(started)) return "-";
  const totalSeconds = Math.max(0, Math.floor((now - started) / 1000));
  const totalMinutes = Math.floor(totalSeconds / 60);
  const totalHours = Math.floor(totalMinutes / 60);
  const totalDays = Math.floor(totalHours / 24);
  if (totalHours < 1) return `${totalMinutes}m`;
  if (totalDays < 1) return `${totalHours}h ${totalMinutes % 60}m`;
  return `${totalDays}d ${totalHours % 24}h`;
}

// The first eight characters of an id (or "?" when empty) - the Blazor ShortId.
export function shortId(id: string | null | undefined): string {
  if (id === null || id === undefined || id.length === 0) return "?";
  return id.slice(0, Math.min(8, id.length));
}

// The Director's short, restart-stable label (issue #237): ":<port>" parsed from controlEndpoint
// (falling back to tailnetEndpoint), or the id short-form when no port can be parsed - never blank.
export function portLabel(controlEndpoint: string | null | undefined, tailnetEndpoint: string | null | undefined, directorId: string): string {
  const port = portOf(controlEndpoint) ?? portOf(tailnetEndpoint);
  return port === null ? shortId(directorId) : `:${port}`;
}

// The port parsed from an endpoint URL (":<port>" tail), or null when none is present. Module-private:
// it was exported for the Exes page's port cell (issue #1261), and that page is gone, so portLabel is
// the only caller and the only surface worth testing.
function portOf(endpoint: string | null | undefined): string | null {
  if (endpoint === null || endpoint === undefined || endpoint.length === 0) return null;
  const m = /:(\d+)\/?$/.exec(endpoint);
  return m === null ? null : m[1];
}

// A wall-clock "HH:mm:ss" stamp for the "updated <time>" page freshness label.
export function clockLabel(when: Date): string {
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${pad(when.getHours())}:${pad(when.getMinutes())}:${pad(when.getSeconds())}`;
}
