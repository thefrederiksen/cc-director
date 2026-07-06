import { useCallback, useEffect, useState } from "react";
import {
  getTelemetryConsent,
  setTelemetryConsent,
  type TelemetryConsent,
} from "@devthrottle/client-core/telemetry/telemetryClient";

// The Telemetry page (issue #978, epic #967) - the React port of the Blazor Cockpit Telemetry.razor
// (#649). One fleet-wide setting, managed on the Gateway: the richer-usage-telemetry consent (opt-out,
// default ON). The page READS it from GET /gateway/telemetry-consent and toggles it via PUT; the
// always-on sign-in / startup auth-floor events are never gated by it. Responsive (CodingStyle.md): the
// page renders immediately with a loading state and loads the value asynchronously. On a load or save
// failure it shows an explicit error (the no-fallback rule), never a fabricated "off" state.
export function TelemetryView() {
  const [consent, setConsent] = useState<TelemetryConsent | null>(null);
  // Load and save failures are DISTINCT states (issue #1028): a failed load leaves us with no value to
  // show, so it replaces the card; a failed SAVE must keep the toggle on screen so the user can retry
  // in place. Sharing one `error` between them made a save failure blank the whole card with a
  // mislabeled "could not load" banner.
  const [loadError, setLoadError] = useState(false);
  const [saveError, setSaveError] = useState(false);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      setLoadError(false);
      setConsent(await getTelemetryConsent(signal));
    } catch {
      if (signal?.aborted) return;
      // The message is fixed friendly copy with a Retry button below - never the raw endpoint string.
      setLoadError(true);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  const toggle = async () => {
    if (busy || consent === null) return;
    setBusy(true); // immediate visual feedback: the button flips to "Saving..." before the call
    setSaved(false);
    setSaveError(false);
    try {
      setConsent(await setTelemetryConsent(!consent.enabled));
      setSaved(true);
    } catch {
      // Keep the toggle visible; surface an inline "couldn't save" notice with a retry (not a
      // page-level load-error banner that would hide the control).
      setSaveError(true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="page tel">
      <div className="page-head">
        <h1>Usage Telemetry</h1>
      </div>
      <p className="tel-lede">
        One fleet-wide setting, managed on the Gateway. It controls the richer, anonymous usage telemetry
        across every Director. The always-on sign-in and startup events are not affected by it.
      </p>

      {loadError ? (
        <div className="tel-error">
          <span>Could not load the telemetry setting from the Gateway.</span>
          <button className="tel-retry" onClick={() => void load()}>Retry</button>
        </div>
      ) : consent === null ? (
        <p className="tel-loading">Loading...</p>
      ) : (
        <>
          <div className="tel-card">
            <div className="tel-state">
              <span className={consent.enabled ? "tel-dot on" : "tel-dot"} />
              <span className={consent.enabled ? "tel-state-label on" : "tel-state-label"}>
                Richer usage telemetry is {consent.enabled ? "ON" : "OFF"}
              </span>
            </div>
            <p className="tel-explain">
              When on, Directors report anonymous usage events (event names and timestamps only - never
              your code, prompts, or credentials). When off, those events stop fleet-wide. Sign-in and
              Director-startup events always flow so the account keeps working.
            </p>
            <div className="tel-actions">
              <button className="tel-toggle" onClick={() => void toggle()} disabled={busy}>
                {busy ? "Saving..." : consent.enabled ? "Turn telemetry off" : "Turn telemetry on"}
              </button>
              <span className="tel-note">Applies to the whole fleet immediately.</span>
            </div>

            {saveError && (
              <div className="tel-saveerror" role="alert">
                <span>
                  Couldn't save the change - the Gateway did not apply it. The setting is unchanged; try
                  again.
                </span>
                <button className="tel-retry" onClick={() => void toggle()} disabled={busy}>
                  {busy ? "Saving..." : "Try again"}
                </button>
              </div>
            )}
          </div>

          {saved && (
            <div className="tel-saved">
              Saved. The fleet-wide telemetry setting is now {consent.enabled ? "ON" : "OFF"}.
            </div>
          )}
        </>
      )}
    </div>
  );
}
