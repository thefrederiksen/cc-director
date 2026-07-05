import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { dotColor, effectiveColor } from "@devthrottle/client-core/sessions/ordering";
import {
  dismissInterruptedJournal,
  dismissInterruptedSession,
  getInterrupted,
  getSessionsEnvelope,
  renameSession,
  restoreInterrupted,
  type InterruptedSession,
  type MachineError,
} from "@devthrottle/client-core/fleet/fleetClient";
import { relativeTime, repoBasename, humanizeState } from "./format";

// The Fleet cards dashboard (issue #975) - the React port of the Blazor Fleet.razor. It lists every
// session the Gateway roster aggregation returns, grouped by machine, with the one effective status
// color, inline rename, and an Open-session deep link; above it, the Interrupted sessions recovery
// panel (sessions lost to an unexpected Director shutdown, restorable or dismissable). It reads
// same-origin from the Gateway (GET /sessions?envelope=true and GET /interrupted) through
// client-core - never a Director address.
//
// Polling matches the Blazor page: the live roster every 2s, the near-static interrupted list every
// 15s. The last-known roster stays on screen on a transient failure (only the error banner shows),
// and a refresh never clobbers an in-progress rename.
const ROSTER_POLL_MS = 2000;
const INTERRUPTED_POLL_MS = 15000;

interface MachineGroup {
  name: string;
  user: string;
  sessions: SessionDto[];
  error: string | null;
}

interface InterruptedGroup {
  key: string;
  deadDirectorId: string;
  deadPid: number;
  machineName: string;
  reportedByDirectorId: string;
  diedAtUtc: string;
  sessions: InterruptedSession[];
}

