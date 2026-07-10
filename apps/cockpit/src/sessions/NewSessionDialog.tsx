import { useCallback, useEffect, useRef, useState } from "react";
import {
  createSession,
  getDirectors,
  getRepos,
  gatewayErrorMessage,
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

// A launch choice: the value we remember (key), what the user reads (label), and the exact command-line
// fragment it contributes (arg - empty string means "add nothing", which is the Director's own default).
interface LaunchChoice {
  key: string;
  label: string;
  arg: string;
}

// Model choices. "Default" adds no argument, so the Director launches the agent's built-in default model;
// the others pass "--model <name>" exactly (issue #1243).
const MODEL_CHOICES: LaunchChoice[] = [
  { key: "default", label: "Default", arg: "" },
  { key: "opus", label: "Opus", arg: "--model opus" },
  { key: "sonnet", label: "Sonnet", arg: "--model sonnet" },
];

// Permission choices. "Ask for permissions" adds no argument (the agent stops for each permission prompt);
// "Skip permission prompts" passes --dangerously-skip-permissions so the session starts working at once.
// We do not steer the user toward either - the default selection is simply their last-used choice.
const PERMISSION_CHOICES: LaunchChoice[] = [
  { key: "ask", label: "Ask for permissions", arg: "" },
  { key: "skip", label: "Skip permission prompts", arg: "--dangerously-skip-permissions" },
];

const MODEL_STORAGE_KEY = "cockpit.newSession.model";
const PERMISSION_STORAGE_KEY = "cockpit.newSession.permission";

// Read the last-used choice key from localStorage, falling back to the first choice when nothing was
// stored or the stored key no longer matches a known choice.
function loadChoiceKey(storageKey: string, choices: LaunchChoice[]): string {
  try {
    const saved = window.localStorage.getItem(storageKey);
    if (saved && choices.some((c) => c.key === saved)) return saved;
  } catch {
    /* localStorage can be unavailable (private mode); fall back to the first choice */
  }
  return choices[0].key;
}

// Save the chosen key so the next open of the dialog pre-selects it.
function saveChoiceKey(storageKey: string, key: string): void {
  try {
    window.localStorage.setItem(storageKey, key);
  } catch {
    /* localStorage can be unavailable (private mode); remembering the choice is best-effort */
  }
}

// Assemble the launch-argument string from the two chosen fragments, dropping the empty ("default")
// ones. Returns "" when both are default, so createSession sends no "args" at all.
function buildLaunchArgs(modelKey: string, permissionKey: string): string {
  const model = MODEL_CHOICES.find((c) => c.key === modelKey);
  const permission = PERMISSION_CHOICES.find((c) => c.key === permissionKey);
  return [model?.arg ?? "", permission?.arg ?? ""].filter((a) => a.length > 0).join(" ");
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

  // Launch options, pre-selected from the last-used choice (issue #1243). Lazy initial state reads
  // localStorage exactly once.
  const [modelKey, setModelKey] = useState(() => loadChoiceKey(MODEL_STORAGE_KEY, MODEL_CHOICES));
  const [permissionKey, setPermissionKey] = useState(() =>
    loadChoiceKey(PERMISSION_STORAGE_KEY, PERMISSION_CHOICES),
  );
  const launchArgs = buildLaunchArgs(modelKey, permissionKey);

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
        setDirectorsError(gatewayErrorMessage(err));
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
          `Could not load repos: ${gatewayErrorMessage(err)}`,
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
      // Remember the choices so the next open pre-selects them (issue #1243).
      saveChoiceKey(MODEL_STORAGE_KEY, modelKey);
      saveChoiceKey(PERMISSION_STORAGE_KEY, permissionKey);
      try {
        const session = await createSession(selectedId, path, launchArgs);
        const sid = session.sessionId;
        if (!sid) throw new Error("The created session had no id.");
        onCreated(sid);
      } catch (err) {
        // Surface the Gateway's message (a bad repo path or an unreachable Director) inline, never a
        // raw thrown error.
        setCreateError(gatewayErrorMessage(err));
        setCreating(false);
      }
    },
    [creating, selectedId, onCreated, launchArgs, modelKey, permissionKey],
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

          {/* Step 3: launch options (model and permission mode), remembered across uses */}
          <div className="newsess-step">
            <div className="newsess-step-label">3. Launch options</div>

            <div className="newsess-opt-label" id="newsess-model-label">
              Model
            </div>
            <div className="newsess-seg" role="group" aria-labelledby="newsess-model-label">
              {MODEL_CHOICES.map((c) => (
                <button
                  key={c.key}
                  type="button"
                  className={`newsess-seg-btn${c.key === modelKey ? " sel" : ""}`}
                  aria-pressed={c.key === modelKey}
                  disabled={creating}
                  onClick={() => setModelKey(c.key)}
                >
                  {c.label}
                </button>
              ))}
            </div>

            <div className="newsess-opt-label" id="newsess-permission-label">
              Permission mode
            </div>
            <div className="newsess-seg" role="group" aria-labelledby="newsess-permission-label">
              {PERMISSION_CHOICES.map((c) => (
                <button
                  key={c.key}
                  type="button"
                  className={`newsess-seg-btn${c.key === permissionKey ? " sel" : ""}`}
                  aria-pressed={c.key === permissionKey}
                  disabled={creating}
                  onClick={() => setPermissionKey(c.key)}
                >
                  {c.label}
                </button>
              ))}
            </div>

            <div className="newsess-opt-label">Arguments passed to the session</div>
            <div className="newsess-args mono" aria-live="polite">
              {launchArgs.length > 0 ? launchArgs : "(none - default model, asks for permissions)"}
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
