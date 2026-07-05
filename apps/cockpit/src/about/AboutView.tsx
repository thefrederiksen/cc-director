import { useEffect, useState } from "react";
import { getAbout, type AboutInfo } from "@devthrottle/client-core/about/aboutClient";

// The About page (issue #978, epic #967) - the React port of the Blazor Cockpit About.razor. Read-only
// diagnostics of what this Gateway is running and what is installed on its box, from GET /gateway/about.
// Responsive (CodingStyle.md): renders immediately with a loading state and loads asynchronously; on a
// load failure it shows an explicit error banner (the no-fallback rule).

/** One label/value row in the diagnostics grid. */
function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="abt-row">
      <div className="abt-row-label">{label}</div>
      <div className="abt-row-value">{value}</div>
    </div>
  );
}

// Format the Gateway's ISO server time to the compact "yyyy-MM-dd HH:mm:ss" (UTC) the Blazor page
// showed. When the value is missing or unparseable, show it verbatim rather than fabricating a time.
function formatServerTime(iso: string): string {
  if (iso.length === 0) return "(unknown)";
  const parsed = new Date(iso);
  if (Number.isNaN(parsed.getTime())) return iso;
  const pad = (n: number) => String(n).padStart(2, "0");
  return (
    `${parsed.getUTCFullYear()}-${pad(parsed.getUTCMonth() + 1)}-${pad(parsed.getUTCDate())} ` +
    `${pad(parsed.getUTCHours())}:${pad(parsed.getUTCMinutes())}:${pad(parsed.getUTCSeconds())}`
  );
}

export function AboutView() {
  const [about, setAbout] = useState<AboutInfo | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      try {
        setAbout(await getAbout(controller.signal));
      } catch (err) {
        if (controller.signal.aborted) return;
        setError(err instanceof Error ? err.message : "Failed to load About info");
      }
    })();
    return () => controller.abort();
  }, []);

  // The installed components, sorted case-insensitively by id (matching the Blazor OrderBy).
  const components =
    about === null
      ? []
      : Object.entries(about.installedComponents).sort((a, b) =>
          a[0].localeCompare(b[0], undefined, { sensitivity: "base" }),
        );

  return (
    <div className="page abt">
      <div className="page-head">
        <h1>About DevThrottle</h1>
      </div>
      <p className="abt-lede">What this Gateway is running and what&apos;s installed.</p>

      {error !== null ? (
        <div className="abt-error">Could not load About info from the Gateway: {error}</div>
      ) : about === null ? (
        <p className="abt-loading">Loading...</p>
      ) : (
        <>
          <div className="abt-card">
            <Row label="Product" value={about.product} />
            <Row label="Version" value={about.version} />
            <Row label="Build date" value={about.buildDate ?? "(unknown)"} />
            <Row label="Machine" value={about.machineName} />
            <Row label="Install root" value={about.installRoot} />
            <Row label="Cockpit URL" value={about.cockpitUrl ?? "(Tailscale unavailable)"} />
            <Row label="Gateway time (UTC)" value={formatServerTime(about.serverTime)} />
          </div>

          <h2 className="abt-h2">INSTALLED COMPONENTS</h2>
          <div className="abt-card">
            {components.length === 0 ? (
              <div className="abt-empty">
                (no installed.json - the Gateway may be running from a dev build)
              </div>
            ) : (
              components.map(([id, version]) => <Row key={id} label={id} value={version} />)
            )}
          </div>
        </>
      )}
    </div>
  );
}
