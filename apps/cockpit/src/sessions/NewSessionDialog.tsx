import { useCallback, useEffect, useRef, useState } from "react";
import {
  createSession,
  getAgents,
  getDirectors,
  getRepos,
  gatewayErrorMessage,
  type AgentChoice,
  type DirectorInfo,
  type RepoInfo,
} from "@devthrottle/client-core/api/client";

// The desktop Cockpit "New session" dialog (issue #1023, QA sweep epic #967). The React Cockpit had
// no way to start a session; this is the dedicated picker dialog the roster rail's "+ New session"
// control opens (the "selection via a dialog, not inline" convention). It reuses the same client-core
// contract the mobile NewSession flow uses - getDirectors / getRepos / createSession - so both shells
// create sessions through the one Gateway front door (POST /directors/{id}/sessions).
//
// The flow mirrors the desktop New Session dialog:
//   1. Pick a MACHINE from GET /directors (default-select the most-recently-seen so repos+agents load
//      with one fewer click).
//   2. Pick a REPOSITORY from GET /directors/{id}/repos (newest-used first) OR type a path.
//   3. LAUNCH OPTIONS: pick the AGENT from GET /directors/{id}/agents (that machine's configured,
//      enabled agents), and choose the permission mode. There is deliberately NO model picker - the
//      model comes from the chosen agent's own configured default, exactly like the desktop dialog
//      (issue #1497). The client sends only the agent kind and the Bypass-permissions choice; the
//      Director applies that agent's configured default model and permission preset.
// On success the parent is told the new session id so it can refresh the roster and open it.

export interface NewSessionDialogProps {
  /** Close the dialog without creating (Cancel / backdrop / Escape). */
  onClose: () => void;
  /** A session was created; the parent refreshes the roster and opens it. */
  onCreated: (sessionId: string) => void;
}

// Permission choices, mapped to the desktop dialog's "Bypass permission prompts" checkbox. "Skip
// permission prompts" is the desktop default (the checkbox defaults to ON), so it is first / pre-selected.
// Neither adds a command-line argument on the client: the choice is sent as a structured flag and the
// Director resolves the agent's configured launch line accordingly, so the model is unaffected either way.
interface PermissionChoice {
  key: string;
  label: string;
  bypass: boolean;
}
const PERMISSION_CHOICES: PermissionChoice[] = [
  { key: "skip", label: "Skip permission prompts", bypass: true },
  { key: "ask", label: "Ask for permissions", bypass: false },
];

const AGENT_STORAGE_KEY = "cockpit.newSession.agent";
const PERMISSION_STORAGE_KEY = "cockpit.newSession.permission";

// Read a remembered string from localStorage, or null when unavailable/absent (private mode is fine).
function loadStored(storageKey: string): string | null {
  try {
    return window.localStorage.getItem(storageKey);
  } catch {
    return null;
  }
}

