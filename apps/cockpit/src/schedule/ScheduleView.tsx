import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  createCronJob,
  deleteCronJob,
  getCronJobs,
  getCronRuns,
  runCronJobNow,
  updateCronJob,
  type CronJob,
  type CronRunRecord,
} from "@devthrottle/client-core/schedule/scheduleClient";
import {
  ENDPOINT_STATE_UNREACHABLE_BY_NAME,
  getFleetDirectors,
  getSessionsEnvelope,
  type FleetDirector,
  type MachineError,
} from "@devthrottle/client-core/fleet/fleetClient";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { clockLabel } from "../fleet/format";

// The Schedule page (issue #976, epic #967) - the React port of the Blazor Cockpit Schedule.razor
// (issue #488). The human's window into cron jobs: a pure CLIENT of the Gateway's /cron/jobs surface
// (every create / update / delete / run / toggle goes through client-core to the Gateway, so the
// Cockpit never owns a copy). It lists jobs, shows the selected job's run history, and drives the
// create/edit modal with its own roomy Director picker dialog (the machine picker, #495). Reads and
// writes are same-origin, root-relative through the Gateway front door - never a Director address.
//
// Polling matches the Blazor page: the job list refreshes every 5s; a refresh never blocks the modal.
const POLL_MS = 5000;

// The create/edit form state, kept together so open/close/reset is one object (mirrors the Blazor
// _f* fields). enabled + preventOverlap have no form control but are preserved across an edit so
// renaming/rescheduling a disabled job never silently re-arms it (Blazor QA #488).
interface FormState {
  editingId: string | null;
  name: string;
  machine: string;
  repoPath: string;
  actionKind: "worklist" | "seed";
  workListName: string;
  seed: string;
  scheduleKind: "oneOff" | "recurring";
  cron: string;
  runAt: string;
  timeZone: string;
  notifyOn: "none" | "always" | "failure";
  notifyWebhookUrl: string;
  enabled: boolean;
  preventOverlap: boolean;
}

// Per-field validation messages for the create/edit form. A field is present here only when it is
// invalid, so an empty object means "minimally valid to submit" (issue #1027). We validate exactly
// the fields the Gateway needs to run a job: a name, a target machine, a repository path, and a
// schedule (a cron expression when recurring, or a run-at instant when one-off).
interface FormErrors {
  name?: string;
  machine?: string;
  repoPath?: string;
  schedule?: string;
}

function validateForm(f: FormState): FormErrors {
  const errors: FormErrors = {};
  if (f.name.trim().length === 0) errors.name = "Enter a name so you can find this job in the list.";
  if (f.machine.trim().length === 0) errors.machine = "Choose the machine this job runs on.";
  if (f.repoPath.trim().length === 0) errors.repoPath = "Enter the repository path the session opens in.";
  if (f.scheduleKind === "recurring") {
    if (f.cron.trim().length === 0) errors.schedule = "Enter a 5-field cron expression, for example 0 0 * * *.";
  } else {
    if (f.runAt.trim().length === 0) errors.schedule = "Enter the local date and time to run once.";
  }
  return errors;
}

const EMPTY_FORM: FormState = {
  editingId: null,
  name: "",
  machine: "",
  repoPath: "",
  actionKind: "worklist",
  workListName: "",
  seed: "",
  scheduleKind: "oneOff",
  cron: "",
  runAt: "",
  timeZone: "",
  notifyOn: "none",
  notifyWebhookUrl: "",
  enabled: true,
  preventOverlap: true,
};

