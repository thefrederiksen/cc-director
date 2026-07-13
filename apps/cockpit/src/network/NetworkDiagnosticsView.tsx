import { useCallback, useEffect, useMemo, useState } from "react";
import {
  getNetworkDiag,
  getNetDiagResults,
  measureLatency,
  measureDownload,
  measureUpload,
  postNetDiagResult,
  type NetworkDiag,
  type NetDiagResult,
  type ThroughputResult,
} from "@devthrottle/client-core/api/client";
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

  useEffect(() => {
    const controller = new AbortController();
    void loadDiag(controller.signal);
    void loadResults(controller.signal);
    return () => controller.abort();
  }, [loadDiag, loadResults]);

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
