import { useState } from "react";
import { Link } from "react-router-dom";
import { setVoiceModeAllSessions, type SessionDto } from "@devthrottle/client-core/api/client";
import {
  classify,
  contextLine,
  deletionReason,
  dotColor,
  effectiveColor,
  groupByDirector,
  inBucket,
  pendingDeletion,
  snoozeCountdown,
  snoozeExpired,
} from "@devthrottle/client-core/sessions/ordering";
import { machinePortLabel } from "@devthrottle/client-core/fleet/directorEndpoint";
import { useNow, waitingLabel } from "@devthrottle/client-core/sessions/waiting";
import {
  reachabilityFor,
  reachabilityLastSeen,
  REACHABILITY_OFFLINE,
  REACHABILITY_WOBBLY,
  type DirectorReachability,
} from "@devthrottle/client-core/fleet/fleetClient";
import { SessionMenu } from "./SessionMenu";

// The fleet-wide session roster (issue #972) - the React port of the Blazor SessionRail. It lists
// EVERY session the Gateway roster aggregation (GET /sessions) returns, across every Director, with
// the one effective status color and the same ordering policy the mobile roster and the desktop rail
// share (client-core/sessions). Selecting a row drives the terminal pane and the reply bar (the row
// is a Link to /session/{sid}).
//
// Ordering rule (the session-rail ordering decision): manual "My order" is the DEFAULT - the stable
// desktop order (the owning Director's drag-to-reorder SortOrder), so rows hold their slot and never
// reshuffle on a color change. In "My order" the sessions are grouped by their owning cc-director under
// a "computer:port" header (the port tells apart several Directors on one machine), and each session's
// facts get their own line so nothing truncates. "Attention first" is an OPT-IN view that groups
// needs-you at the top and on-hold at the bottom, across every machine; it is never applied automatically.

export type RosterView = "my-order" | "attention";

export interface SessionRosterProps {
  sessions: SessionDto[] | null;
  /** Per-Director reachability for the Online / Wobbly / Offline rendering (issue #1215). */
  directors: DirectorReachability[];
  /** directorId -> Control API port, for the "computer:port" group headers. Empty until GET /directors
   *  lands, or for a Director whose endpoint carries no port - the header then shows the bare machine. */
  portByDirector: Map<string, string>;
  selectedId: string | undefined;
  view: RosterView;
  onView: (view: RosterView) => void;
  error: string | null;
  /** Open the "New session" dialog (issue #1023). */
  onNewSession: () => void;
}

export function SessionRoster({ sessions, directors, portByDirector, selectedId, view, onView, error, onNewSession }: SessionRosterProps) {
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

      {/* The fleet-wide voice switch (issue #1765): one button turns voice mode on for every session,
          or off again, so a person leaving their desk can put the whole fleet on voice and take it back
          off later without touching each session. It reads the roster to pick its own direction. */}
      {sessions !== null && total > 0 && <VoiceAllButton sessions={sessions} />}

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
        <>
          {groupByDirector(sessions, portByDirector).map((group) => (
            <div className="roster-group" key={group.directorId || "(no-director)"}>
              <div className="roster-group-head">
                <span className="roster-group-name">
                  {machinePortLabel(group.machineName, group.port) || "(unknown director)"}
                </span>
              </div>
              <ul className="roster-list">
                {group.sessions.map((s) => (
                  <RosterRow
                    key={s.sessionId}
                    session={s}
                    directors={directors}
                    portByDirector={portByDirector}
                    showMachine={false}
                    selectedId={selectedId}
                  />
                ))}
              </ul>
            </div>
          ))}
        </>
      )}

      {sessions !== null && total > 0 && view === "attention" && (
        <AttentionGroups
          sessions={sessions}
          directors={directors}
          portByDirector={portByDirector}
          selectedId={selectedId}
        />
      )}
    </div>
  );
}

