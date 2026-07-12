import { useMemo } from "react";
import { useNavigate } from "react-router-dom";
import { type SessionDto } from "@devthrottle/client-core/api/client";
import { dotColor, effectiveColor, stateLabel } from "@devthrottle/client-core/sessions/ordering";
import { useSharedRoster } from "@devthrottle/client-core/fleet/rosterStore";
import { repoBasename, relativeTime } from "../fleet/format";
import { groupByMission, type MissionGroup } from "./missionGrouping";

// The Missions page (issue #1405, Phase 1a): the same live fleet the Fleet Map draws, seen the way the
// owner actually thinks about the work - grouped into MISSIONS rather than machines or repos. A mission
// is derived purely from the session name ("<Mission> - <Role>") by the pure module missionGrouping.ts;
// this page only lays the result out. Sessions that are not a mission member fall into a Standalone
// group shown last.
//
// It reads the ONE shared fleet roster store (issue #1239) - the same GET /sessions envelope the Fleet
// Map, Sessions, and Directors pages read from a single poll loop - never a Director address, and reuses
// the ONE shared effective-color + state-label rule so a status dot here matches every other Cockpit
// surface. Clicking a session row opens that session (/session/:id) - the "linking" this phase delivers.
//
// Phase 1a shows a WHY slot on every card with a loud "No why set" flag; Phase 1b wires a durable,
// shared store behind it. Nothing here writes; nothing here parses (that is missionGrouping.ts).

// The Phase 1a placeholder for a mission's WHY, shown until Phase 1b wires the durable store. It is a
// deliberately loud flag - a mission with no stated WHY is a red flag the screen makes obvious (the
// mission's founding rule), never a silent blank.
const NO_WHY_TEXT = "No why set - add one";