export function FleetView() {
  const [sessions, setSessions] = useState<SessionDto[] | null>(null);
  const [machineErrors, setMachineErrors] = useState<MachineError[]>([]);
  const [interrupted, setInterrupted] = useState<InterruptedSession[]>([]);
  const [lastError, setLastError] = useState<string | null>(null);

  // Restore/dismiss in-flight markers and, once restored, the NEW session id for the jump link.
  const [restoring, setRestoring] = useState<ReadonlySet<string>>(new Set());
  const [restored, setRestored] = useState<Record<string, string>>({});
  const [dismissing, setDismissing] = useState<ReadonlySet<string>>(new Set());

  // Inline rename state. editingRef mirrors editingSid so the poll can skip a refresh mid-rename
  // without re-subscribing on every keystroke.
  const [editingSid, setEditingSid] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const editingRef = useRef<string | null>(null);
  editingRef.current = editingSid;

  const loadRoster = useCallback(async (signal?: AbortSignal) => {
    // Never clobber an in-progress rename with a background refresh (Blazor RefreshAsync guard).
    if (editingRef.current !== null) return;
    try {
      const env = await getSessionsEnvelope(signal);
      setSessions(env.sessions);
      setMachineErrors(env.machineErrors);
      setLastError(null);
    } catch (err) {
      if (signal?.aborted === true) return;
      setLastError(err instanceof Error ? err.message : "Failed to fetch sessions");
    }
  }, []);

  const loadInterrupted = useCallback(async (signal?: AbortSignal) => {
    try {
      const fresh = await getInterrupted(signal);
      // Keep restored rows visible as ghosts so the "Open session" jump link survives the journal
      // pruning a successful restore performs (Blazor RefreshInterruptedAsync ghost behavior).
      setInterrupted((prev) => {
        const merged = [...fresh];
        for (const old of prev) {
          const stillGhost =
            restored[old.sessionId] !== undefined && !fresh.some((f) => f.sessionId === old.sessionId);
          if (stillGhost) merged.push(old);
        }
        return merged;
      });
    } catch {
      // Non-fatal: the live roster is the primary view; keep the last interrupted list.
    }
  }, [restored]);

  useEffect(() => {
    const controller = new AbortController();
    void loadRoster(controller.signal);
    const timer = window.setInterval(() => void loadRoster(controller.signal), ROSTER_POLL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [loadRoster]);

  useEffect(() => {
    const controller = new AbortController();
    void loadInterrupted(controller.signal);
    const timer = window.setInterval(() => void loadInterrupted(controller.signal), INTERRUPTED_POLL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [loadInterrupted]);

  const list = sessions ?? [];
  const redCount = list.filter((s) => effectiveColor(s) === "red").length;
  const yellowCount = list.filter((s) => effectiveColor(s) === "yellow").length;
  const groups = machineGroups(list, machineErrors);
  const intGroups = interruptedGroups(interrupted);

  // ---- inline rename ----

  const beginRename = (s: SessionDto) => {
    setEditingSid(s.sessionId ?? null);
    setEditName(s.name ?? "");
  };

  const commitRename = async (s: SessionDto) => {
    const sid = s.sessionId ?? "";
    if (editingRef.current !== sid) return;
    const trimmed = editName.trim();
    setEditingSid(null);
    if (trimmed === (s.name ?? "")) return;
    try {
      const dto = await renameSession(sid, trimmed);
      setSessions((prev) =>
        prev === null ? prev : prev.map((x) => (x.sessionId === sid ? { ...x, name: dto.name } : x)),
      );
    } catch (err) {
      setLastError(err instanceof Error ? `Rename failed: ${err.message}` : "Rename failed");
    }
  };

  // ---- interrupted recovery actions ----

  const doRestore = async (g: InterruptedGroup, s: InterruptedSession) => {
    if (restoring.has(s.sessionId)) return;
    setRestoring((prev) => new Set(prev).add(s.sessionId));
    try {
      const result = await restoreInterrupted(g.deadDirectorId, g.deadPid, s.sessionId, g.reportedByDirectorId);
      const newSid = result.targetSession?.sessionId ?? "";
      if (newSid.length === 0) throw new Error("restore returned no target session");
      setRestored((prev) => ({ ...prev, [s.sessionId]: newSid }));
    } catch (err) {
      setLastError(err instanceof Error ? `Restore failed: ${err.message}` : "Restore failed");
    } finally {
      setRestoring((prev) => {
        const next = new Set(prev);
        next.delete(s.sessionId);
        return next;
      });
    }
  };

  const doDismissSession = async (g: InterruptedGroup, s: InterruptedSession) => {
    if (dismissing.has(s.sessionId)) return;
    setDismissing((prev) => new Set(prev).add(s.sessionId));
    try {
      await dismissInterruptedSession(g.deadDirectorId, g.deadPid, s.sessionId, g.reportedByDirectorId);
      setInterrupted((prev) =>
        prev.filter((x) => !(x.sessionId === s.sessionId && x.deadDirectorId === g.deadDirectorId && x.deadPid === g.deadPid)),
      );
    } catch (err) {
      setLastError(err instanceof Error ? `Dismiss failed: ${err.message}` : "Dismiss failed");
    } finally {
      setDismissing((prev) => {
        const next = new Set(prev);
        next.delete(s.sessionId);
        return next;
      });
    }
  };

  const doDismissGroup = async (g: InterruptedGroup) => {
    if (dismissing.has(g.key)) return;
    setDismissing((prev) => new Set(prev).add(g.key));
    try {
      await dismissInterruptedJournal(g.deadDirectorId, g.deadPid, g.reportedByDirectorId);
      setInterrupted((prev) => prev.filter((s) => !(s.deadDirectorId === g.deadDirectorId && s.deadPid === g.deadPid)));
    } catch (err) {
      setLastError(err instanceof Error ? `Dismiss failed: ${err.message}` : "Dismiss failed");
    } finally {
      setDismissing((prev) => {
        const next = new Set(prev);
        next.delete(g.key);
        return next;
      });
    }
  };

  return (
    <div className="fleet">
      <header className="fleet-head">
        <h1 className="fleet-title">Fleet</h1>
        <span className="fleet-stats">
          {list.length} session{list.length === 1 ? "" : "s"}
          {redCount > 0 && <span className="fleet-red"> &middot; {redCount} red</span>}
          {yellowCount > 0 && <span className="fleet-yellow"> &middot; {yellowCount} yellow</span>}
          {interrupted.length > 0 && (
            <span className="fleet-interrupted"> &middot; {interrupted.length} interrupted</span>
          )}
        </span>
      </header>

      {lastError !== null && <div className="fleet-error">Failed to fetch sessions: {lastError}</div>}

      {intGroups.length > 0 && (
        <section className="fint-wrap">
          <div className="fint-banner">
            <span className="fint-badge">INTERRUPTED</span>
            <span className="fint-bannertext">
              {interrupted.length} session{interrupted.length === 1 ? "" : "s"} were lost to an unexpected
              Director shutdown. Their context is below - recover it, or dismiss what you no longer need.
            </span>
          </div>

          {intGroups.map((g) => (
            <div key={g.key} className="fint-group">
              <div className="fint-head">
                <span className="fint-machine">
                  {g.machineName.trim().length === 0 ? "(unknown machine)" : g.machineName}
                </span>
                <span className="fint-when">
                  Director died {relativeTime(g.diedAtUtc)} ago &middot; {g.sessions.length} session
                  {g.sessions.length === 1 ? "" : "s"}
                </span>
                <span className="fleet-spacer" />
                <button
                  type="button"
                  className="fint-dismiss"
                  onClick={() => void doDismissGroup(g)}
                  disabled={dismissing.has(g.key)}
                >
                  {dismissing.has(g.key) ? "Dismissing..." : "Dismiss all"}
                </button>
              </div>
              <div className="fleet-cards">
                {g.sessions.map((s) => {
                  const newSid = restored[s.sessionId];
                  return (
                    <article key={s.sessionId} className="fint-card">
                      <div className="fleet-card-head">
                        <span className="fleet-dot fleet-dot-interrupted" title={`Director died ${relativeTime(g.diedAtUtc)} ago`} />
                        <span className={`fleet-cardtitle${(s.name ?? "").trim().length === 0 ? " fleet-unnamed" : ""}`}>
                          {(s.name ?? "").trim().length === 0 ? "(unnamed)" : s.name}
                        </span>
                        <span className="fleet-agent">{(s.agent ?? "").trim().length === 0 ? "?" : s.agent}</span>
                      </div>
                      <div className="fleet-repo" title={s.repoPath ?? undefined}>{repoBasename(s.repoPath)}</div>
                      {(s.railLine ?? "").trim().length > 0 ? (
                        <div className="fint-rail"><span className="fint-rail-lbl">last wingman read</span>{s.railLine}</div>
                      ) : (s.headline ?? "").trim().length > 0 ? (
                        <div className="fint-rail"><span className="fint-rail-lbl">was working on</span>{s.headline}</div>
                      ) : null}
                      <div className="fint-actions">
                        {newSid !== undefined ? (
                          <span className="fint-restoredone">
                            Restored
                            <Link to={`/session/${encodeURIComponent(newSid)}`}>Open session &rarr;</Link>
                          </span>
                        ) : restoring.has(s.sessionId) ? (
                          <>
                            <button type="button" className="fint-restore" disabled>Restoring...</button>
                            <span className="fint-restoring">creating continuation session</span>
                          </>
                        ) : (
                          <>
                            <button type="button" className="fint-restore" onClick={() => void doRestore(g, s)}>Restore</button>
                            <button
                              type="button"
                              className="fint-carddismiss"
                              onClick={() => void doDismissSession(g, s)}
                              disabled={dismissing.has(s.sessionId)}
                            >
                              Dismiss
                            </button>
                          </>
                        )}
                      </div>
                    </article>
                  );
                })}
              </div>
            </div>
          ))}
        </section>
      )}

      {sessions === null && lastError === null && <div className="fleet-empty">Loading sessions...</div>}

      {sessions !== null && list.length === 0 && machineErrors.length === 0 && (
        <div className="fleet-empty">
          <p>No sessions registered anywhere on the fleet.</p>
          <p className="fleet-empty-sub">
            A session will appear here when a Director starts one. Make sure the Director has{" "}
            <code>gateway.url</code> configured if it is running on another machine.
          </p>
        </div>
      )}

      {groups.map((g) => (
        <section key={g.name} className="fleet-group">
          <div className="fleet-machine">
            <span className="fleet-mname">{g.name}</span>
            {g.user.length > 0 && <span className="fleet-muser"> &middot; {g.user}</span>}
            <span className="fleet-mcount"> &middot; {g.sessions.length} session{g.sessions.length === 1 ? "" : "s"}</span>
            {g.error !== null && <span className="fleet-munreachable"> &middot; unreachable: {g.error}</span>}
          </div>

          {g.sessions.length > 0 && (
            <div className="fleet-cards">
              {g.sessions.map((s) => (
                <FleetCard
                  key={s.sessionId}
                  session={s}
                  editing={editingSid === s.sessionId}
                  editName={editName}
                  onEditNameChange={setEditName}
                  onBeginRename={() => beginRename(s)}
                  onCommit={() => void commitRename(s)}
                  onCancel={() => setEditingSid(null)}
                />
              ))}
            </div>
          )}
        </section>
      ))}
    </div>
  );
}

interface FleetCardProps {
  session: SessionDto;
  editing: boolean;
  editName: string;
  onEditNameChange: (value: string) => void;
  onBeginRename: () => void;
  onCommit: () => void;
  onCancel: () => void;
}

function FleetCard({ session: s, editing, editName, onEditNameChange, onBeginRename, onCommit, onCancel }: FleetCardProps) {
  const color = effectiveColor(s);
  const unnamed = (s.name ?? "").trim().length === 0;
  const sid = s.sessionId ?? "";
  const hasNum = s.number !== null && s.number !== undefined && String(s.number).trim().length > 0;
  return (
    <article className="fleet-card">
      <div className="fleet-card-head">
        <span className="fleet-dot" style={{ backgroundColor: dotColor(color) }} title={s.lastStatusReason ?? undefined} />
        {hasNum && <span className="fleet-num" title="Session number">{s.number}</span>}
        {editing ? (
          <input
            className="fleet-name-edit"
            value={editName}
            autoFocus
            maxLength={120}
            placeholder="Session name"
            onChange={(e) => onEditNameChange(e.target.value)}
            onBlur={onCommit}
            onKeyDown={(e) => {
              if (e.key === "Enter") onCommit();
              else if (e.key === "Escape") onCancel();
            }}
          />
        ) : (
          <span className={`fleet-cardtitle${unnamed ? " fleet-unnamed" : ""}`} title="Click to rename" onClick={onBeginRename}>
            {unnamed ? "(unnamed)" : s.name}
            <span className="fleet-pencil">&#9998;</span>
          </span>
        )}
        <span className="fleet-agent">{(s.agent ?? "").trim().length === 0 ? "?" : s.agent}</span>
      </div>
      <div className="fleet-repo" title={s.repoPath ?? undefined}>{repoBasename(s.repoPath)}</div>
      <div className="fleet-state-row">
        <span className="fleet-state">{humanizeState(s.assessedState ?? s.activityState)}</span>
        <span className="fleet-idle" title={`last activity ${s.lastActivityAt ?? ""}`}>{relativeTime(s.lastActivityAt)}</span>
      </div>
      {sid.length > 0 && (
        <Link className="fleet-open" to={`/session/${encodeURIComponent(sid)}`}>Open session &rarr;</Link>
      )}
    </article>
  );
}

// ---- grouping (pure) ----

// Group the roster by machine (case-insensitive), each group's sessions in a STABLE order (creation
// time, then session id) so a card never jumps when its color changes. Unreachable machines with no
// live sessions still get a header from the envelope's machineErrors. Matches Blazor Fleet.Groups().
function machineGroups(sessions: SessionDto[], errors: MachineError[]): MachineGroup[] {
  const byMachine = new Map<string, { key: string; user: string; list: SessionDto[]; error: string | null }>();
  const keyOf = (name: string | null | undefined) =>
    (name ?? "").trim().length === 0 ? "(unknown)" : (name ?? "");

  for (const s of sessions) {
    const name = keyOf(s.machineName);
    const lower = name.toLowerCase();
    let g = byMachine.get(lower);
    if (g === undefined) {
      g = { key: name, user: s.user ?? "", list: [], error: null };
      byMachine.set(lower, g);
    }
    g.list.push(s);
  }
  for (const me of errors) {
    const name = keyOf(me.machineName);
    const lower = name.toLowerCase();
    const g = byMachine.get(lower);
    if (g === undefined) byMachine.set(lower, { key: name, user: "", list: [], error: me.error ?? "" });
    else g.error = me.error ?? "";
  }

  return [...byMachine.values()]
    .map((g) => ({
      name: g.key,
      user: g.user,
      error: g.error,
      sessions: [...g.list].sort((a, b) => {
        const byCreated = String(a.createdAt ?? "").localeCompare(String(b.createdAt ?? ""));
        if (byCreated !== 0) return byCreated;
        return String(a.sessionId ?? "").localeCompare(String(b.sessionId ?? ""));
      }),
    }))
    .sort((a, b) => a.name.toLowerCase().localeCompare(b.name.toLowerCase()));
}

// Group interrupted sessions by dead Director + pid, newest death first, sessions oldest-created
// first within a group. Matches Blazor Fleet.InterruptedGroups().
function interruptedGroups(interrupted: InterruptedSession[]): InterruptedGroup[] {
  const byKey = new Map<string, InterruptedSession[]>();
  for (const s of interrupted) {
    const key = `${s.deadDirectorId}.${s.deadPid}`;
    const arr = byKey.get(key);
    if (arr === undefined) byKey.set(key, [s]);
    else arr.push(s);
  }
  return [...byKey.entries()]
    .map(([key, arr]) => {
      const first = arr[0];
      return {
        key,
        deadDirectorId: first.deadDirectorId,
        deadPid: first.deadPid,
        machineName: first.machineName ?? "",
        reportedByDirectorId: first.reportedByDirectorId,
        diedAtUtc: first.diedAtUtc ?? "",
        sessions: [...arr].sort((a, b) => String(a.createdAtUtc ?? "").localeCompare(String(b.createdAtUtc ?? ""))),
      };
    })
    .sort((a, b) => String(b.diedAtUtc).localeCompare(String(a.diedAtUtc)));
}
