import { useCallback, useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { type SessionDto } from "@devthrottle/client-core/api/client";
import { dotColor, effectiveColor, stateLabel } from "@devthrottle/client-core/sessions/ordering";
import { useSharedRoster } from "@devthrottle/client-core/fleet/rosterStore";
import { getMissionNotes, setMissionNote } from "@devthrottle/client-core/missions/missionNotes";
import { repoBasename, relativeTime } from "../fleet/format";
import { groupByMission, type MissionGroup } from "./missionGrouping";

// The Missions page (issue #1405): the same live fleet the Fleet Map draws, seen the way the owner
// actually thinks about the work - grouped into MISSIONS rather than machines or repos. A mission is
// derived purely from the session name ("<Mission> - <Role>") by the pure module missionGrouping.ts;
// this page only lays the result out. Sessions that are not a mission member fall into a Standalone
// group shown last.
//
// It reads the ONE shared fleet roster store (issue #1239) - the same GET /sessions envelope the Fleet
// Map, Sessions, and Directors pages read from a single poll loop - never a Director address, and reuses
// the ONE shared effective-color + state-label rule so a status dot here matches every other Cockpit
// surface. Clicking a session row opens that session (/session/:id) - the "linking".
//
// The WHY (Phase 1b): every card carries its mission's WHY, front and center, from the durable + shared
// Gateway mission-notes store (getMissionNotes / setMissionNote in client-core) - keyed by the SAME
// normalized mission key groupByMission produces, so every client and the future chat/API read the same
// WHY. A mission with no WHY shows a loud flag (the mission's founding rule), never a silent blank; the
// flag is the button that adds one, and the WHY is editable inline. Roster parsing stays in
// missionGrouping.ts; the only writes here go through the WHY store.

// The loud flag shown when a mission has no WHY set - a mission with no stated WHY is a red flag the
// screen makes obvious (the mission's founding rule), never a silent blank. It is also the button that
// opens the inline editor to add one.
const NO_WHY_TEXT = "No why set - add one";

export function MissionsView() {
  const navigate = useNavigate();
  // Sessions, the unreachable-machine list, and the keep-last error banner all come from the ONE shared
  // roster store; no poll of its own.
  const { sessions, machineErrors, error: lastError } = useSharedRoster();

  // The mission WHYs, keyed by the normalized mission key (the same key groupByMission produces). Read
  // once on mount from the durable, shared Gateway store; refreshed in place after an inline edit. A
  // failed read is non-fatal - the page still renders the fleet, every card just shows its flag.
  const [whyByKey, setWhyByKey] = useState<Map<string, string>>(new Map());

  const refreshNotes = useCallback(() => {
    getMissionNotes()
      .then((notes) => setWhyByKey(new Map(notes.map((n) => [n.key, n.why]))))
      .catch(() => {
        /* non-fatal: the WHYs are unavailable this load; the cards fall back to the flag */
      });
  }, []);

  useEffect(() => {
    refreshNotes();
  }, [refreshNotes]);

  // Save (or clear) one mission's WHY through the shared store, then reflect it locally. The store
  // treats an empty why as "unset", so clearing returns the card to its flag.
  const saveWhy = useCallback(async (missionName: string, why: string) => {
    const note = await setMissionNote(missionName, why);
    setWhyByKey((prev) => {
      const next = new Map(prev);
      const key = missionName.trim().toLowerCase();
      if (note === null) next.delete(key);
      else next.set(note.key, note.why);
      return next;
    });
  }, []);

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
            <MissionCard
              key={m.key}
              mission={m}
              why={whyByKey.get(m.key) ?? ""}
              onSaveWhy={saveWhy}
              onOpen={openSession}
            />
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
  why: string;
  onSaveWhy: (missionName: string, why: string) => Promise<void>;
  onOpen: (sessionId: string | null | undefined) => void;
}

function MissionCard({ mission, why, onSaveWhy, onOpen }: MissionCardProps) {
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

      {/* The WHY slot (issue #1405): first-class on every card, front and center. The WHY comes from the
          durable, shared Gateway store and is editable inline; a missing WHY shows the loud flag. */}
      <WhySlot missionName={mission.name} why={why} onSave={onSaveWhy} />

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

interface WhySlotProps {
  missionName: string;
  why: string;
  onSave: (missionName: string, why: string) => Promise<void>;
}

// The WHY slot on a mission card: shows the mission's WHY front and center, or a loud flag when none is
// set. Either the flag or an "Edit" affordance opens an inline editor; saving writes through the shared
// store (an empty value clears the WHY back to the flag). Save errors are shown loudly and keep the
// editor open rather than silently dropping the edit.
function WhySlot({ missionName, why, onSave }: WhySlotProps) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(why);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const beginEdit = () => {
    setDraft(why);
    setError(null);
    setEditing(true);
  };

  const cancel = () => {
    setEditing(false);
    setError(null);
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      await onSave(missionName, draft);
      setEditing(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not save the why");
    } finally {
      setSaving(false);
    }
  };

  if (editing) {
    return (
      <div className="msn-why msn-why-editing">
        <span className="msn-why-k">Why</span>
        <div className="msn-why-edit">
          <textarea
            className="msn-why-input"
            value={draft}
            autoFocus
            rows={2}
            placeholder="Why are we on this mission?"
            disabled={saving}
            onChange={(e) => setDraft(e.target.value)}
          />
          <div className="msn-why-actions">
            <button type="button" className="msn-why-save" disabled={saving} onClick={save}>
              {saving ? "Saving..." : "Save"}
            </button>
            <button type="button" className="msn-why-cancel" disabled={saving} onClick={cancel}>
              Cancel
            </button>
          </div>
          {error !== null && <div className="msn-why-error">{error}</div>}
        </div>
      </div>
    );
  }

  const hasWhy = why.trim().length > 0;
  return (
    <div className={hasWhy ? "msn-why" : "msn-why msn-why-empty"}>
      <span className="msn-why-k">Why</span>
      {hasWhy ? (
        <>
          <span className="msn-why-text">{why}</span>
          <button type="button" className="msn-why-editbtn" onClick={beginEdit}>
            Edit
          </button>
        </>
      ) : (
        <button type="button" className="msn-why-flag" onClick={beginEdit}>
          {NO_WHY_TEXT}
        </button>
      )}
    </div>
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
