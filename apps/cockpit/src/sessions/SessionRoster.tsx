import { Link } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import {
  classify,
  contextLine,
  dotColor,
  effectiveColor,
  inBucket,
  inDesktopOrder,
} from "@devthrottle/client-core/sessions/ordering";
import { useNow, waitingLabel } from "@devthrottle/client-core/sessions/waiting";

// The fleet-wide session roster (issue #972) - the React port of the Blazor SessionRail. It lists
// EVERY session the Gateway roster aggregation (GET /sessions) returns, across every Director, with
// the one effective status color and the same ordering policy the mobile roster and the desktop rail
// share (client-core/sessions). Selecting a row drives the terminal pane and the reply bar (the row
// is a Link to /session/{sid}).
//
// Ordering rule (the session-rail ordering decision): manual "My order" is the DEFAULT - the stable
// desktop order (the owning Director's drag-to-reorder SortOrder), so rows hold their slot and never
// reshuffle on a color change. "Attention first" is an OPT-IN view that groups needs-you at the top
// and on-hold at the bottom; it is never applied automatically.

export type RosterView = "my-order" | "attention";

export interface SessionRosterProps {
  sessions: SessionDto[] | null;
  selectedId: string | undefined;
  view: RosterView;
  onView: (view: RosterView) => void;
  error: string | null;
  /** Open the "New session" dialog (issue #1023). */
  onNewSession: () => void;
}

export function SessionRoster({ sessions, selectedId, view, onView, error, onNewSession }: SessionRosterProps) {
  const total = sessions?.length ?? 0;

  return (
    <div className="roster-rail">
      <div className="roster-head">
        <span className="roster-title">Sessions</span>
        <span className="roster-count">{total}</span>
        {/* The only way to start a session from the desktop Cockpit (issue #1023). Opens the
            dedicated machine/repo picker dialog. */}
        <button type="button" className="roster-newbtn" onClick={onNewSession} title="Start a new session">
          + New session
        </button>
      </div>

      {/* The ordering toggle. "My order" is pressed by default; "Attention first" is the opt-in view. */}
      <div className="roster-view" role="group" aria-label="Roster ordering">
        <button
          type="button"
          className={`roster-view-btn ${view === "my-order" ? "on" : ""}`}
          aria-pressed={view === "my-order"}
          onClick={() => onView("my-order")}
        >
          My order
        </button>
        <button
          type="button"
          className={`roster-view-btn ${view === "attention" ? "on" : ""}`}
          aria-pressed={view === "attention"}
          onClick={() => onView("attention")}
        >
          Attention first
        </button>
      </div>

      {error !== null && (
        <div className="roster-error" role="alert">
          {sessions !== null ? "Roster stale - showing last-known sessions" : error}
        </div>
      )}

      {sessions === null && error === null && <div className="roster-empty">Loading sessions...</div>}

      {sessions !== null && total === 0 && (
        <div className="roster-empty">No sessions. The Gateway returned an empty roster.</div>
      )}

      {sessions !== null && total > 0 && view === "my-order" && (
        <ul className="roster-list">
          {inDesktopOrder(sessions).map((s) => (
            <RosterRow key={s.sessionId} session={s} selectedId={selectedId} />
          ))}
        </ul>
      )}

      {sessions !== null && total > 0 && view === "attention" && (
        <AttentionGroups sessions={sessions} selectedId={selectedId} />
      )}
    </div>
  );
}

// Opt-in attention view: needs-you first, then active, then on-hold. Each bucket keeps its members in
// desktop order (inBucket), so a session holds its slot within its bucket and does not reshuffle.
function AttentionGroups({ sessions, selectedId }: { sessions: SessionDto[]; selectedId: string | undefined }) {
  const needs = inBucket(sessions, "needsYou");
  const active = inBucket(sessions, "active");
  const held = inBucket(sessions, "onHold");
  return (
    <>
      {needs.length > 0 && (
        <Bucket title="Needs you" tone="needs" count={needs.length}>
          {needs.map((s) => (
            <RosterRow key={`needs-${s.sessionId}`} session={s} selectedId={selectedId} />
          ))}
        </Bucket>
      )}
      {active.length > 0 && (
        <Bucket title="Active" count={active.length}>
          {active.map((s) => (
            <RosterRow key={`active-${s.sessionId}`} session={s} selectedId={selectedId} />
          ))}
        </Bucket>
      )}
      {held.length > 0 && (
        <Bucket title="On hold" tone="hold" count={held.length}>
          {held.map((s) => (
            <RosterRow key={`hold-${s.sessionId}`} session={s} selectedId={selectedId} />
          ))}
        </Bucket>
      )}
    </>
  );
}

function Bucket({
  title,
  count,
  tone,
  children,
}: {
  title: string;
  count: number;
  tone?: "needs" | "hold";
  children: React.ReactNode;
}) {
  return (
    <div className="roster-bucket">
      <div className={`roster-bucket-head ${tone ?? ""}`}>
        {title} <span className="roster-bucket-count">{count}</span>
      </div>
      <ul className="roster-list">{children}</ul>
    </div>
  );
}

function RosterRow({ session, selectedId }: { session: SessionDto; selectedId: string | undefined }) {
  const sid = session.sessionId ?? "";
  const color = effectiveColor(session);
  const selected = sid === selectedId;
  const attention = classify(session) === "needsYou";
  const name = session.name && session.name.trim().length > 0 ? session.name : session.repoPath || "(unnamed session)";
  const num = session.number;
  const hasNum = num !== null && num !== undefined && String(num).trim().length > 0;
  const machine = (session.machineName ?? "").trim();
  return (
    <li>
      <Link
        className={`roster-row${selected ? " roster-row-selected" : ""}${attention ? " roster-row-attention" : ""}`}
        style={{ borderLeftColor: dotColor(color) }}
        to={`/session/${encodeURIComponent(sid)}`}
        title={session.lastStatusReason ?? undefined}
      >
        <span className="roster-dot" style={{ backgroundColor: dotColor(color) }} aria-hidden="true" />
        <span className="roster-body">
          <span className="roster-name">
            {hasNum && <span className="num-badge">{num}</span>}
            <span className="roster-name-text">{name}</span>
          </span>
          <span className="roster-meta">
            <span className="roster-state">{contextLine(session)}</span>
            {session.onHold && <span className="roster-tag">hold</span>}
            {session.voiceMode && <span className="roster-tag voice">voice</span>}
            {machine && <span className="roster-machine" title={session.directorId ?? undefined}>{machine}</span>}
            {attention && session.needsYouSince && <WaitingTime since={String(session.needsYouSince)} />}
          </span>
          {attention && session.railLine && session.railLine.trim().length > 0 && (
            <span className="roster-railline">{session.railLine}</span>
          )}
        </span>
      </Link>
    </li>
  );
}

// The live "waiting <dur>" label for a needs-you row, ticking each second from the held
// needsYouSince (no roster refetch). Only mounted for needs-you rows, so the per-second re-render
// never touches active/other rows.
function WaitingTime({ since }: { since: string }) {
  const now = useNow(1000);
  const label = waitingLabel(since, now);
  if (label.length === 0) return null;
  return <span className="roster-waiting">{label}</span>;
}
