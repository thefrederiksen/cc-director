import { useCallback, useEffect, useRef, useState } from "react";
import {
  createSession,
  getDirectors,
  getRepos,
  type DirectorInfo,
  type RepoInfo,
} from "@devthrottle/client-core/api/client";

// The desktop Cockpit "New session" dialog (issue #1023, QA sweep epic #967). The React Cockpit had
// no way to start a session; this is the dedicated picker dialog the roster rail's "+ New session"
// control opens (the "selection via a dialog, not inline" convention). It reuses the same client-core
// contract the mobile NewSession flow uses - getDirectors / getRepos / createSession - so both shells
// create sessions through the one Gateway front door (POST /directors/{id}/sessions).
//
// The flow mirrors the mobile two-step:
//   1. Pick a MACHINE from GET /directors (default-select the most-recently-seen so repos load with
//      one fewer click).
//   2. Pick a REPOSITORY from GET /directors/{id}/repos (newest-used first) OR type a path.
// On success the parent is told the new session id so it can refresh the roster and open it.

export interface NewSessionDialogProps {
  /** Close the dialog without creating (Cancel / backdrop / Escape). */
  onClose: () => void;
  /** A session was created; the parent refreshes the roster and opens it. */
  onCreated: (sessionId: string) => void;
}

function directorLabel(d: DirectorInfo): string {
  if (d.machineName.trim()) return d.machineName.trim();
  return d.directorId || "director";
}

function repoLabel(r: RepoInfo): string {
  if (r.name.trim()) return r.name.trim();
  const parts = r.path.replace(/[\\/]+$/, "").split(/[\\/]/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : r.path;
}

export function NewSessionDialog({ onClose, onCreated }: NewSessionDialogProps) {
  const [directors, setDirectors] = useState<DirectorInfo[] | null>(null);
  const [directorsError, setDirectorsError] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const [repos, setRepos] = useState<RepoInfo[] | null>(null);
  const [reposStatus, setReposStatus] = useState("Pick a machine first.");

  const [manualPath, setManualPath] = useState("");
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);

  // Guards against a stale repo response landing after the user switched machines.
  const reposReqRef = useRef(0);

  // Step 1: load the machines once, default-selecting the most-recently-seen (directors[0]) so the
  // repos load immediately (mobile NewSession / Android OpenNewSessionPanelAsync parity).
  useEffect(() => {
    const controller = new AbortController();
    getDirectors(controller.signal)
      .then((list) => {
        setDirectors(list);
        setDirectorsError(null);
        if (list.length > 0) setSelectedId(list[0].directorId);
      })
      .catch((err) => {
        if (controller.signal.aborted) return;
        setDirectorsError(err instanceof Error ? err.message : "Could not load machines");
      });
    return () => controller.abort();
  }, []);

  // Step 2: whenever the selected machine changes, load THAT machine's recent repos.
  useEffect(() => {
    if (!selectedId) return;
    const controller = new AbortController();
    const reqId = ++reposReqRef.current;
    setRepos(null);
    setReposStatus("Loading repos...");
    getRepos(selectedId, controller.signal)
      .then((list) => {
        if (reqId !== reposReqRef.current) return; // a newer selection superseded this one
        setRepos(list);
        setReposStatus(
          list.length === 0
            ? "No recent repos here. Enter a path below."
            : `${list.length} recent repo(s). Click one to start.`,
        );
      })
      .catch((err) => {
        if (controller.signal.aborted || reqId !== reposReqRef.current) return;
        setRepos([]);
        setReposStatus(
          err instanceof Error ? `Could not load repos: ${err.message}` : "Could not load repos",
        );
      });
    return () => controller.abort();
  }, [selectedId]);

  // Close on Escape, matching the desktop dialog convention.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const create = useCallback(
    async (repoPath: string) => {
      if (creating) return; // a create is already in flight (guards a double-click)
      if (!selectedId) {
        setCreateError("Pick a machine first.");
        return;
      }
      const path = repoPath.trim();
      if (!path) {
        setCreateError("Enter a repo path, or click a recent repo above.");
        return;
      }
      setCreating(true);
      setCreateError(null);
      try {
        const session = await createSession(selectedId, path);
        const sid = session.sessionId;
        if (!sid) throw new Error("The created session had no id.");
        onCreated(sid);
      } catch (err) {
        // Surface the Gateway's message (a bad repo path or an unreachable Director) inline, never a
        // raw thrown error.
        setCreateError(err instanceof Error ? err.message : "Could not create session");
        setCreating(false);
      }
    },
    [creating, selectedId, onCreated],
  );

  return (
    <div className="newsess-backdrop" onClick={onClose}>
      <div
        className="newsess-modal"
        role="dialog"
        aria-modal="true"
        aria-label="Start a new session"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="newsess-head">Start a new session</div>

        <div className="newsess-body">
          {/* Step 1: machine */}
          <div className="newsess-step">
            <div className="newsess-step-label">1. Machine</div>
            {directorsError !== null && (
              <div className="newsess-error" role="alert">
                {directorsError}
              </div>
            )}
            {directors === null && directorsError === null && (
              <div className="newsess-status">Loading machines...</div>
            )}
            {directors !== null && directors.length === 0 && (
              <div className="newsess-status">No machines found on this Gateway.</div>
            )}
            {directors !== null && directors.length > 0 && (
              <ul className="newsess-list">
                {directors.map((d) => (
                  <li key={d.directorId}>
                    <button
                      type="button"
                      className={`newsess-pick${d.directorId === selectedId ? " sel" : ""}`}
                      onClick={() => setSelectedId(d.directorId)}
                    >
                      <span className="newsess-pick-name">{directorLabel(d)}</span>
                      {d.directorId === selectedId && (
                        <span className="newsess-pick-check" aria-hidden="true">
                          selected
                        </span>
                      )}
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>

          {/* Step 2: repository */}
          <div className="newsess-step">
            <div className="newsess-step-label">2. Repository</div>
            <div className="newsess-status">{reposStatus}</div>
            {repos !== null && repos.length > 0 && (
              <ul className="newsess-list">
                {repos.map((r) => (
                  <li key={r.path}>
                    <button
                      type="button"
                      className="newsess-pick"
                      disabled={creating}
                      onClick={() => void create(r.path)}
                    >
                      <span className="newsess-pick-name">{repoLabel(r)}</span>
                      <span className="newsess-pick-path">{r.path}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}

            <label className="newsess-manual-label" htmlFor="newsess-path">
              Or enter a path
            </label>
            <div className="newsess-manual">
              <input
                id="newsess-path"
                className="newsess-input mono"
                type="text"
                autoComplete="off"
                autoCapitalize="off"
                autoCorrect="off"
                spellCheck={false}
                placeholder="D:\Repos\my-project"
                value={manualPath}
                onChange={(e) => setManualPath(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    e.preventDefault();
                    void create(manualPath);
                  }
                }}
              />
            </div>
          </div>

          {createError !== null && (
            <div className="newsess-error" role="alert">
              {createError}
            </div>
          )}
        </div>

        <div className="newsess-foot">
          <button type="button" className="newsess-btn" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="newsess-btn primary"
            disabled={creating || !selectedId}
            onClick={() => void create(manualPath)}
          >
            {creating ? "Creating..." : "Create session"}
          </button>
        </div>
      </div>
    </div>
  );
}