// Save a chosen value so the next open pre-selects it. Best-effort (private mode may reject it).
function saveStored(storageKey: string, value: string): void {
  try {
    window.localStorage.setItem(storageKey, value);
  } catch {
    /* localStorage can be unavailable (private mode); remembering the choice is best-effort */
  }
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

// The one-line summary of what will launch, e.g. "Claude Code . Opus 4.8 . skips permission prompts".
// Mirrors the desktop dialog telling you the agent and the model it will use (issue #1497).
function launchSummary(agent: AgentChoice | null, bypass: boolean): string {
  if (agent === null) return "Pick an agent above.";
  const model = agent.modelLabel.trim() || "its default model";
  const permission = bypass ? "skips permission prompts" : "asks for permissions";
  return `${agent.displayName} . ${model} . ${permission}`;
}

export function NewSessionDialog({ onClose, onCreated }: NewSessionDialogProps) {
  const [directors, setDirectors] = useState<DirectorInfo[] | null>(null);
  const [directorsError, setDirectorsError] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const [repos, setRepos] = useState<RepoInfo[] | null>(null);
  const [reposStatus, setReposStatus] = useState("Pick a machine first.");

  // The selected machine's configured agents (issue #1497), loaded like the repos when the machine
  // changes. `agentsStatus` carries the loading / empty / error line for the agent picker.
  const [agents, setAgents] = useState<AgentChoice[] | null>(null);
  const [agentsStatus, setAgentsStatus] = useState<string | null>(null);
  const [selectedAgentType, setSelectedAgentType] = useState<string | null>(null);

  const [manualPath, setManualPath] = useState("");
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);

  // Permission mode, pre-selected from the last-used choice (default: Skip, matching the desktop
  // Bypass-permissions checkbox default of ON).
  const [permissionKey, setPermissionKey] = useState(() => {
    const saved = loadStored(PERMISSION_STORAGE_KEY);
    return saved && PERMISSION_CHOICES.some((c) => c.key === saved) ? saved : PERMISSION_CHOICES[0].key;
  });
  const permission = PERMISSION_CHOICES.find((c) => c.key === permissionKey) ?? PERMISSION_CHOICES[0];

  const selectedAgent = agents?.find((a) => a.type === selectedAgentType) ?? null;

  // Guards against a stale repo/agent response landing after the user switched machines.
  const reposReqRef = useRef(0);
  const agentsReqRef = useRef(0);

  // Step 1: load the machines once, default-selecting the most-recently-seen (directors[0]) so the
  // repos and agents load immediately (desktop New Session parity).
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
        setReposStatus(`Could not load repos: ${gatewayErrorMessage(err)}`);
      });
    return () => controller.abort();
  }, [selectedId]);

  // Step 3: whenever the selected machine changes, load THAT machine's configured agents (issue #1497).
  // Default-select the last-used agent when it is still offered, else the first - mirroring the desktop
  // dialog, which pre-selects the first enabled agent.
  useEffect(() => {
    if (!selectedId) return;
    const controller = new AbortController();
    const reqId = ++agentsReqRef.current;
    setAgents(null);
    setSelectedAgentType(null);
    setAgentsStatus("Loading agents...");
    getAgents(selectedId, controller.signal)
      .then((list) => {
        if (reqId !== agentsReqRef.current) return; // a newer selection superseded this one
        setAgents(list);
        if (list.length === 0) {
          setAgentsStatus("No agents configured on this machine.");
          return;
        }
        setAgentsStatus(null);
        const remembered = loadStored(AGENT_STORAGE_KEY);
        const pick = list.find((a) => a.type === remembered) ?? list[0];
        setSelectedAgentType(pick.type);
      })
      .catch((err) => {
        if (controller.signal.aborted || reqId !== agentsReqRef.current) return;
        setAgents([]);
        setAgentsStatus(`Could not load agents: ${gatewayErrorMessage(err)}`);
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
      // The agent is required when the machine offers a list; fall back to Claude Code only when the
      // list could not be loaded, so the dialog still works if the agents read failed.
      if (agents !== null && agents.length > 0 && !selectedAgentType) {
        setCreateError("Pick an agent below.");
        return;
      }
      const agentType = selectedAgentType ?? "ClaudeCode";
      setCreating(true);
      setCreateError(null);
      // Remember the choices so the next open pre-selects them.
      saveStored(AGENT_STORAGE_KEY, agentType);
      saveStored(PERMISSION_STORAGE_KEY, permissionKey);
      try {
        const session = await createSession(selectedId, path, {
          agent: agentType,
          bypassPermissions: permission.bypass,
          signal: undefined,
        });
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
    [creating, selectedId, agents, selectedAgentType, permission.bypass, permissionKey, onCreated],
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

          {/* Step 3: launch options (agent and permission mode), remembered across uses. No model
              picker - the model comes from the chosen agent's own configured default (issue #1497). */}
          <div className="newsess-step">
            <div className="newsess-step-label">3. Launch options</div>

            <div className="newsess-opt-label" id="newsess-agent-label">
              Agent
            </div>
            {agentsStatus !== null && <div className="newsess-status">{agentsStatus}</div>}
            {agents !== null && agents.length > 0 && (
              <div className="newsess-seg" role="group" aria-labelledby="newsess-agent-label">
                {agents.map((a) => (
                  <button
                    key={a.type}
                    type="button"
                    className={`newsess-seg-btn${a.type === selectedAgentType ? " sel" : ""}`}
                    aria-pressed={a.type === selectedAgentType}
                    disabled={creating}
                    onClick={() => setSelectedAgentType(a.type)}
                  >
                    {a.displayName}
                  </button>
                ))}
              </div>
            )}

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

            <div className="newsess-opt-label">This session will start as</div>
            <div className="newsess-args mono" aria-live="polite">
              {launchSummary(selectedAgent, permission.bypass)}
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
