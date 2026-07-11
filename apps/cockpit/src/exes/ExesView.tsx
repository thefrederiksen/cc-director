import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  buildStartSlot,
  deleteSlot,
  getExes,
  killDirector,
  type ExesDirector,
  type ExesList,
} from "@devthrottle/client-core/exes/exesClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { dotColor } from "@devthrottle/client-core/sessions/ordering";
import { ConfirmDialog, EmptyState, ErrorBanner, LoadingState, StatusMessage, useFlash } from "../components";
import { portOf, repoBasename, uptime } from "../fleet/format";

// The Exes management page (issue #977, epic #967) - the React port of the Blazor Cockpit
// Exes.razor(.css) (#183). It lists the Directors running on THIS computer + their sessions and the
// 1-4 build slots, refreshes on a 3s timer that never fires over an in-flight build, and offers
// Kill / Build & start / Delete against the same Gateway endpoints. It reads and drives same-origin
// through the Gateway front door (client-core) - never a Director address.
//
// This page is one of the first three to adopt the shared user-interface kit (issue #1244): every
// destructive action now asks through the shared ConfirmDialog instead of a blocking browser
// window.confirm, and every action result is a StatusMessage instead of a browser window.alert.
const REFRESH_MS = 3000;

// The action awaiting confirmation. Exes has three heavy actions, so a discriminated union feeds the
// single ConfirmDialog: killing a Director (destructive), deleting a slot build (destructive), and
// building + starting a slot (heavy but not destructive - danger: false).
type PendingAction =
  | { kind: "kill"; dir: ExesDirector }
  | { kind: "deleteSlot"; slot: number }
  | { kind: "buildSlot"; slot: number };

