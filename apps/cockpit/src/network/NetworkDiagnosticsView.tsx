import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import {
  getNetworkDiag,
  getNetDiagResults,
  getNetDiagRollup,
  measureLatency,
  measureDownload,
  measureUpload,
  postNetDiagResult,
  type NetworkDiag,
  type NetDiagResult,
  type NetDiagRollupBucket,
  type ThroughputResult,
} from "@devthrottle/client-core/api/client";
import { useNetStatus } from "@devthrottle/client-core/net/useNetStatus";
import "./network.css";

// Network Diagnostics (Cockpit): the operator/agent view of network health. Its centerpiece is the
// SERVER-SIDE Tailscale check (GET /diag/network) - per connected device, direct-vs-DERP-relay + latency,
// with no phone needed - which is the one signal that tells "warming up on the relay" apart from
// "genuinely slow". It also lets you test THIS browser's path and shows the recent results users saw.

const DOWNLOAD_BYTES = 4 * 1024 * 1024;
const UPLOAD_BYTES = 2 * 1024 * 1024;
const LATENCY_SAMPLES = 6;

type TestPhase = "idle" | "latency" | "download" | "upload" | "done";

export function NetworkDiagnosticsView() {
  const [diag, setDiag] = useState<NetworkDiag | null>(null);
  const [diagError, setDiagError] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  const [results, setResults] = useState<NetDiagResult[]>([]);
  const [rollup, setRollup] = useState<NetDiagRollupBucket[]>([]);
  const [trendHours, setTrendHours] = useState<24 | 168>(24);
  const status = useNetStatus(); // same authoritative signal as the header pill, so the light always agrees

  const [phase, setPhase] = useState<TestPhase>("idle");
  const [latency, setLatency] = useState<number[] | null>(null);
  const [download, setDownload] = useState<ThroughputResult | null>(null);
  const [upload, setUpload] = useState<ThroughputResult | null>(null);

  const loadDiag = useCallback(async (signal?: AbortSignal) => {
    try {
      setDiagError(false);
      setRefreshing(true);
      const d = await getNetworkDiag(signal);
      setDiag(d);
    } catch {
      if (!signal?.aborted) setDiagError(true);
    } finally {
      setRefreshing(false);
    }
  }, []);

  const loadResults = useCallback(async (signal?: AbortSignal) => {
    try {
      setResults(await getNetDiagResults(signal));
    } catch {
      /* the results list is a nice-to-have; leave it empty on failure */
    }
  }, []);

  const loadRollup = useCallback(async (signal?: AbortSignal) => {
    try {
      setRollup(await getNetDiagRollup(signal));
    } catch {
      /* the trend is a nice-to-have; leave it empty on failure */
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void loadDiag(controller.signal);
    void loadResults(controller.signal);
    void loadRollup(controller.signal);
    return () => controller.abort();
  }, [loadDiag, loadResults, loadRollup]);

  // Derive the trend points from the hourly rollup for the selected window (newest buckets), skipping
  // hours with no data so gaps do not draw a misleading flat line. Percent-direct is the headline; average
  // latency (honestly "average", not median) is secondary.
  const trend = useMemo(() => {
    const recent = rollup.slice(-trendHours);
    const directPct: number[] = [];
    const avgLatency: number[] = [];
    for (const b of recent) {
      const judged = b.directCount + b.relayCount;
      if (judged > 0) directPct.push((b.directCount / judged) * 100);
      if (b.count > 0 && b.sumLatencyMs > 0) avgLatency.push(b.sumLatencyMs / b.count);
    }
    return { directPct, avgLatency };
  }, [rollup, trendHours]);

  const recentThroughput = useMemo(() => {
    const withThroughput = results.find((r) => r.downloadMbps != null || r.uploadMbps != null);
    return withThroughput ?? null;
  }, [results]);

  const testRunning = phase === "latency" || phase === "download" || phase === "upload";
  const medianLatency = useMemo(() => (latency ? median(latency) : null), [latency]);

  async function runSelfTest() {
    setLatency(null);
    setDownload(null);
    setUpload(null);
    try {
      setPhase("latency");
      const timings = await measureLatency(LATENCY_SAMPLES);
      setLatency(timings);
      setPhase("download");
      const dl = await measureDownload(DOWNLOAD_BYTES);
      setDownload(dl);
      setPhase("upload");
      const ul = await measureUpload(UPLOAD_BYTES);
      setUpload(ul);
      setPhase("done");
      void postNetDiagResult({
        route: "",
        latencyMedianMs: median(timings),
        latencyBestMs: Math.min(...timings),
        latencySamples: timings.length,
        downloadMbps: dl.mbps,
        uploadMbps: ul.mbps,
        rating: rate(median(timings), dl.mbps),
        verdict: `Cockpit self-test: ${formatMs(median(timings))} ms, ${formatMbps(dl.mbps)} Mbps down`,
        loadedFrom: typeof window !== "undefined" ? window.location.host : "",
        surface: "cockpit",
      }).catch(() => undefined);
      void loadResults();
    } catch {
      setPhase("idle");
    }
  }

  return (
    <div className="page netdiag">
      <div className="page-head">
        <h1>Network Diagnostics</h1>
        <button type="button" className="netdiag-refresh" onClick={() => void loadDiag()} disabled={refreshing}>
          {refreshing ? "Checking..." : "Refresh"}
        </button>
      </div>

      <p className="netdiag-lede">
        The server-side Tailscale check for this Gateway. For each connected device it shows whether Tailscale
        is on a direct LAN path or relaying through a distant DERP server - the difference between a fast home
        connection and a slow one.
      </p>

      <section className="netdiag-trend" aria-label="Network health trend">
        <div className="netdiag-light-row">
          <span className={`netdiag-light netdiag-light-${status.level}`} aria-hidden="true" />
          <span className="netdiag-light-label">{status.label}</span>
          <span className="netdiag-light-detail">{status.detail}</span>
          <div className="netdiag-window" role="group" aria-label="Trend window">
            <button type="button" className={trendHours === 24 ? "on" : ""} onClick={() => setTrendHours(24)}>24h</button>
            <button type="button" className={trendHours === 168 ? "on" : ""} onClick={() => setTrendHours(168)}>7d</button>
          </div>
        </div>
        <div className="netdiag-trend-grid">
          <TrendCard title="Direct connections" subtitle="percent of the time on a direct path - higher is better">
            <TrendChart values={trend.directPct} yMin={0} yMax={100} color="var(--accent)" unit="%" prominent />
          </TrendCard>
          <TrendCard title="Average latency" subtitle="hourly average round trip - lower is better">
            <TrendChart values={trend.avgLatency} yMin={0} yMax={Math.max(50, ...trend.avgLatency)} color="#e8a33d" unit="ms" />
          </TrendCard>
        </div>
        {recentThroughput && (
          <div className="netdiag-throughput-note">
            Latest speed test: {fmtMbps(recentThroughput.downloadMbps)} down / {fmtMbps(recentThroughput.uploadMbps)} up
            <span className="netdiag-muted"> - throughput is measured only when a speed test runs, so it is not a continuous trend.</span>
          </div>
        )}
      </section>

      <section className="netdiag-section" aria-label="Network health">
        {diagError ? (
          <div className="netdiag-error">Could not read the network diagnostic from the Gateway.</div>
        ) : diag === null ? (
          <div className="netdiag-muted">Loading...</div>
        ) : !diag.tailscaleAvailable ? (
          <div className="netdiag-muted">Tailscale is not installed on the Gateway machine.</div>
        ) : (
          <>
            <div className="netdiag-summary">
              <span>
                Gateway <strong>{diag.selfName ?? "?"}</strong> ({diag.selfTailscaleIp ?? "?"})
              </span>
              <span>backend: {diag.backendState ?? "?"}</span>
              <span>UDP: {yn(diag.udpOk)}</span>
              <span>hard-NAT: {yn(diag.mappingVariesByDestIp)}</span>
              <span>nearest relay: {diag.nearestDerp ?? "?"}</span>
            </div>
            <table className="netdiag-table">
              <thead>
                <tr>
                  <th>Device</th>
                  <th>Tailscale IP</th>
                  <th>OS</th>
                  <th>Online</th>
                  <th>Path</th>
                  <th>Latency</th>
                </tr>
              </thead>
              <tbody>
                {diag.peers.length === 0 && (
                  <tr>
                    <td colSpan={6} className="netdiag-muted">
                      No peers connected.
                    </td>
                  </tr>
                )}
                {diag.peers.map((p) => (
                  <tr key={p.tailscaleIp ?? p.name}>
                    <td>{p.name}</td>
                    <td>{p.tailscaleIp ?? "-"}</td>
                    <td>{p.os ?? "-"}</td>
                    <td>{yn(p.online)}</td>
                    <td>
                      {p.direct === true ? (
                        <span className="netdiag-direct">direct {p.path ?? ""}</span>
                      ) : p.direct === false ? (
                        <span className="netdiag-relay">RELAY {p.path ?? ""}</span>
                      ) : (
                        <span className="netdiag-muted">{p.path ?? "-"}</span>
                      )}
                    </td>
                    <td>{p.latencyMs != null ? `${Math.round(p.latencyMs)} ms` : "-"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {diag.notes.map((n, i) => (
              <div key={i} className="netdiag-note">
                {n}
              </div>
            ))}
          </>
        )}
      </section>

      <section className="netdiag-section" aria-label="Test this browser">
        <h2>Test this browser</h2>
        <button type="button" className="netdiag-run" onClick={runSelfTest} disabled={testRunning}>
          {testRunning ? testPhaseLabel(phase) : latency ? "Run again" : "Run speed test"}
        </button>
        {(latency || download || upload) && (
          <div className="netdiag-metrics">
            <Metric label="Latency" value={medianLatency != null ? `${formatMs(medianLatency)} ms` : "..."} />
            <Metric label="Download" value={download ? `${formatMbps(download.mbps)} Mbps` : "..."} />
            <Metric label="Upload" value={upload ? `${formatMbps(upload.mbps)} Mbps` : "..."} />
          </div>
        )}
      </section>

      <section className="netdiag-section" aria-label="Recent results">
        <h2>Recent results</h2>
        {results.length === 0 ? (
          <div className="netdiag-muted">No speed-test results logged yet.</div>
        ) : (
          <table className="netdiag-table">
            <thead>
              <tr>
                <th>Received (UTC)</th>
                <th>Surface</th>
                <th>Gateway sees</th>
                <th>Latency</th>
                <th>Down</th>
                <th>Up</th>
                <th>Rating</th>
              </tr>
            </thead>
            <tbody>
              {results.map((r, i) => (
                <tr key={i}>
                  <td>{shortTime(r.receivedAt)}</td>
                  <td>{r.surface || "-"}</td>
                  <td>
                    {r.clientIp ?? "?"} ({r.clientPath ?? "?"})
                  </td>
                  <td>{ms(r.latencyMedianMs)}</td>
                  <td>{mbps(r.downloadMbps)}</td>
                  <td>{mbps(r.uploadMbps)}</td>
                  <td>{r.rating || "-"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}

function TrendCard({ title, subtitle, children }: { title: string; subtitle: string; children: ReactNode }) {
  return (
    <div className="netdiag-trend-card">
      <div className="netdiag-trend-title">{title}</div>
      <div className="netdiag-trend-sub">{subtitle}</div>
      {children}
    </div>
  );
}

// A minimal, theme-aware, dependency-free line chart. Values are evenly spaced in time (oldest first). The
// stroke colour is passed in (an accessible, colourblind-safe hue chosen by the caller); the line reads in
// both light and dark because it uses the caller's token/hex and non-scaling stroke. Honest empty state.
function TrendChart({
  values, yMin, yMax, color, unit, prominent = false,
}: { values: number[]; yMin: number; yMax: number; color: string; unit: string; prominent?: boolean }) {
  if (values.length < 2) return <div className="netdiag-muted netdiag-chart-empty">Not enough data yet.</div>;
  const w = 600, h = 90, pad = 6;
  const span = yMax - yMin || 1;
  const x = (i: number) => pad + (i / (values.length - 1)) * (w - 2 * pad);
  const y = (v: number) => h - pad - ((Math.max(yMin, Math.min(yMax, v)) - yMin) / span) * (h - 2 * pad);
  const d = values.map((v, i) => `${i === 0 ? "M" : "L"}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(" ");
  const last = values[values.length - 1];
  return (
    <div className="netdiag-chart-wrap">
      <svg className="netdiag-chart" viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" role="img"
        aria-label={`Trend over the selected window; latest ${Math.round(last)} ${unit}`}>
        <path d={d} fill="none" stroke={color} strokeWidth={prominent ? 2.5 : 1.75} vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
      </svg>
      <div className="netdiag-chart-axis">
        <span>{Math.round(yMin)}{unit}</span>
        <span className="netdiag-chart-latest">latest {Math.round(last)}{unit}</span>
        <span>{Math.round(yMax)}{unit}</span>
      </div>
    </div>
  );
}

function fmtMbps(v: number | null | undefined): string {
  return typeof v === "number" ? `${v.toFixed(1)} Mbps` : "-";
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <div className="netdiag-metric">
      <div className="netdiag-metric-label">{label}</div>
      <div className="netdiag-metric-value">{value}</div>
    </div>
  );
}

function testPhaseLabel(phase: TestPhase): string {
  if (phase === "latency") return "Latency...";
  if (phase === "download") return "Download...";
  if (phase === "upload") return "Upload...";
  return "Running...";
}

function rate(medianMs: number, downMbps: number): string {
  if (medianMs < 30 && downMbps >= 50) return "fast";
  if (medianMs < 100 && downMbps >= 10) return "ok";
  return "slow";
}

function median(values: number[]): number {
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
}

function yn(value: boolean | null | undefined): string {
  return value === true ? "yes" : value === false ? "no" : "?";
}

function ms(value: number | null | undefined): string {
  return typeof value === "number" ? `${Math.round(value)} ms` : "-";
}

function mbps(value: number | null | undefined): string {
  return typeof value === "number" ? `${value.toFixed(1)} Mbps` : "-";
}

function formatMs(value: number): string {
  return value >= 100 ? String(Math.round(value)) : value.toFixed(1);
}

function formatMbps(value: number): string {
  return value >= 100 ? String(Math.round(value)) : value.toFixed(1);
}

function shortTime(value: string | undefined): string {
  if (!value) return "-";
  return value.slice(0, 19).replace("T", " ");
}
