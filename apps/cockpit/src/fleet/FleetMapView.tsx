import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { gatewayErrorMessage, type SessionDto } from "@devthrottle/client-core/api/client";
import { dotColor, effectiveColor, stateLabel } from "@devthrottle/client-core/sessions/ordering";
import {
  getSessionsEnvelope,
  reachabilityFor,
  reachabilityLastSeen,
  REACHABILITY_OFFLINE,
  REACHABILITY_WOBBLY,
  type DirectorReachability,
  type MachineError,
} from "@devthrottle/client-core/fleet/fleetClient";
import { repoBasename, relativeTime } from "./format";

// Per-Director reachability for the Online / Wobbly / Offline node rendering (issue #1215), provided at
// the Fleet Map root and read by each NodeCard so the cards dim in place without prop-drilling.
const ReachabilityContext = createContext<DirectorReachability[]>([]);

// The Fleet Map (issue #1109): a live, spatial view of everything running across the tailnet. Where
// the Fleet page (FleetView) is a list of cards grouped by machine, this is a node canvas - a root
// node for the Gateway branching down to grouped "lane" panels of terminal-window session cards,
// joined by SVG elbow connectors, with one status dot per node. The same fleet is re-laid-out on the
// fly three ways (the pivot switch): by machine (machine -> Director -> session, the topology), by
// repository (what we are building), or by agent (Claude/Codex/Gemini/... - the workforce).
//
// A Wingman narration toggle overlays each node with the session's live one-line read (the SessionDto
// railLine the roster already returns) - opt-in, a pure display layer, never a new fetch.
//
// It reads the same same-origin envelope the Fleet page polls (GET /sessions?envelope=true) through
// client-core - never a Director address - and reuses the ONE shared effective-color rule so a node's
// dot matches every other Cockpit surface. Polling and the keep-last-on-error behavior mirror
// FleetView so the two pages agree on the fleet at all times.
const ROSTER_POLL_MS = 2000;

type Pivot = "machine" | "repo" | "agent" | "list";

const PIVOTS: ReadonlyArray<{ key: Pivot; label: string; kindLabel: string }> = [
  { key: "machine", label: "By machine", kindLabel: "Machine" },
  { key: "repo", label: "By repository", kindLabel: "Repository" },
  { key: "agent", label: "By agent", kindLabel: "Agent" },
  // The flat "Fleet list" pivot (issue #1212) absorbs the retired Fleet page: every session once, as
  // the same node card, no lane grouping (machines are already their own pivot).
  { key: "list", label: "Fleet list", kindLabel: "Session" },
];

// A group of sessions that share a team (SessionDto.groupId), rendered as a lead/worker cluster.
interface Team {
  groupId: string;
  sessions: SessionDto[];
}

// One column on the canvas for the active pivot: a header (the machine/repo/agent) plus its sessions,
// optionally sub-grouped by Director (machine pivot only) and by team.
interface Lane {
  key: string;
  kindLabel: string;
  title: string;
  subtitle: string;
  sessions: SessionDto[];
}

