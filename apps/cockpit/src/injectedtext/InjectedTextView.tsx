import { useEffect, useMemo, useState } from "react";
import {
  getInjectedText,
  setInjectedText,
  type InjectedText,
} from "@devthrottle/client-core/settings/injectedText";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { bannerFor, fleetCommandsWarning, validateTemplate } from "./injectedTextState";

// The Injected text page: the whole of what DevThrottle puts in front of an agent at the start of a
// session, and the controls to read it, replace it, or go back to ours. The setting is GATEWAY-OWNED, so
// this one page governs every machine the user runs a Director on. Responsive (CodingStyle.md): renders
// immediately with a loading line, loads asynchronously, and shows an explicit error banner on failure
// (the no-fallback rule - it never fabricates a state).
//
// YOURS OR OURS, NEVER A MERGE, and the one thing the user must never be wrong about is WHICH is live -
// so it is a full-width banner that changes colour, not a checkbox someone can misread.
export function InjectedTextView() {
  const [data, setData] = useState<InjectedText | null>(null);
  const [error, setError] = useState<string | null>(null);

  // The editor surface. `editing` means the textarea is the active, editable "your text"; otherwise the
  // page shows ours as a read-only preview. `draft` is what is in the textarea while editing.
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");
  const [showOurs, setShowOurs] = useState(false);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      try {
        const loaded = await getInjectedText(controller.signal);
        setData(loaded);
        setEditing(loaded.useYours);
        setDraft(loaded.yours ?? "");
      } catch (e) {
        if (!controller.signal.aborted) setError(gatewayErrorMessage(e));
      }
    })();
    return () => controller.abort();
  }, []);

  const dirty = data !== null && editing && draft !== (data.yours ?? "");
  const editingUnsaved = data !== null && editing && (dirty || !data.useYours);
  const templateProblem = editing ? validateTemplate(draft) : null;
  const fleetWarning = editing ? fleetCommandsWarning(draft) : null;
  const banner = useMemo(
    () => bannerFor(data?.useYours ?? false, editingUnsaved),
    [data?.useYours, editingUnsaved],
  );

  if (error !== null) {
    return (
      <div className="page injected-text">
        <div className="page-head">
          <h1>Injected text</h1>
        </div>
        <div className="itx-error" role="alert">
          {error}
        </div>
      </div>
    );
  }

  if (data === null) {
    return (
      <div className="page injected-text">
        <div className="page-head">
          <h1>Injected text</h1>
        </div>
        <div className="itx-loading">Loading...</div>
      </div>
    );
  }

  async function apply(useYours: boolean, yours: string | null, note: string) {
    setBusy(true);
    setMsg(null);
    setError(null);
    try {
      const updated = await setInjectedText(useYours, yours);
      setData(updated);
      setEditing(updated.useYours);
      setDraft(updated.yours ?? "");
      setMsg(note);
    } catch (e) {
      setError(gatewayErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  function writeMyOwn() {
    // Seed a fresh custom text from the user's saved version if they have one, otherwise from ours as a
    // starting point they can edit. Nothing is saved until they click Save.
    setDraft(data!.yours ?? data!.ours);
    setEditing(true);
    setMsg(null);
  }

  function discard() {
    setDraft(data!.yours ?? "");
    setEditing(data!.useYours);
    setMsg(null);
  }

  return (
    <div className="page injected-text">
      <div className="page-head">
        <h1>Injected text</h1>
      </div>

      <p className="itx-intro">
        When DevThrottle starts an agent, it gives the agent this text before your first message. It tells
        the agent which session it is and how to reach the other sessions in your fleet. It is handed over
        through each agent's own documented startup extension point - it is not typed into your terminal,
        and it does not touch anything you type. This is the whole of what DevThrottle adds at the start of
        a session, on every machine you run a Director on.
      </p>

      <div className={`itx-banner itx-banner-${banner.tone}`}>
        <div className="itx-banner-title">{banner.title}</div>
        <div className="itx-banner-detail">{banner.detail}</div>
      </div>

      <div className="itx-actions">
        <button
          type="button"
          className="itx-btn"
          disabled={busy || !data.useYours}
          onClick={() => void apply(false, data.yours, "Now running the DevThrottle text. Your version is kept.")}
          title="Go back to the text DevThrottle ships. Your own version is kept, not deleted."
        >
          Use the DevThrottle text
        </button>
        <button
          type="button"
          className="itx-btn"
          disabled={busy || editingUnsaved}
          onClick={writeMyOwn}
          title="Start from a copy of the DevThrottle text and edit it. You stop receiving our updates to this text."
        >
          Write my own version
        </button>
        {editing && (
          <button
            type="button"
            className="itx-btn"
            onClick={() => setShowOurs((v) => !v)}
            title="Read the version DevThrottle ships today, even while your own is live."
          >
            {showOurs ? "Hide the DevThrottle text" : "Show the current DevThrottle text"}
          </button>
        )}
      </div>

      <div className={`itx-panes ${showOurs ? "itx-panes-split" : ""}`}>
        <div className="itx-pane">
          <div className="itx-pane-label">{editing ? "Your text" : "The DevThrottle text (read-only)"}</div>
          <textarea
            className="itx-editor"
            spellCheck={false}
            readOnly={!editing}
            value={editing ? draft : data.ours}
            onChange={(e) => setDraft(e.target.value)}
          />
        </div>

        {showOurs && (
          <div className="itx-pane">
            <div className="itx-pane-label">The current DevThrottle text</div>
            <textarea className="itx-editor" spellCheck={false} readOnly value={data.ours} />
          </div>
        )}
      </div>

      <p className="itx-hint">
        These stay editable as placeholders and are filled in for each session:{" "}
        {data.placeholders.join("  ")}
      </p>

      {templateProblem !== null && (
        <p className="itx-problem" role="alert">
          {templateProblem}
        </p>
      )}
      {fleetWarning !== null && templateProblem === null && (
        <p className="itx-warn">{fleetWarning}</p>
      )}

      <div className="itx-footer">
        {msg !== null && <span className="itx-msg">{msg}</span>}
        {editing && dirty && (
          <button type="button" className="itx-btn" disabled={busy} onClick={discard}>
            Discard changes
          </button>
        )}
        {editing && (
          <button
            type="button"
            className="itx-btn itx-btn-primary"
            disabled={busy || templateProblem !== null || (!dirty && data.useYours)}
            onClick={() => void apply(true, draft, "Saved. Your agents now receive your text.")}
          >
            {busy ? "Saving..." : "Save my version"}
          </button>
        )}
      </div>
    </div>
  );
}