export function ExesView() {
  const [data, setData] = useState<ExesList | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false); // never refresh over an in-flight build (mirrors state.busy)
  const [buildingSlot, setBuildingSlot] = useState<number | null>(null);
  const [pending, setPending] = useState<PendingAction | null>(null);
  const result = useFlash();

  // busyRef mirrors busy so the interval callback reads the latest value without re-subscribing.
  const busyRef = useRef(false);
  busyRef.current = busy;

  const refresh = useCallback(async (signal?: AbortSignal) => {
    if (busyRef.current) return; // never refresh over an in-flight build
    try {
      const fresh = await getExes(signal);
      setData(fresh);
      setError(null);
    } catch (err) {
      if (signal?.aborted === true) return;
      setError(`Failed to load: ${gatewayErrorMessage(err)}`);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void refresh(controller.signal);
    const timer = window.setInterval(() => void refresh(controller.signal), REFRESH_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [refresh]);

  const hasRepoRoot = data !== null && data.repoRoot.trim().length > 0;
  const statsText =
    data === null
      ? "loading..."
      : `${data.directors.length} director${data.directors.length === 1 ? "" : "s"} on ${data.machineName}`;

  // ---- actions (run once the ConfirmDialog is confirmed) ----
  // Each leaves failures to throw so the dialog surfaces them (fail loudly); on success it flashes a
  // StatusMessage. None uses a browser pop-up.
  const killDir = async (dir: ExesDirector) => {
    await killDirector(dir.directorId);
    await new Promise((r) => window.setTimeout(r, 600));
    await refresh();
    result.show(`Director ${dir.slot != null ? `slot ${dir.slot}` : `PID ${dir.pid}`} killed.`, "success");
  };

  const removeSlot = async (n: number) => {
    await deleteSlot(n);
    await refresh();
    result.show(`Slot ${n} build deleted.`, "success");
  };

  const buildStart = async (n: number) => {
    setBusy(true);
    busyRef.current = true;
    setBuildingSlot(n);
    try {
      const built = await buildStartSlot(n);
      result.show(`Slot ${n} built and started (PID ${built.pid}).`, "success");
    } finally {
      setBusy(false);
      busyRef.current = false;
      setBuildingSlot(null);
    }
    await refresh();
  };

  // Derive the ConfirmDialog's copy and confirmed action from the pending action.
  const confirm = describePending(pending, { killDir, removeSlot, buildStart });

  return (
    <div className="ex-root">
      <header className="ex-header">
        <h1>DEVTHROTTLE &middot; EXES</h1>
        <span className="ex-stats">{statsText}</span>
        <StatusMessage flash={result.flash} />
        <span className="ex-spacer" />
        <Link className="ex-link ex-link-accent" to="/sessions">
          sessions
        </Link>
        <Link className="ex-link ex-link-accent" to="/transcripts">
          transcripts
        </Link>
      </header>

      <main className="ex-main">
        {error !== null && <ErrorBanner message={error} onRetry={() => void refresh()} />}

        {data === null ? (
          <LoadingState />
        ) : (
          <>
            {!hasRepoRoot && (
              <div className="ex-notice">
                Slot management is unavailable: the Gateway is not running from inside the cc-director
                repo, so build scripts and local_builds cannot be located.
              </div>
            )}

            {/* ----- running directors ----- */}
            <h2 className="ex-section">
              Running directors on this computer
              {hasRepoRoot && <span className="ex-repo-root"> &middot; {data.repoRoot}</span>}
            </h2>

            {data.directors.length === 0 ? (
              <EmptyState message="No Director processes are running on this computer." />
            ) : (
              data.directors.map((dir) => (
                <div className="ex-dir" key={dir.directorId}>
                  <div className="ex-head">
                    <span className={`ex-badge ${dir.slot == null ? "ex-gray" : ""}`}>
                      {dir.slot == null ? "no slot" : `slot ${dir.slot}`}
                    </span>
                    <span className="ex-meta">
                      PID <b>{dir.pid}</b> &middot; port <b>{portOf(dir.controlEndpoint) ?? "?"}</b> &middot; v
                      {dir.version ?? "?"} &middot; up {uptime(dir.startedAt)}
                    </span>
                    <span className="ex-spacer" />
                    {dir.directorUrl && dir.directorUrl.trim().length > 0 && (
                      <a className="ex-btn" href={dir.directorUrl}>
                        Director &rarr;
                      </a>
                    )}
                    <button
                      className="ex-btn ex-danger"
                      onClick={() => setPending({ kind: "kill", dir })}
                      disabled={busy}
                    >
                      Kill
                    </button>
                  </div>
                  {dir.exePath.trim().length > 0 && (
                    <div className="ex-exe" title={dir.exePath}>
                      {dir.exePath}
                    </div>
                  )}
                  <div className="ex-sessions">
                    {dir.sessionError && dir.sessionError.trim().length > 0 ? (
                      <div className="ex-none">sessions unavailable: {dir.sessionError}</div>
                    ) : dir.sessions.length === 0 ? (
                      <div className="ex-none">No sessions.</div>
                    ) : (
                      dir.sessions.map((s) => (
                        <div className="ex-sess" key={s.sessionId}>
                          <span className="ex-dot" style={{ backgroundColor: dotColor(s.effectiveColor ?? "unknown") }} />
                          {!s.name || s.name.trim().length === 0 ? (
                            <span className="ex-sname ex-unnamed">(unnamed)</span>
                          ) : (
                            <span className="ex-sname">{s.name}</span>
                          )}
                          <span className="ex-agent-pill">
                            {!s.agent || s.agent.trim().length === 0 ? "?" : s.agent}
                          </span>
                          <span className="ex-sstate">
                            {repoBasename(s.repoPath)} &middot; {s.stateLabel ?? "-"}
                          </span>
                        </div>
                      ))
                    )}
                  </div>
                </div>
              ))
            )}

            {/* ----- build slots ----- */}
            <h2 className="ex-section">Build slots (1-4)</h2>
            {!hasRepoRoot ? (
              <EmptyState message="Unavailable (see notice above)." />
            ) : (
              <div className="ex-slots">
                {data.slots.map((sl) => {
                  const running = sl.running != null;
                  const statusCls = running ? "ex-running" : sl.exists ? "ex-built" : "ex-missing";
                  const statusTxt = running
                    ? `running (PID ${sl.running?.pid})`
                    : sl.exists
                    ? "built, stopped"
                    : "not built";
                  return (
                    <div className="ex-slot" key={sl.slot}>
                      <div className="ex-shead">
                        <span className="ex-stitle">Slot {sl.slot}</span>
                        <span className="ex-spacer" />
                        <span className={`ex-status ${statusCls}`}>{statusTxt}</span>
                      </div>
                      {sl.exists ? (
                        <div className="ex-sub">
                          {fmtSize(sl.sizeBytes)} &middot; built {uptime(sl.lastBuiltUtc)} ago
                        </div>
                      ) : (
                        <div className="ex-sub">no exe in local_builds</div>
                      )}
                      <div className="ex-actions">
                        <button
                          className="ex-btn"
                          onClick={() => setPending({ kind: "buildSlot", slot: sl.slot })}
                          disabled={running || busy}
                        >
                          {buildingSlot === sl.slot ? "Building..." : "Build & start"}
                        </button>
                        <button
                          className="ex-btn ex-danger"
                          onClick={() => setPending({ kind: "deleteSlot", slot: sl.slot })}
                          disabled={!sl.exists || running || busy}
                        >
                          Delete
                        </button>
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </>
        )}
      </main>

      {confirm !== null && (
        <ConfirmDialog
          open
          title={confirm.title}
          message={confirm.message}
          confirmLabel={confirm.confirmLabel}
          busyLabel={confirm.busyLabel}
          danger={confirm.danger}
          onConfirm={confirm.onConfirm}
          onClose={() => setPending(null)}
        />
      )}
    </div>
  );
}

// Turn a pending action into the ConfirmDialog's copy and its confirmed handler. Kept as a plain
// function (not inline JSX) so the three actions read as a single table.
function describePending(
  pending: PendingAction | null,
  actions: {
    killDir: (dir: ExesDirector) => Promise<void>;
    removeSlot: (n: number) => Promise<void>;
    buildStart: (n: number) => Promise<void>;
  },
): {
  title: string;
  message: string;
  confirmLabel: string;
  busyLabel: string;
  danger: boolean;
  onConfirm: () => Promise<void>;
} | null {
  if (pending === null) return null;
  switch (pending.kind) {
    case "kill": {
      const label = pending.dir.slot != null ? `slot ${pending.dir.slot}` : `PID ${pending.dir.pid}`;
      return {
        title: `Kill Director ${label}?`,
        message:
          `This terminates the process (PID ${pending.dir.pid}) and ALL of its running sessions. ` +
          "Unsaved work in those sessions will be lost. This cannot be undone.",
        confirmLabel: "Kill Director",
        busyLabel: "Killing...",
        danger: true,
        onConfirm: () => actions.killDir(pending.dir),
      };
    }
    case "deleteSlot":
      return {
        title: `Delete the slot ${pending.slot} build?`,
        message:
          `This removes local_builds/cc-director${pending.slot}.exe from disk. You can rebuild it ` +
          'later with "Build & start".',
        confirmLabel: "Delete",
        busyLabel: "Deleting...",
        danger: true,
        onConfirm: () => actions.removeSlot(pending.slot),
      };
    case "buildSlot":
      return {
        title: `Build slot ${pending.slot} and launch it?`,
        message:
          `This runs the build script (about a minute) and then starts cc-director${pending.slot}.exe. ` +
          "The slot must not already be running.",
        confirmLabel: "Build & start",
        busyLabel: "Building...",
        danger: false,
        onConfirm: () => actions.buildStart(pending.slot),
      };
  }
}

// The build-slot exe size ("12.3 MB" / "512 KB" / "0 B") - a display helper unique to this page, so it
// stays local. The port, repo basename, and duration helpers this page used to re-implement now come
// from the shared fleet/format module (issue #1261).
function fmtSize(bytes: number): string {
  if (bytes === 0) return "0 B";
  const mb = bytes / (1024 * 1024);
  if (mb >= 1) return `${mb.toFixed(1)} MB`;
  return `${Math.round(bytes / 1024)} KB`;
}