export function MissionsView() {
  const navigate = useNavigate();
  // Sessions, the unreachable-machine list, and the keep-last error banner all come from the ONE shared
  // roster store; no poll of its own.
  const { sessions, machineErrors, error: lastError } = useSharedRoster();

  const list = useMemo(() => sessions ?? [], [sessions]);
  const grouped = useMemo(() => groupByMission(list), [list]);

  // Fleet-wide summary counts, read from the same shared color rule the cards use.
  const missionCount = grouped.missions.length;
  const standaloneCount = grouped.standalone.length;
  const redCount = list.filter((s) => effectiveColor(s) === "red").length;
  const workingCount = list.filter((s) => effectiveColor(s) === "blue").length;

  const openSession = (sid: string | null | undefined) => {
    const id = (sid ?? "").trim();
    if (id.length > 0) navigate(`/session/${encodeURIComponent(id)}`);
  };

  return (
    <div className="msn">
      <header className="msn-head">
        <h1 className="msn-title">Missions</h1>
        <span className="msn-stats">
          <span>
            {missionCount} mission{missionCount === 1 ? "" : "s"}
          </span>
          {workingCount > 0 && (
            <>
              <span className="msn-sep" aria-hidden="true" />
              <span className="msn-stat-blue">{workingCount} working</span>
            </>
          )}
          {redCount > 0 && (
            <>
              <span className="msn-sep" aria-hidden="true" />
              <span className="msn-stat-red">{redCount} needs you</span>
            </>
          )}
          {standaloneCount > 0 && (
            <>
              <span className="msn-sep" aria-hidden="true" />
              <span>
                {standaloneCount} standalone
              </span>
            </>
          )}
        </span>
      </header>

      {lastError !== null && <div className="msn-error">{lastError}</div>}

      {machineErrors.length > 0 && (
        <div className="msn-warn">
          {machineErrors.length} machine{machineErrors.length === 1 ? "" : "s"} unreachable on the last
          sweep:{" "}
          {machineErrors
            .map((m) => (m.machineName ?? "").trim())
            .filter((n) => n.length > 0)
            .join(", ") || "(unknown)"}
        </div>
      )}

      {sessions === null && lastError === null && <div className="msn-empty">Loading the fleet...</div>}

      {sessions !== null && list.length === 0 && machineErrors.length === 0 && (
        <div className="msn-empty">
          <p>No sessions are running anywhere on the fleet.</p>
          <p className="msn-empty-sub">A mission appears here the moment its sessions start.</p>
        </div>
      )}

      {list.length > 0 && (
        <div className="msn-list">
          {grouped.missions.map((m) => (
            <MissionCard key={m.key} mission={m} onOpen={openSession} />
          ))}

          {grouped.standalone.length > 0 && (
            <>
              <div className="msn-group-label">Standalone</div>
              <div className="msn-card msn-card-standalone">
                <div className="msn-sessions">
                  {grouped.standalone.map((s) => (
                    <SessionRow
                      key={s.sessionId ?? s.number}
                      session={s}
                      label={(s.name ?? "").trim().length === 0 ? "(unnamed)" : (s.name as string)}
                      onOpenSession={openSession}
                    />
                  ))}
                </div>
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}

interface MissionCardProps {
  mission: MissionGroup;
  onOpen: (sessionId: string | null | undefined) => void;
}

function MissionCard({ mission, onOpen }: MissionCardProps) {
  const sessions = mission.members.map((m) => m.session);

  // The card's accent (left border) and status pill follow the same color rule as everything else: red
  // if any session needs you, else blue if any is working, else a neutral idle.
  const needs = sessions.filter((s) => effectiveColor(s) === "red").length;
  const working = sessions.filter((s) => effectiveColor(s) === "blue").length;
  const accent = needs > 0 ? "red" : working > 0 ? "blue" : "grey";

  // The repositories this mission spans - one label when they agree, a count when they do not.
  const repos = useMemo(() => {
    const set = new Set<string>();
    for (const s of sessions) set.add(repoBasename(s.repoPath));
    return [...set];
  }, [sessions]);
  const repoLabel = repos.length === 1 ? repos[0] : `${repos.length} repos`;

  const count = mission.members.length;

  return (
    <section className="msn-card">
      <div className="msn-card-head" style={{ borderLeftColor: dotColor(accent) }}>
        <div className="msn-card-title">
          <div className="msn-name">{mission.name}</div>
          <div className="msn-meta">
            <span className="msn-repo">{repoLabel}</span>
            <span>
              {count} session{count === 1 ? "" : "s"}
            </span>
          </div>
        </div>
        <MissionPill needs={needs} working={working} />
      </div>

      {/* The WHY slot (issue #1405): first-class on every card from Phase 1a, shown as a loud flag until
          Phase 1b wires the durable store. */}
      <div className="msn-why msn-why-empty">
        <span className="msn-why-k">Why</span>
        <span className="msn-why-flag">{NO_WHY_TEXT}</span>
      </div>

      <div className="msn-sessions">
        {mission.members.map((m) => (
          <SessionRow
            key={m.session.sessionId ?? m.session.number}
            session={m.session}
            label={m.role}
            onOpenSession={onOpen}
          />
        ))}
      </div>
    </section>
  );
}

function MissionPill({ needs, working }: { needs: number; working: number }) {
  if (needs > 0) {
    return (
      <span className="msn-pill msn-pill-needs">
        <span className="msn-pdot" style={{ backgroundColor: dotColor("red") }} />
        {needs} needs you
      </span>
    );
  }
  if (working > 0) {
    return (
      <span className="msn-pill msn-pill-work">
        <span className="msn-pdot" style={{ backgroundColor: dotColor("blue") }} />
        working
      </span>
    );
  }
  return (
    <span className="msn-pill msn-pill-idle">
      <span className="msn-pdot" style={{ backgroundColor: dotColor("grey") }} />
      idle
    </span>
  );
}

interface SessionRowProps {
  session: SessionDto;
  // The primary label for the row: the parsed role inside a mission, or the session name in Standalone.
  label: string;
  onOpenSession: (sessionId: string | null | undefined) => void;
}

// One clickable session row: number badge, primary label, a short context line, machine chip, and the
// live state label - all colored by the ONE shared effective-color rule so the row agrees with the rail
// and the Fleet Map. Clicking (or Enter/Space) opens the session.
function SessionRow({ session: s, label, onOpenSession }: SessionRowProps) {
  const color = effectiveColor(s);
  const hex = dotColor(color);
  const sid = s.sessionId ?? "";
  const num = s.number;
  const hasNum = num !== null && num !== undefined && String(num).trim().length > 0;
  const machine = (s.machineName ?? "").trim();
  const context = (s.lastStatusReason ?? "").trim();

  return (
    <div
      className="msn-srow"
      tabIndex={0}
      role="button"
      title={sid.length > 0 ? "Open session" : undefined}
      onClick={() => onOpenSession(sid)}
      onKeyDown={(e) => {
        if (sid.length > 0 && (e.key === "Enter" || e.key === " ")) {
          e.preventDefault();
          onOpenSession(sid);
        }
      }}
    >
      <span className="msn-lbar" style={{ backgroundColor: hex }} />
      {hasNum && <span className="num-badge">{num}</span>}
      <div className="msn-srow-grow">
        <div className="msn-role">{label}</div>
        {context.length > 0 && <div className="msn-rmeta">{context}</div>}
      </div>
      {machine.length > 0 && <span className="msn-machine">{machine}</span>}
      <span className="msn-state" style={{ color: hex }}>
        {stateLabel(s)}
        <span className="msn-idle">{relativeTime(s.lastActivityAt)}</span>
      </span>
    </div>
  );
}