// The fleet-wide voice switch shown under the roster ordering toggle (issue #1765). One control for both
// directions: while no session is a voice session it offers "Turn on voice for all N sessions"; the moment
// any session is on, it offers "Turn voice off for all sessions". It calls the Gateway's single fan-out
// endpoint, which walks the roster itself, then shows a plain summary of what changed and what was skipped
// (a session whose owning computer is offline is passed over, never failing the batch). The next roster
// poll repaints the "voice" tags and the button flips to its opposite direction.
function VoiceAllButton({ sessions }: { sessions: SessionDto[] }) {
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const anyOn = sessions.some((s) => Boolean(s.voiceMode));
  const enable = !anyOn;
  const count = sessions.length;

  const onClick = async () => {
    setBusy(true);
    setError(null);
    setNote(null);
    try {
      const result = await setVoiceModeAllSessions(enable);
      const changedLabel = `${result.changed} ${result.changed === 1 ? "session" : "sessions"} ${enable ? "on" : "off"}`;
      setNote(result.skipped > 0 ? `${changedLabel}, ${result.skipped} skipped (computer offline)` : changedLabel);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not change voice mode for all sessions");
    } finally {
      setBusy(false);
    }
  };

  const label = enable ? `Turn on voice for all ${count}` : "Turn voice off for all";
  const busyLabel = enable ? "Turning voice on..." : "Turning voice off...";

  return (
    <div className="roster-voice-all">
      <button
        type="button"
        className={`roster-voice-all-btn${enable ? "" : " off"}`}
        onClick={() => void onClick()}
        disabled={busy}
        title="Turn voice mode on or off for every session at once"
      >
        {busy ? busyLabel : label}
      </button>
      {note && <span className="roster-voice-all-note" role="status">{note}</span>}
      {error && <span className="roster-voice-all-error" role="alert">{error}</span>}
    </div>
  );
}