export function ScheduleView() {
  const [jobs, setJobs] = useState<CronJob[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [runs, setRuns] = useState<CronRunRecord[]>([]);
  const [lastRefresh, setLastRefresh] = useState<Date | null>(null);
  const [lastError, setLastError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  // Director-picker source data (the machine picker's live preview).
  const [directors, setDirectors] = useState<FleetDirector[]>([]);
  const [sessions, setSessions] = useState<SessionDto[]>([]);
  const [machineErrors, setMachineErrors] = useState<MachineError[]>([]);
  const [machineFilter, setMachineFilter] = useState("");
  const [showDirectorPicker, setShowDirectorPicker] = useState(false);

  // Create/edit modal state.
  const [showForm, setShowForm] = useState(false);
  const [saving, setSaving] = useState(false);
  const [form, setForm] = useState<FormState>(EMPTY_FORM);
  const [formError, setFormError] = useState<string | null>(null);

  // selectedRef mirrors selectedId so the poll can refetch the open job's runs without re-subscribing.
  const selectedRef = useRef<string | null>(null);
  selectedRef.current = selectedId;

  const refresh = useCallback(async (signal?: AbortSignal) => {
    try {
      const fresh = await getCronJobs(signal);
      setJobs(fresh);
      setLastError(null);
      setLastRefresh(new Date());
      const sel = selectedRef.current;
      if (sel !== null && fresh.some((j) => j.id === sel)) {
        try {
          setRuns(await getCronRuns(sel, signal));
        } catch {
          /* run-history refresh is non-fatal; keep the last list */
        }
      }
    } catch (err) {
      if (signal?.aborted === true) return;
      setLastError(gatewayErrorMessage(err));
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void refresh(controller.signal);
    const timer = window.setInterval(() => void refresh(controller.signal), POLL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [refresh]);

  const selectJob = useCallback(async (id: string) => {
    setSelectedId(id);
    setActionError(null);
    try {
      setRuns(await getCronRuns(id));
    } catch {
      setRuns([]);
    }
  }, []);

  const loadDirectors = useCallback(async () => {
    try {
      const [dirs, env] = await Promise.all([getFleetDirectors(), getSessionsEnvelope()]);
      setDirectors(dirs);
      setSessions(env.sessions);
      setMachineErrors(env.machineErrors);
    } catch {
      /* the picker degrades to "no machines known" rather than blocking the form */
    }
  }, []);

  const openCreate = useCallback(() => {
    setForm(EMPTY_FORM);
    setFormError(null);
    setShowForm(true);
    void loadDirectors();
  }, [loadDirectors]);

  const openEdit = useCallback(
    (job: CronJob) => {
      setForm({
        editingId: job.id,
        name: job.name,
        machine: job.target.machine,
        repoPath: job.action.repoPath,
        actionKind: job.action.workListName && job.action.workListName.length > 0 ? "worklist" : "seed",
        workListName: job.action.workListName ?? "",
        seed: job.action.seed,
        scheduleKind: job.scheduleKind.toLowerCase() === "recurring" ? "recurring" : "oneOff",
        cron: job.cronExpression ?? "",
        runAt: job.runAt ?? "",
        timeZone: job.timeZoneId,
        notifyOn: normalizeNotify(job.notifyOn),
        notifyWebhookUrl: job.notifyWebhookUrl ?? "",
        enabled: job.enabled,
        preventOverlap: job.preventOverlap,
      });
      setFormError(null);
      setShowForm(true);
      void loadDirectors();
    },
    [loadDirectors],
  );

  const buildFromForm = useCallback((f: FormState): CronJob => {
    return {
      id: "",
      name: f.name.trim(),
      enabled: f.enabled,
      scheduleKind: f.scheduleKind,
      cronExpression: f.scheduleKind === "recurring" ? f.cron.trim() : null,
      runAt: f.scheduleKind === "oneOff" ? f.runAt.trim() : null,
      timeZoneId: f.timeZone.trim(),
      target: { machine: f.machine.trim() },
      action: {
        repoPath: f.repoPath.trim(),
        seed: f.actionKind === "seed" ? f.seed.trim() : "",
        workListName: f.actionKind === "worklist" ? f.workListName.trim() : null,
      },
      preventOverlap: f.preventOverlap,
      notifyOn: f.notifyOn,
      notifyWebhookUrl:
        f.notifyOn !== "none" && f.notifyWebhookUrl.trim().length > 0 ? f.notifyWebhookUrl.trim() : null,
    };
  }, []);

  // Live per-field validation of the open form. Empty object => minimally valid (issue #1027).
  const formErrors = useMemo(() => validateForm(form), [form]);
  const formValid = Object.keys(formErrors).length === 0;

  const save = useCallback(async () => {
    // The Create/Save button is disabled while invalid, but guard here too so no code path can POST
    // an empty job (issue #1027).
    if (!formValid) return;
    setSaving(true);
    setFormError(null);
    try {
      const dto = buildFromForm(form);
      if (form.editingId === null) {
        await createCronJob(dto);
      } else {
        await updateCronJob(form.editingId, dto);
      }
      setShowForm(false);
      await refresh();
    } catch (err) {
      // Surface the Gateway's message (incl. a 400 for an invalid cron) inline in the form.
      setFormError(gatewayErrorMessage(err));
    } finally {
      setSaving(false);
    }
  }, [buildFromForm, form, formValid, refresh]);

  const runNow = useCallback(
    async (job: CronJob) => {
      setActionError(null);
      try {
        await runCronJobNow(job.id);
        setSelectedId(job.id);
        await refresh();
        setRuns(await getCronRuns(job.id));
      } catch (err) {
        setActionError(`Run now failed: ${gatewayErrorMessage(err)}`);
      }
    },
    [refresh],
  );

  const remove = useCallback(
    async (job: CronJob) => {
      setActionError(null);
      try {
        await deleteCronJob(job.id);
        if (selectedRef.current === job.id) {
          setSelectedId(null);
          setRuns([]);
        }
        await refresh();
      } catch (err) {
        setActionError(`Delete failed: ${gatewayErrorMessage(err)}`);
      }
    },
    [refresh],
  );

  const toggleEnabled = useCallback(
    async (job: CronJob) => {
      setActionError(null);
      try {
        // Send only the mutable job fields (issue #1027) - never echo back the Gateway's own
        // computed fields (nextRunUtc / lastFiredUtc / lastStatus / createdUtc). This preserves
        // every field the toggle does not change, notify settings included, so flipping enabled
        // never silently clears them (Blazor #622), while keeping read-only scheduling state on the
        // Gateway.
        await updateCronJob(job.id, toMutableDto(job, { enabled: !job.enabled }));
        await refresh();
      } catch (err) {
        setActionError(`Toggle failed: ${gatewayErrorMessage(err)}`);
      }
    },
    [refresh],
  );

  // ---- Director picker (the machine picker, #495) ----
  const machines = useMemo(() => {
    const names = directors
      .map((d) => d.machineName ?? "")
      .filter((m) => m.trim().length > 0);
    const distinct = Array.from(new Set(names.map((m) => m)));
    // Case-insensitive de-dup + sort, matching the Blazor Machines() helper.
    const seen = new Map<string, string>();
    for (const m of distinct) {
      const key = m.toLowerCase();
      if (!seen.has(key)) seen.set(key, m);
    }
    return Array.from(seen.values()).sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase()));
  }, [directors]);

  const filteredMachines = useMemo(() => {
    const f = machineFilter.trim().toLowerCase();
    if (f.length === 0) return machines;
    return machines.filter((m) => m.toLowerCase().includes(f));
  }, [machines, machineFilter]);

  const isUnreachable = useCallback(
    (d: FleetDirector): boolean =>
      d.advertisedEndpointState === ENDPOINT_STATE_UNREACHABLE_BY_NAME ||
      machineErrors.some((e) => (e.directorId ?? "").toLowerCase() === (d.directorId ?? "").toLowerCase()),
    [machineErrors],
  );

  const selectedName = jobs.find((j) => j.id === selectedId)?.name ?? "";

  return (
    <div className="sched">
      <header className="sched-head">
        <h1 className="sched-title">Schedule</h1>
        <span className="sched-sub">
          {jobs.length} cron job{jobs.length === 1 ? "" : "s"}
        </span>
        <span className="sched-refreshed">
          {lastRefresh === null ? "connecting..." : `updated ${clockLabel(lastRefresh)}`}
        </span>
        <span className="sched-spacer" />
        <button className="sched-btn primary" onClick={openCreate}>
          New cron job
        </button>
      </header>

      {lastError !== null && <div className="sched-banner-error">Gateway error: {lastError}</div>}

      {jobs.length === 0 && lastError === null && lastRefresh !== null && (
        <div className="sched-empty">
          No cron jobs yet. Create one to schedule a session or a work-list drain on a machine.
        </div>
      )}

      {jobs.length > 0 && (
        <>
          <table className="sched-tbl">
            <thead>
              <tr>
                <th>Name</th>
                <th>Target</th>
                <th>Runs</th>
                <th>Schedule</th>
                <th>Next run</th>
                <th>Last run</th>
                <th>Status</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {jobs.map((job) => (
                <tr
                  key={job.id}
                  className={`sched-row${job.id === selectedId ? " sel" : ""}`}
                  onClick={() => void selectJob(job.id)}
                >
                  <td>
                    <span className="sched-cell-name">{job.name}</span>
                  </td>
                  <td className="mono">{job.target.machine}</td>
                  <td>{runsLabel(job)}</td>
                  <td>
                    {scheduleLabel(job)}
                    {notifyEnabled(job) && (
                      <span className="sched-notify-badge" title={notifyTitle(job)}>
                        {notifyLabel(job)}
                      </span>
                    )}
                  </td>
                  <td className="mono">{fmtUtc(job.nextRunUtc)}</td>
                  <td className="dim">
                    {job.lastFiredUtc ? fmtUtc(job.lastFiredUtc) : "never"}{" "}
                    {job.lastStatus && job.lastStatus.length > 0 ? `(${job.lastStatus})` : ""}
                  </td>
                  <td>
                    <button
                      className={`sched-chip ${job.enabled ? "enabled" : "disabled"}`}
                      title="Toggle enabled"
                      onClick={(e) => {
                        e.stopPropagation();
                        void toggleEnabled(job);
                      }}
                    >
                      {job.enabled ? "Enabled" : "Disabled"}
                    </button>
                  </td>
                  <td className="sched-actions" onClick={(e) => e.stopPropagation()}>
                    <button className="sched-linkbtn" onClick={() => void runNow(job)}>
                      Run now
                    </button>
                    <button className="sched-linkbtn" onClick={() => openEdit(job)}>
                      Edit
                    </button>
                    <button className="sched-linkbtn del" onClick={() => void remove(job)}>
                      Delete
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          {actionError !== null && <div className="sched-banner-error">{actionError}</div>}
        </>
      )}

      {selectedId !== null && (
        <>
          <div className="sched-sec-head">
            <h2>Run history</h2>
            <span className="sched-hint">
              {selectedName} - newest first. Infra status (did it start) is separate from task status
              (did it finish).
            </span>
          </div>

          {runs.length === 0 ? (
            <div className="sched-empty">No runs recorded yet for this job.</div>
          ) : (
            <table className="sched-tbl">
              <thead>
                <tr>
                  <th>Scheduled</th>
                  <th>Fired</th>
                  <th>Target</th>
                  <th>Session</th>
                  <th>Infra</th>
                  <th>Task</th>
                </tr>
              </thead>
              <tbody>
                {runs.map((r, i) => (
                  <tr key={`${r.scheduledUtc}/${r.firedUtc}/${i}`}>
                    <td className="mono">{fmtUtc(r.scheduledUtc)}</td>
                    <td className="mono">{fmtUtc(r.firedUtc)}</td>
                    <td className="mono">{r.targetDirectorId}</td>
                    <td className="mono dim">{r.sessionId && r.sessionId.length > 0 ? r.sessionId : "-"}</td>
                    <td>
                      <span className={`sched-st ${infraClass(r.infraStatus)}`}>{r.infraStatus}</span>
                    </td>
                    <td className="dim">{r.taskStatus}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      )}

      {showForm && (
        <div className="sched-modal-backdrop" onClick={() => setShowForm(false)}>
          <div className="sched-modal" onClick={(e) => e.stopPropagation()}>
            <div className="sched-modal-head">{form.editingId === null ? "New cron job" : "Edit cron job"}</div>
            <div className="sched-modal-body">
              <div className="sched-fld">
                <label className="sched-fld-label">Name</label>
                <input
                  className={formErrors.name ? "invalid" : undefined}
                  value={form.name}
                  placeholder="e.g. Tonight - drain work list"
                  onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
                />
                {formErrors.name && <div className="sched-fld-err">{formErrors.name}</div>}
              </div>

              <div className="sched-fld">
                <label className="sched-fld-label">Run on (machine)</label>
                <div className="sched-dpick-field">
                  <span className={`sched-dpick-chosen${form.machine.length === 0 ? " none" : ""}`}>
                    {form.machine.length === 0 ? "No machine selected" : form.machine}
                  </span>
                  <button
                    type="button"
                    className="sched-btn dpick-choose"
                    onClick={() => {
                      setMachineFilter("");
                      setShowDirectorPicker(true);
                    }}
                  >
                    {form.machine.length === 0 ? "Choose..." : "Change"}
                  </button>
                </div>
                {formErrors.machine && <div className="sched-fld-err">{formErrors.machine}</div>}
              </div>

              <div className="sched-fld">
                <label className="sched-fld-label">Repository path</label>
                <input
                  className={`mono${formErrors.repoPath ? " invalid" : ""}`}
                  value={form.repoPath}
                  placeholder="C:\repos\devthrottle"
                  onChange={(e) => setForm((f) => ({ ...f, repoPath: e.target.value }))}
                />
                {formErrors.repoPath && <div className="sched-fld-err">{formErrors.repoPath}</div>}
              </div>

              <div className="sched-fld">
                <label className="sched-fld-label">What to run</label>
                <select
                  value={form.actionKind}
                  onChange={(e) => setForm((f) => ({ ...f, actionKind: e.target.value as "worklist" | "seed" }))}
                >
                  <option value="worklist">Run a named work list</option>
                  <option value="seed">Run a skill / prompt</option>
                </select>
              </div>

              {form.actionKind === "worklist" ? (
                <div className="sched-fld">
                  <label className="sched-fld-label">Work list name</label>
                  <input
                    value={form.workListName}
                    placeholder="e.g. Tonight"
                    onChange={(e) => setForm((f) => ({ ...f, workListName: e.target.value }))}
                  />
                </div>
              ) : (
                <div className="sched-fld">
                  <label className="sched-fld-label">Skill / prompt</label>
                  <input
                    className="mono"
                    value={form.seed}
                    placeholder="/implementation-loop 312"
                    onChange={(e) => setForm((f) => ({ ...f, seed: e.target.value }))}
                  />
                </div>
              )}

              <div className="sched-fld">
                <label className="sched-fld-label">Schedule</label>
                <select
                  value={form.scheduleKind}
                  onChange={(e) => setForm((f) => ({ ...f, scheduleKind: e.target.value as "oneOff" | "recurring" }))}
                >
                  <option value="oneOff">Run once</option>
                  <option value="recurring">Recurring (cron)</option>
                </select>
              </div>

              {form.scheduleKind === "recurring" ? (
                <div className="sched-fld">
                  <label className="sched-fld-label">Cron expression (5-field)</label>
                  <input
                    className={`mono${formErrors.schedule ? " invalid" : ""}`}
                    value={form.cron}
                    placeholder="0 0 * * *"
                    onChange={(e) => setForm((f) => ({ ...f, cron: e.target.value }))}
                  />
                  {formErrors.schedule && <div className="sched-fld-err">{formErrors.schedule}</div>}
                </div>
              ) : (
                <div className="sched-fld">
                  <label className="sched-fld-label">Run at (local time)</label>
                  <input
                    className={`mono${formErrors.schedule ? " invalid" : ""}`}
                    value={form.runAt}
                    placeholder="2026-06-18T00:00:00"
                    onChange={(e) => setForm((f) => ({ ...f, runAt: e.target.value }))}
                  />
                  {formErrors.schedule && <div className="sched-fld-err">{formErrors.schedule}</div>}
                </div>
              )}

              <div className="sched-fld">
                <label className="sched-fld-label">Time zone</label>
                <input
                  className="mono"
                  value={form.timeZone}
                  placeholder="America/Chicago"
                  onChange={(e) => setForm((f) => ({ ...f, timeZone: e.target.value }))}
                />
              </div>

              <div className="sched-fld">
                <label className="sched-fld-label">Notify when run completes</label>
                <select
                  value={form.notifyOn}
                  onChange={(e) =>
                    setForm((f) => ({ ...f, notifyOn: e.target.value as "none" | "always" | "failure" }))
                  }
                >
                  <option value="none">Off (no notification)</option>
                  <option value="always">Always (success or failure)</option>
                  <option value="failure">Only on failure</option>
                </select>
              </div>

              {form.notifyOn !== "none" && (
                <div className="sched-fld">
                  <label className="sched-fld-label">Webhook URL (optional)</label>
                  <input
                    className="mono"
                    value={form.notifyWebhookUrl}
                    placeholder="example.com/hook (https)"
                    onChange={(e) => setForm((f) => ({ ...f, notifyWebhookUrl: e.target.value }))}
                  />
                </div>
              )}

              {formError !== null && <div className="sched-modal-error">{formError}</div>}
            </div>
            <div className="sched-modal-foot">
              <button className="sched-btn" onClick={() => setShowForm(false)}>
                Cancel
              </button>
              <button
                className="sched-btn primary"
                disabled={saving || !formValid}
                title={formValid ? undefined : "Fill in the highlighted fields to continue"}
                onClick={() => void save()}
              >
                {saving ? "Saving..." : form.editingId === null ? "Create job" : "Save"}
              </button>
            </div>
          </div>
        </div>
      )}

      {showDirectorPicker && (
        <div className="sched-modal-backdrop dpicker-over" onClick={() => setShowDirectorPicker(false)}>
          <div className="sched-modal dpicker-modal" onClick={(e) => e.stopPropagation()}>
            <div className="sched-modal-head">Choose a machine</div>
            <div className="sched-modal-body">
              <input
                className="sched-dpicker-filter"
                placeholder="filter machine"
                value={machineFilter}
                onChange={(e) => setMachineFilter(e.target.value)}
              />
              {machines.length === 0 ? (
                <div className="sched-dpick-empty">No machines known to this Gateway yet.</div>
              ) : (
                <div className="sched-dpick">
                  {filteredMachines.map((machine) => {
                    const dirs = directors.filter(
                      (d) => (d.machineName ?? "").toLowerCase() === machine.toLowerCase(),
                    );
                    const reachable = dirs.filter((d) => !isUnreachable(d)).length;
                    const machineSessions = sessions.filter(
                      (s) => (s.machineName ?? "").toLowerCase() === machine.toLowerCase(),
                    );
                    const needs = machineSessions.filter((s) => s.needsYouSince != null).length;
                    const shown = [...machineSessions]
                      .sort((a, b) => Number(a.sortOrder ?? 0) - Number(b.sortOrder ?? 0))
                      .slice(0, 3);
                    return (
                      <button
                        key={machine}
                        type="button"
                        className={`sched-dcard${form.machine === machine ? " sel" : ""}`}
                        onClick={() => {
                          setForm((f) => ({ ...f, machine }));
                          setShowDirectorPicker(false);
                        }}
                      >
                        <div className="sched-dcard-top">
                          <span className={`sched-dot ${reachable > 0 ? "ok" : "dead"}`} />
                          <span className="sched-dname">{machine}</span>
                          <span className="sched-dmeta">
                            {reachable} director{reachable === 1 ? "" : "s"} running
                          </span>
                          {needs > 0 && <span className="sched-dneeds">{needs} NEEDS YOU</span>}
                        </div>
                        {reachable === 0 ? (
                          <div className="sched-dsub dempty">no Director running - one will be launched on demand</div>
                        ) : machineSessions.length === 0 ? (
                          <div className="sched-dsub dempty">idle - 0 sessions</div>
                        ) : (
                          <>
                            {shown.map((s) => (
                              <div className="sched-dsess" key={s.sessionId}>
                                <span className={`sched-sdot ${sessClass(s)}`} />
                                <span className="sched-sname">{sessName(s)}</span>
                                <span className={`sched-sstate ${sessClass(s)}`}>{sessState(s)}</span>
                              </div>
                            ))}
                            {machineSessions.length > 3 && (
                              <div className="sched-dsub dempty">+{machineSessions.length - 3} more</div>
                            )}
                          </>
                        )}
                      </button>
                    );
                  })}
                </div>
              )}
            </div>
            <div className="sched-modal-foot">
              <button className="sched-btn" onClick={() => setShowDirectorPicker(false)}>
                Cancel
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

// Reduce a full CronJob (as read back from the Gateway, carrying its computed nextRunUtc /
// lastFiredUtc / lastStatus / createdUtc) to a clean create/update DTO of ONLY the mutable fields,
// applying overrides last. The enable/disable toggle routes through this so a plain toggle never
// echoes the Gateway's read-only scheduling state back to it (issue #1027) - the same clean shape
// buildFromForm produces for the edit path.
function toMutableDto(job: CronJob, overrides: Partial<CronJob>): CronJob {
  return {
    id: "",
    name: job.name,
    enabled: job.enabled,
    scheduleKind: job.scheduleKind,
    cronExpression: job.cronExpression ?? null,
    runAt: job.runAt ?? null,
    timeZoneId: job.timeZoneId,
    target: { machine: job.target.machine },
    action: {
      repoPath: job.action.repoPath,
      seed: job.action.seed,
      workListName: job.action.workListName ?? null,
    },
    preventOverlap: job.preventOverlap,
    notifyOn: job.notifyOn,
    notifyWebhookUrl: job.notifyWebhookUrl ?? null,
    ...overrides,
  };
}

// ---- display helpers (faithful ports of the Blazor private helpers) ----

function normalizeNotify(value: string | null | undefined): "none" | "always" | "failure" {
  const v = (value ?? "").toLowerCase();
  return v === "always" || v === "failure" ? v : "none";
}

function notifyEnabled(j: CronJob): boolean {
  return j.notifyOn.length > 0 && j.notifyOn.toLowerCase() !== "none";
}

function notifyLabel(j: CronJob): string {
  return j.notifyOn.toLowerCase() === "failure" ? "notify: failures" : "notify: always";
}

function notifyTitle(j: CronJob): string {
  return !j.notifyWebhookUrl || j.notifyWebhookUrl.length === 0
    ? "Run-complete notification rides the fleet channel"
    : `Run-complete notification + webhook: ${j.notifyWebhookUrl}`;
}

function scheduleLabel(j: CronJob): string {
  return j.scheduleKind.toLowerCase() === "recurring"
    ? j.cronExpression ?? "(no cron)"
    : `once @ ${j.runAt ?? ""}`;
}

function runsLabel(j: CronJob): string {
  return !j.action.workListName || j.action.workListName.length === 0
    ? `skill ${j.action.seed}`
    : `work list ${j.action.workListName}`;
}

// "yyyy-MM-dd HH:mm 'UTC'" from an ISO UTC timestamp, matching the Blazor FmtUtc. "-" when absent.
function fmtUtc(iso: string | null | undefined): string {
  if (!iso || iso.length === 0) return "-";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "-";
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ${pad(d.getUTCHours())}:${pad(
    d.getUTCMinutes(),
  )} UTC`;
}

function infraClass(infra: string): string {
  if (infra === "started" || infra === "worklist-started") return "ok";
  if (infra === "catch-up") return "warn";
  if (infra === "not-started") return "err";
  if (infra.startsWith("worklist-") && infra !== "worklist-started") return "warn";
  return "dim";
}

// The Director-picker session preview vocabulary (Blazor SessName / SessState / SessClass).
function sessName(s: SessionDto): string {
  const name = s.name ?? "";
  if (name.trim().length > 0) return name;
  const sid = s.sessionId ?? "";
  return sid.length > 8 ? sid.slice(0, 8) : sid;
}

function sessState(s: SessionDto): string {
  if (s.needsYouSince != null) return "needs you";
  const state = s.activityState ?? "";
  return state.trim().length === 0 ? "idle" : state.toLowerCase();
}

function sessClass(s: SessionDto): string {
  if (s.needsYouSince != null) return "needs";
  return (s.activityState ?? "").toLowerCase() === "working" ? "run" : "idle";
}