export function FleetMapView() {
  const navigate = useNavigate();
  const [sessions, setSessions] = useState<SessionDto[] | null>(null);
  const [machineErrors, setMachineErrors] = useState<MachineError[]>([]);
  const [directors, setDirectors] = useState<DirectorReachability[]>([]);
  const [lastError, setLastError] = useState<string | null>(null);
  const [pivot, setPivot] = useState<Pivot>("machine");
  // Title search (issue #1211): a case-insensitive substring filter over the session name. It hides
  // non-matches and NEVER reorders what remains (see the lanes memo below). Not persisted across
  // reloads.
  const [query, setQuery] = useState("");
  // The Wingman narration overlay is not implemented and is not planned, so its toggle is hidden from
  // the header (see below). Keep the value wired as a constant false so the narration code paths stay
  // intact for an easy future restore - flip this back to useState and restore the toggle to bring it
  // back.
  const wingman = false;

  const loadRoster = useCallback(async (signal?: AbortSignal) => {
    try {
      const env = await getSessionsEnvelope(signal);
      setSessions(env.sessions);
      setMachineErrors(env.machineErrors);
      setDirectors(env.directors);
      setLastError(null);
    } catch (err) {
      if (signal?.aborted === true) return;
      // Keep the last-known map on a transient failure; only surface the banner (FleetView parity).
      setLastError(gatewayErrorMessage(err));
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void loadRoster(controller.signal);
    const timer = window.setInterval(() => void loadRoster(controller.signal), ROSTER_POLL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [loadRoster]);

  const list = useMemo(() => sessions ?? [], [sessions]);
  // Build the lanes from the WHOLE fleet first, so lane order and each card's slot are fixed. The title
  // search then only removes non-matching cards from within those fixed lanes and drops lanes that go
  // empty - a matching card never moves and the lanes never reorder (issue #1211). The flat "list"
  // pivot does not use lanes.
  const allLanes = useMemo(() => (pivot === "list" ? [] : buildLanes(list, pivot)), [list, pivot]);
  const lanes = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (q.length === 0) return allLanes;
    return allLanes
      .map((lane) => ({ ...lane, sessions: lane.sessions.filter((s) => (s.name ?? "").toLowerCase().includes(q)) }))
      .filter((lane) => lane.sessions.length > 0);
  }, [allLanes, query]);
  // The flat "Fleet list" pivot (issue #1212): every session once, filtered by the same title search,
  // in a stable session-number order (the identity the owner reads), so matches never move.
  const flatSessions = useMemo(() => {
    const q = query.trim().toLowerCase();
    const filtered = q.length === 0 ? list : list.filter((s) => (s.name ?? "").toLowerCase().includes(q));
    return [...filtered].sort(flatSort);
  }, [list, query]);
  const matchCount = useMemo(
    () => (pivot === "list" ? flatSessions.length : lanes.reduce((n, lane) => n + lane.sessions.length, 0)),
    [pivot, flatSessions, lanes],
  );

  const machineCount = useMemo(() => {
    const set = new Set<string>();
    for (const s of list) set.add((s.machineName ?? "").toLowerCase());
    return set.size;
  }, [list]);
  const repoCount = useMemo(() => {
    const set = new Set<string>();
    for (const s of list) set.add(repoBasename(s.repoPath).toLowerCase());
    return set.size;
  }, [list]);
  const redCount = list.filter((s) => effectiveColor(s) === "red").length;
  const workingCount = list.filter((s) => effectiveColor(s) === "blue").length;

  return (
    <ReachabilityContext.Provider value={directors}>
    <div className="fmap">
      <header className="fmap-head">
        <h1 className="fmap-title">Fleet Map</h1>
        <span className="fmap-stats">
          <span>{list.length} session{list.length === 1 ? "" : "s"}</span>
          <span className="fmap-sep" aria-hidden="true" />
          <span>{machineCount} machine{machineCount === 1 ? "" : "s"}</span>
          <span className="fmap-sep" aria-hidden="true" />
          <span>{repoCount} repo{repoCount === 1 ? "" : "s"}</span>
          {workingCount > 0 && (
            <>
              <span className="fmap-sep" aria-hidden="true" />
              <span className="fmap-stat-blue">{workingCount} working</span>
            </>
          )}
          {redCount > 0 && (
            <>
              <span className="fmap-sep" aria-hidden="true" />
              <span className="fmap-stat-red">{redCount} needs you</span>
            </>
          )}
        </span>

        <div className="fmap-controls">
          <input
            type="search"
            className="fmap-search"
            placeholder="Search titles..."
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            aria-label="Search sessions by title"
          />
          <div className="fmap-pivot" role="group" aria-label="Group the fleet by">
            {PIVOTS.map((p) => (
              <button
                key={p.key}
                type="button"
                className={pivot === p.key ? "fmap-pivot-btn on" : "fmap-pivot-btn"}
                aria-pressed={pivot === p.key}
                onClick={() => setPivot(p.key)}
              >
                {p.label}
              </button>
            ))}
          </div>
          {/* The "Wingman narration" toggle is hidden for now: the narration overlay is not
              implemented and is not planned. Restore this <label> (and the useState above) to bring
              the feature back. */}
        </div>
      </header>

      {lastError !== null && <div className="fmap-error">{lastError}</div>}

      {machineErrors.length > 0 && (
        <div className="fmap-warn">
          {machineErrors.length} machine{machineErrors.length === 1 ? "" : "s"} unreachable on the last sweep:{" "}
          {machineErrors
            .map((m) => (m.machineName ?? "").trim())
            .filter((n) => n.length > 0)
            .join(", ") || "(unknown)"}
        </div>
      )}

      {sessions === null && lastError === null && <div className="fmap-empty">Loading the fleet...</div>}

      {sessions !== null && list.length === 0 && machineErrors.length === 0 && (
        <div className="fmap-empty">
          <p>No sessions are running anywhere on the fleet.</p>
          <p className="fmap-empty-sub">
            A node appears here the moment a Director starts a session.
          </p>
        </div>
      )}

      {sessions !== null &&
        list.length > 0 &&
        matchCount === 0 &&
        query.trim().length > 0 && (
          <div className="fmap-empty">
            <p>No sessions match &ldquo;{query.trim()}&rdquo;.</p>
            <p className="fmap-empty-sub">Clear the search to see the whole fleet.</p>
          </div>
        )}

      {pivot === "list"
        ? flatSessions.length > 0 && (
            <FleetList
              sessions={flatSessions}
              onOpen={(sid) => navigate(`/session/${encodeURIComponent(sid)}`)}
            />
          )
        : lanes.length > 0 && (
            <Canvas
              lanes={lanes}
              pivot={pivot}
              wingman={wingman}
              sessionCount={query.trim().length > 0 ? matchCount : list.length}
              machineCount={machineCount}
              onOpen={(sid) => navigate(`/session/${encodeURIComponent(sid)}`)}
            />
          )}

      <div className="fmap-legend" aria-hidden="true">
        <LegendDot color="blue" label="Working" />
        <LegendDot color="red" label="Needs you" />
        <LegendDot color="green" label="Idle" />
        <LegendDot color="yellow" label="Wingman reading" />
        <LegendDot color="orange" label="Transcribing" />
        <LegendDot color="supporting" label="Sub-agent" />
        <LegendDot color="grey" label="On hold" />
      </div>
    </div>
    </ReachabilityContext.Provider>
  );
}

function LegendDot({ color, label }: { color: string; label: string }) {
  return (
    <span className="fmap-legend-item">
      <span className="fmap-legend-dot" style={{ backgroundColor: dotColor(color) }} />
      {label}
    </span>
  );
}

interface CanvasProps {
  lanes: Lane[];
  pivot: Pivot;
  wingman: boolean;
  sessionCount: number;
  machineCount: number;
  onOpen: (sessionId: string) => void;
}

// The node canvas: a root node above a horizontal row of lane panels, joined by SVG elbow connectors
// measured from the live DOM (so the wiring tracks whatever width the panels lay out at). The lanes
// scroll horizontally inside their own container; the SVG is sized to the scrollable content so the
// wires stay attached when the row is wider than the pane.
function Canvas({ lanes, pivot, wingman, sessionCount, machineCount, onOpen }: CanvasProps) {
  const innerRef = useRef<HTMLDivElement | null>(null);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const headRefs = useRef<Map<string, HTMLDivElement>>(new Map());
  const [wires, setWires] = useState<string[]>([]);

  const laneKeys = lanes.map((l) => l.key).join("|");

  const drawWires = useCallback(() => {
    const inner = innerRef.current;
    const root = rootRef.current;
    if (inner === null || root === null) return;
    const base = inner.getBoundingClientRect();
    const r = root.getBoundingClientRect();
    const rx = r.left - base.left + r.width / 2;
    const ry = r.bottom - base.top;
    const next: string[] = [];
    for (const lane of lanes) {
      const head = headRefs.current.get(lane.key);
      if (head === undefined) continue;
      const hb = head.getBoundingClientRect();
      const hx = hb.left - base.left + hb.width / 2;
      const hy = hb.top - base.top;
      const midY = ry + (hy - ry) * 0.5;
      next.push(`M ${rx} ${ry} V ${midY} H ${hx} V ${hy}`);
    }
    setWires((prev) => (prev.length === next.length && prev.every((p, i) => p === next[i]) ? prev : next));
  }, [lanes]);

  // Redraw after layout whenever the grouping, pivot, or narration (card heights) changes, and on any
  // resize of the canvas. useLayoutEffect so the wires are computed against the just-painted DOM.
  useLayoutEffect(() => {
    drawWires();
    const inner = innerRef.current;
    if (inner === null) return;
    const ro = new ResizeObserver(() => drawWires());
    ro.observe(inner);
    window.addEventListener("resize", drawWires);
    return () => {
      ro.disconnect();
      window.removeEventListener("resize", drawWires);
    };
  }, [drawWires, laneKeys, pivot, wingman]);

  return (
    <div className={wingman ? "fmap-canvas-scroll wm" : "fmap-canvas-scroll"}>
      <div className="fmap-canvas" ref={innerRef}>
        <svg className="fmap-wires" aria-hidden="true">
          {wires.map((d, i) => (
            <path key={i} d={d} />
          ))}
        </svg>

        <div className="fmap-canvas-inner">
          <div className="fmap-root" ref={rootRef}>
            <div className="fmap-root-k">Gateway</div>
            <div className="fmap-root-t">This fleet</div>
            <div className="fmap-root-s">
              {machineCount} machine{machineCount === 1 ? "" : "s"} &nbsp;/&nbsp; {sessionCount} session
              {sessionCount === 1 ? "" : "s"}
            </div>
          </div>

          <div className="fmap-lanes">
            {lanes.map((lane) => (
              <LanePanel
                key={lane.key}
                lane={lane}
                pivot={pivot}
                onOpen={onOpen}
                headRef={(el) => {
                  if (el === null) headRefs.current.delete(lane.key);
                  else headRefs.current.set(lane.key, el);
                }}
              />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

interface LanePanelProps {
  lane: Lane;
  pivot: Pivot;
  onOpen: (sessionId: string) => void;
  headRef: (el: HTMLDivElement | null) => void;
}

function LanePanel({ lane, pivot, onOpen, headRef }: LanePanelProps) {
  const agg = aggregateColors(lane.sessions);

  // Machine pivot: sub-group the lane's sessions by Director so the panel reads machine -> Director ->
  // session. Other pivots render a flat list (each card still tags its machine/Director).
  const directorGroups = pivot === "machine" ? groupByDirector(lane.sessions) : null;

  return (
    <section className="fmap-lane">
      <div className="fmap-lane-head" ref={headRef}>
        <span className="fmap-lane-k">{lane.kindLabel}</span>
        <span className="fmap-lane-t">{lane.title}</span>
        {lane.subtitle.length > 0 && <span className="fmap-lane-sub">{lane.subtitle}</span>}
        <span className="fmap-lane-agg">
          {agg.map((c) => (
            <span key={c} className="fmap-lane-aggdot" style={{ backgroundColor: dotColor(c) }} />
          ))}
        </span>
      </div>

      <div className="fmap-lane-body">
        {directorGroups !== null
          ? directorGroups.map((dg) => (
              <div key={dg.key} className="fmap-dgroup">
                <div className="fmap-subhead">{dg.label}</div>
                <LaneCards sessions={dg.sessions} pivot={pivot} onOpen={onOpen} />
              </div>
            ))
          : <LaneCards sessions={lane.sessions} pivot={pivot} onOpen={onOpen} />}
      </div>
    </section>
  );
}

// The flat "Fleet list" pivot body (issue #1212): every session as the same NodeCard, in a responsive
// grid, no lane grouping. Reuses the exact card the lane pivots render, so the number badge and the
// title search apply identically.
function FleetList({ sessions, onOpen }: { sessions: SessionDto[]; onOpen: (sid: string) => void }) {
  return (
    <div className="fmap-list">
      {sessions.map((s) => (
        <NodeCard key={s.sessionId ?? s.number} session={s} pivot="list" onOpen={onOpen} />
      ))}
    </div>
  );
}

function LaneCards({ sessions, pivot, onOpen }: { sessions: SessionDto[]; pivot: Pivot; onOpen: (sid: string) => void }) {
  const { teams, loose } = splitTeams(sessions);
  return (
    <>
      {teams.map((t) => (
        <div key={t.groupId} className="fmap-team">
          <div className="fmap-team-cap">
            <span className="fmap-team-tag">Team</span>
            {t.groupId}
          </div>
          {t.sessions.map((s) => (
            <NodeCard key={s.sessionId ?? s.number} session={s} pivot={pivot} onOpen={onOpen} />
          ))}
        </div>
      ))}
      {loose.map((s) => (
        <NodeCard key={s.sessionId ?? s.number} session={s} pivot={pivot} onOpen={onOpen} />
      ))}
    </>
  );
}

function NodeCard({ session: s, pivot, onOpen }: { session: SessionDto; pivot: Pivot; onOpen: (sid: string) => void }) {
  const directors = useContext(ReachabilityContext);
  const color = effectiveColor(s);
  const sid = s.sessionId ?? "";
  const unnamed = (s.name ?? "").trim().length === 0;
  const num = s.number;
  const hasNum = num !== null && num !== undefined && String(num).trim().length > 0;
  const role = (s.groupRole ?? "").toLowerCase();
  // Reachability of the owning Director (issue #1215): a Wobbly/Offline machine dims its cards in place.
  const reach = reachabilityFor(directors, s.directorId);
  const wobbly = reach?.state === REACHABILITY_WOBBLY;
  const offline = reach?.state === REACHABILITY_OFFLINE;
  const lastSeen = wobbly || offline ? reachabilityLastSeen(reach?.lastSeenAgeSeconds) : "";
  const cls =
    "fmap-card" +
    (color === "red" ? " needs" : "") +
    (role === "lead" ? " lead" : "") +
    (role === "worker" ? " worker" : "") +
    (wobbly ? " fmap-card-wobbly" : "") +
    (offline ? " fmap-card-offline" : "");
  const rail = (s.railLine ?? "").trim();

  // The card tags carry the two hierarchy coordinates NOT already implied by the lane the card sits in.
  const tags = cardTags(s, pivot);

  return (
    <article
      className={cls}
      tabIndex={0}
      role="button"
      title={sid.length > 0 ? "Open session" : undefined}
      onClick={() => sid.length > 0 && onOpen(sid)}
      onKeyDown={(e) => {
        if (sid.length > 0 && (e.key === "Enter" || e.key === " ")) {
          e.preventDefault();
          onOpen(sid);
        }
      }}
    >
      <div className="fmap-card-top">
        <span
          className={color === "blue" ? "fmap-dot working" : "fmap-dot"}
          style={{ backgroundColor: dotColor(color) }}
          title={s.lastStatusReason ?? undefined}
        />
        {hasNum && <span className="num-badge">{num}</span>}
        <span className={unnamed ? "fmap-card-name unnamed" : "fmap-card-name"}>
          {unnamed ? "(unnamed)" : s.name}
        </span>
        <span className="fmap-agent">{(s.agent ?? "").trim().length === 0 ? "?" : s.agent}</span>
      </div>

      {tags.length > 0 && (
        <div className="fmap-card-tags">
          {tags.map((t) => (
            <span key={t.k} className="fmap-card-tag">
              <span className="fmap-card-tag-k">{t.k}</span>
              {t.v}
            </span>
          ))}
        </div>
      )}

      <div className="fmap-card-state" style={{ color: dotColor(color) }}>
        {stateLabel(s)}
        <span className="fmap-card-idle">{relativeTime(s.lastActivityAt)}</span>
      </div>

      {lastSeen.length > 0 && (
        <div className="fmap-card-lastseen">{offline ? "Offline" : "Wobbly"} - {lastSeen}</div>
      )}

      {rail.length > 0 && (
        <div className="fmap-rail">
          <span className="fmap-rail-k">Wingman</span>
          {rail}
        </div>
      )}
    </article>
  );
}

// ---- grouping (pure) ----

function buildLanes(sessions: SessionDto[], pivot: Pivot): Lane[] {
  const byKey = new Map<string, { title: string; list: SessionDto[] }>();
  const keyOf = (s: SessionDto): { key: string; title: string } => {
    if (pivot === "machine") {
      const name = (s.machineName ?? "").trim();
      const title = name.length === 0 ? "(unknown machine)" : name;
      return { key: title.toLowerCase(), title };
    }
    if (pivot === "repo") {
      const title = repoBasename(s.repoPath);
      return { key: title.toLowerCase(), title };
    }
    const agent = (s.agent ?? "").trim();
    const title = agent.length === 0 ? "(unknown agent)" : agent;
    return { key: title.toLowerCase(), title };
  };

  for (const s of sessions) {
    const { key, title } = keyOf(s);
    let g = byKey.get(key);
    if (g === undefined) {
      g = { title, list: [] };
      byKey.set(key, g);
    }
    g.list.push(s);
  }

  const kindLabel = PIVOTS.find((p) => p.key === pivot)?.kindLabel ?? "";

  return [...byKey.entries()]
    .map(([key, g]) => ({
      key,
      kindLabel,
      title: g.title,
      subtitle: laneSubtitle(g.list, pivot),
      sessions: [...g.list].sort(sessionSort),
    }))
    .sort((a, b) => b.sessions.length - a.sessions.length || a.title.toLowerCase().localeCompare(b.title.toLowerCase()));
}

function laneSubtitle(sessions: SessionDto[], pivot: Pivot): string {
  const n = sessions.length;
  const sessionWord = `${n} session${n === 1 ? "" : "s"}`;
  if (pivot === "machine") {
    const directors = new Set(sessions.map((s) => (s.directorId ?? "").trim()).filter((x) => x.length > 0));
    const d = directors.size;
    return `${d} director${d === 1 ? "" : "s"} / ${sessionWord}`;
  }
  return sessionWord;
}

// Stable order within a lane: creation time, then session id, so a card never jumps when its color
// changes (matches the Fleet page ordering intent).
function sessionSort(a: SessionDto, b: SessionDto): number {
  const byCreated = String(a.createdAt ?? "").localeCompare(String(b.createdAt ?? ""));
  if (byCreated !== 0) return byCreated;
  return String(a.sessionId ?? "").localeCompare(String(b.sessionId ?? ""));
}

// Stable order for the flat "Fleet list" pivot: by session number ascending (the identity the owner
// reads), numbers before the unnumbered, then session id - so a card never jumps on a color change.
function flatSort(a: SessionDto, b: SessionDto): number {
  const na = Number(a.number);
  const nb = Number(b.number);
  const aHas = Number.isFinite(na);
  const bHas = Number.isFinite(nb);
  if (aHas && bHas && na !== nb) return na - nb;
  if (aHas !== bHas) return aHas ? -1 : 1;
  return String(a.sessionId ?? "").localeCompare(String(b.sessionId ?? ""));
}

function groupByDirector(sessions: SessionDto[]): Array<{ key: string; label: string; sessions: SessionDto[] }> {
  const byDir = new Map<string, SessionDto[]>();
  for (const s of sessions) {
    const key = (s.directorId ?? "").trim();
    const arr = byDir.get(key);
    if (arr === undefined) byDir.set(key, [s]);
    else arr.push(s);
  }
  return [...byDir.entries()]
    .map(([key, arr]) => ({
      key: key.length === 0 ? "(unknown)" : key,
      label: `Director ${key.length === 0 ? "(unknown)" : shortDir(key)}`,
      sessions: [...arr].sort(sessionSort),
    }))
    .sort((a, b) => a.key.localeCompare(b.key));
}

// A Director id shortened to its last segment (Directors are "<machine>-<n>" or a guid); keeps the
// sub-header compact without losing which Director it is.
function shortDir(directorId: string): string {
  const dash = directorId.lastIndexOf("-");
  const tail = dash >= 0 ? directorId.slice(dash + 1) : directorId;
  return tail.length > 8 ? tail.slice(0, 8) : tail;
}

// Split a lane's sessions into team clusters (2+ sharing a groupId) and loose singletons. A groupId
// held by a single session is not a team - it renders as a normal card.
function splitTeams(sessions: SessionDto[]): { teams: Team[]; loose: SessionDto[] } {
  const byGroup = new Map<string, SessionDto[]>();
  const loose: SessionDto[] = [];
  for (const s of sessions) {
    const g = (s.groupId ?? "").trim();
    if (g.length === 0) {
      loose.push(s);
      continue;
    }
    const arr = byGroup.get(g);
    if (arr === undefined) byGroup.set(g, [s]);
    else arr.push(s);
  }
  const teams: Team[] = [];
  for (const [groupId, arr] of byGroup.entries()) {
    if (arr.length >= 2) {
      // Lead first, then workers, each sub-sorted stably.
      teams.push({
        groupId,
        sessions: [...arr].sort((a, b) => roleRank(a) - roleRank(b) || sessionSort(a, b)),
      });
    } else {
      loose.push(arr[0]);
    }
  }
  loose.sort(sessionSort);
  return { teams, loose };
}

function roleRank(s: SessionDto): number {
  return (s.groupRole ?? "").toLowerCase() === "lead" ? 0 : 1;
}

// The two hierarchy coordinates to show on a card that the lane header does NOT already state.
function cardTags(s: SessionDto, pivot: Pivot): Array<{ k: string; v: string }> {
  const dir = (s.directorId ?? "").trim();
  const machine = (s.machineName ?? "").trim();
  if (pivot === "machine") {
    // Lane = machine, sub-grouped by Director; the missing coordinate is the repo.
    return [{ k: "repo", v: repoBasename(s.repoPath) }];
  }
  if (pivot === "repo") {
    // Lane = repo; show where it runs (machine + Director) and which agent.
    const out: Array<{ k: string; v: string }> = [];
    if (machine.length > 0) out.push({ k: machine, v: dir.length > 0 ? shortDir(dir) : "-" });
    return out;
  }
  // Lane = agent, or the flat Fleet list (no lane at all); show machine + repo.
  const out: Array<{ k: string; v: string }> = [];
  if (machine.length > 0) out.push({ k: machine, v: repoBasename(s.repoPath) });
  return out;
}

function aggregateColors(sessions: SessionDto[]): string[] {
  const priority = ["red", "orange", "yellow", "blue", "green", "supporting", "grey"];
  const present = new Set(sessions.map((s) => effectiveColor(s)));
  return priority.filter((c) => present.has(c));
}