// Opt-in attention view: needs-you first, then active, then on-hold. Each bucket keeps its members in
// desktop order (inBucket), so a session holds its slot within its bucket and does not reshuffle. These
// buckets mix machines, so - unlike the grouped "My order" view - each card still shows its own
// "computer:port" line so you can see which cc-director a needs-you session lives on.
function AttentionGroups({
  sessions,
  directors,
  portByDirector,
  selectedId,
}: {
  sessions: SessionDto[];
  directors: DirectorReachability[];
  portByDirector: Map<string, string>;
  selectedId: string | undefined;
}) {
  const needs = inBucket(sessions, "needsYou");
  const active = inBucket(sessions, "active");
  const held = inBucket(sessions, "onHold");
  return (
    <>
      {needs.length > 0 && (
        <Bucket title="Needs you" tone="needs" count={needs.length}>
          {needs.map((s) => (
            <RosterRow key={`needs-${s.sessionId}`} session={s} directors={directors} portByDirector={portByDirector} showMachine selectedId={selectedId} />
          ))}
        </Bucket>
      )}
      {active.length > 0 && (
        <Bucket title="Active" count={active.length}>
          {active.map((s) => (
            <RosterRow key={`active-${s.sessionId}`} session={s} directors={directors} portByDirector={portByDirector} showMachine selectedId={selectedId} />
          ))}
        </Bucket>
      )}
      {held.length > 0 && (
        <Bucket title="Snoozed" tone="hold" count={held.length}>
          {held.map((s) => (
            <RosterRow key={`hold-${s.sessionId}`} session={s} directors={directors} portByDirector={portByDirector} showMachine selectedId={selectedId} />
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

function RosterRow({
  session,
  directors,
  portByDirector,
  showMachine,
  selectedId,
}: {
  session: SessionDto;
  directors: DirectorReachability[];
  portByDirector: Map<string, string>;
  /** Show the "computer:port" line on the card. False in "My order" (the group header carries it); true
   *  in the cross-machine Attention view, where each card needs to say which cc-director it lives on. */
  showMachine: boolean;
  selectedId: string | undefined;
}) {
  const sid = session.sessionId ?? "";
  const color = effectiveColor(session);
  const selected = sid === selectedId;
  const attention = classify(session) === "needsYou";
  const name = session.name && session.name.trim().length > 0 ? session.name : session.repoPath || "(unnamed session)";
  const num = session.number;
  const hasNum = num !== null && num !== undefined && String(num).trim().length > 0;
  const directorId = (session.directorId ?? "").trim();
  const machineLine = showMachine
    ? machinePortLabel((session.machineName ?? "").trim(), portByDirector.get(directorId) ?? "")
    : "";
  // The owning Director's reachability (issue #1215): a Wobbly/Offline Director dims its sessions in
  // place and shows a "last seen" age; an Online (or unknown) Director renders normally.
  const reach = reachabilityFor(directors, session.directorId);
  const wobbly = reach?.state === REACHABILITY_WOBBLY;
  const offline = reach?.state === REACHABILITY_OFFLINE;
  const lastSeen = wobbly || offline ? reachabilityLastSeen(reach?.lastSeenAgeSeconds) : "";
  // The hold time ("wakes in 3h 48m") and the winding-down flag are the Gateway's fold, read the same way
  // the desktop rail reads them - never the raw onHold sensor, which can drift from the fold (a Held
  // session that starts working is blue, and must not still read "snoozed"). The snooze LABEL already
  // rides on contextLine (the stamped stateLabel); this tag adds the countdown beside it.
  const holdCountdown = snoozeCountdown(session);
  const windingDown = pendingDeletion(session);
  // The tag row (voice / hold-time / snooze-ended / winding-down / last-seen / waiting) renders only when
  // it has something to say, so a plain working session stays a compact two lines (name + state).
  const hasTags =
    holdCountdown !== null ||
    snoozeExpired(session) ||
    windingDown ||
    !!session.voiceMode ||
    lastSeen.length > 0 ||
    (attention && !!session.needsYouSince);
  return (
    <li className="roster-li">
      <Link
        className={`roster-row${selected ? " roster-row-selected" : ""}${attention ? " roster-row-attention" : ""}${wobbly ? " roster-row-wobbly" : ""}${offline ? " roster-row-offline" : ""}`}
        style={{ borderLeftColor: dotColor(color) }}
        to={`/session/${encodeURIComponent(sid)}`}
        title={session.lastStatusReason ?? undefined}
      >
        <span className="roster-dot" style={{ backgroundColor: dotColor(color) }} aria-hidden="true" />
        <span className="roster-body">
          {/* Line 1: the session number badge + the full name (wraps freely, no clamp). */}
          <span className="roster-name">
            {hasNum && <span className="num-badge">{num}</span>}
            <span className="roster-name-text">{name}</span>
          </span>
          {/* Line 2: the Gateway-stamped status, on its own line so it is never squeezed to "Wor...". */}
          <span className="roster-state">{contextLine(session)}</span>
          {/* Line 3 (Attention view only): which cc-director this session lives on. */}
          {machineLine && (
            <span className="roster-machine" title={session.directorId ?? undefined}>
              {machineLine}
            </span>
          )}
          {/* Line 4 (only when there is something to show): the tags and the live waiting timer. */}
          {hasTags && (
            <span className="roster-tags">
              {session.voiceMode && <span className="roster-tag voice">voice</span>}
              {windingDown && (
                <span className="roster-tag winding-down" title={deletionReason(session) ?? "Marked for deletion"}>
                  winding down
                </span>
              )}
              {holdCountdown !== null && <HoldCountdown session={session} />}
              {snoozeExpired(session) && <span className="roster-tag snooze-ended">Snooze ended</span>}
              {lastSeen && <span className="roster-lastseen">{lastSeen}</span>}
              {attention && session.needsYouSince && <WaitingTime since={String(session.needsYouSince)} />}
            </span>
          )}
          {/* The attention narration line, when present. */}
          {attention && session.railLine && session.railLine.trim().length > 0 && (
            <span className="roster-railline">{session.railLine}</span>
          )}
        </span>
      </Link>
      {/* The same session menu as the session page (issue #1214), pinned to the card's top-right. It
          sits OUTSIDE the Link so opening the menu never navigates into the session. */}
      <SessionMenu session={session} variant="rail" />
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

// The live "wakes in <dur>" hold time for a snoozed row, ticking each second from the Gateway-owned
// snooze clock (no roster refetch). Only mounted for snoozed rows that carry a clock, so the per-second
// re-render never touches other rows - the same pattern as WaitingTime.
function HoldCountdown({ session }: { session: SessionDto }) {
  const now = useNow(1000);
  const label = snoozeCountdown(session, now);
  if (label === null) return null;
  return <span className="roster-tag hold-time">{label}</span>;
}
