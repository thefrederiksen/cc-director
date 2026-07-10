import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { getGatewayHealth, gatewayErrorMessage, type GatewayHealth } from "@devthrottle/client-core/api/client";

export function About() {
  const [health, setHealth] = useState<GatewayHealth | null>(null);
  const [error, setError] = useState<string | null>(null);
  const bundle = useMemo(() => currentBundleName(), []);
  const sw = serviceWorkerState();
  const displayMode = isStandalone() ? "Installed PWA" : "Browser tab";

  useEffect(() => {
    const controller = new AbortController();
    getGatewayHealth(controller.signal)
      .then((h) => {
        setHealth(h);
        setError(null);
      })
      .catch((err) => {
        if (!controller.signal.aborted) setError(gatewayErrorMessage(err));
      });
    return () => controller.abort();
  }, []);

  const version = health?.version ?? "Loading...";
  const sha = shortSha(version);

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>About</h1>
      </header>

      {error !== null && (
        <div className="banner banner-error" role="alert">
          {error}
        </div>
      )}

      <section className="about-panel" aria-label="Application version">
        <div className="about-product">DevThrottle Mobile</div>
        <div className="about-version">{version}</div>
        {sha !== "" && <div className="about-sha">Build {sha}</div>}
      </section>

      <section className="about-list" aria-label="Runtime details">
        <AboutRow label="Mobile bundle" value={bundle} />
        <AboutRow label="Gateway status" value={health?.status ?? "Loading..."} />
        <AboutRow label="Gateway time" value={formatTime(health?.serverTime)} />
        <AboutRow label="Directors" value={health ? String(health.directors) : "Loading..."} />
        <AboutRow label="Sessions" value={health ? String(health.sessions) : "Loading..."} />
        <AboutRow label="Service worker" value={sw} />
        <AboutRow label="Display" value={displayMode} />
      </section>
    </div>
  );
}

function AboutRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="about-row">
      <span className="about-label">{label}</span>
      <span className="about-value">{value}</span>
    </div>
  );
}

function currentBundleName(): string {
  if (typeof document === "undefined") return "unknown";
  const scripts = Array.from(document.scripts)
    .map((s) => s.getAttribute("src") ?? "")
    .filter((src) => src.includes("/assets/") && src.endsWith(".js"));
  const src = scripts.at(-1);
  if (!src) return "unknown";
  return src.substring(src.lastIndexOf("/") + 1);
}

function serviceWorkerState(): string {
  if (typeof navigator === "undefined" || !("serviceWorker" in navigator)) return "not supported";
  return navigator.serviceWorker.controller ? "active" : "not controlling yet";
}

function isStandalone(): boolean {
  if (typeof window === "undefined") return false;
  const nav = navigator as Navigator & { standalone?: boolean };
  return window.matchMedia("(display-mode: standalone)").matches || Boolean(nav.standalone);
}

function shortSha(version: string): string {
  const plus = version.indexOf("+");
  if (plus < 0) return "";
  return version.substring(plus + 1, plus + 8);
}

function formatTime(value: string | undefined): string {
  if (!value) return "Loading...";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}
