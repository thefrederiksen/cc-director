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
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      setError(null);
      setConsent(await getTelemetryConsent(signal));
    } catch (err) {
      if (signal?.aborted) return;
      setError(err instanceof Error ? err.message : "Failed to load the telemetry setting");
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
    setError(null);
    try {
      setConsent(await setTelemetryConsent(!consent.enabled));
      setSaved(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save the telemetry setting");
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

      {error !== null ? (
        <div className="tel-error">Could not load the telemetry setting from the Gateway: {error}</div>
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
