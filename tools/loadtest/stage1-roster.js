// Stage 1 of the Gateway load-test plan (devthrottle_internal issue #1173): read/polling load.
//
// N virtual viewers poll GET /sessions every 2 seconds (the real client roster cadence,
// ROSTER_POLL_MS = 2000), each authenticated as a synthetic tenant's viewer device key from the
// LoadRig's viewers.json. The ramp climbs toward the plan's ~9,000 requests/second target; the ceiling
// is the step where p95 crosses the threshold or errors appear.
//
// Run (see tools/loadtest/README.md):
//   GATEWAY_URL=http://127.0.0.1:7891 KEYS_FILE=./loadtest-out/viewers.json k6 run tools/loadtest/stage1-roster.js
//
// Environment:
//   GATEWAY_URL   REQUIRED. Local hosts only, unless LOADTEST_ALLOW_HOST names a staging host.
//                 Production (azurewebsites.net / devthrottle hosts) is REFUSED with no override.
//   KEYS_FILE     REQUIRED. The LoadRig's viewers.json: [{"tenant": "...", "deviceKey": "..."}].
//   MAX_VUS       Cap the profile (default 10000). Steps above the cap are clamped, so a smaller
//                 machine can run the same shape lower.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend, Rate } from 'k6/metrics';

const BASE = (__ENV.GATEWAY_URL || '').replace(/\/+$/, '');
if (!BASE) throw new Error('GATEWAY_URL is required (e.g. http://127.0.0.1:7891).');

// The one hard safety rule, in JavaScript: never production, loopback is free, anything else must be
// named in LOADTEST_ALLOW_HOST. Mirrors tools/loadtest/Shared/LoadTargetGuard.cs.
const v6Match = BASE.match(/^https?:\/\/\[([0-9a-fA-F:]+)\](?::\d+)?$/);
const hostMatch = v6Match || BASE.match(/^https?:\/\/([^/\]:]+)(?::\d+)?$/);
if (!hostMatch) throw new Error(`GATEWAY_URL is not a plain base URL: ${BASE}`);
// Strip trailing dots before ruling: 'gw.azurewebsites.net.' is the same DNS name as without the
// dot, and an endsWith check that misses it would let the absolute-form spelling of a production
// host through (the C# guard trims the same way).
const host = hostMatch[1].toLowerCase().replace(/\.+$/, '');
if (host.endsWith('azurewebsites.net') || host.includes('devthrottle'))
  throw new Error(`REFUSED: ${BASE} matches the production deny list. The harness NEVER runs against production; there is no override.`);
// IPv6 loopback in its compressed, expanded, and zero-padded spellings - k6 has no address parser,
// so the known spellings are listed; anything else IPv6 falls through to the allow-host rule.
const LOCAL_HOSTS = ['localhost', '127.0.0.1', 'host.docker.internal',
  '::1', '0:0:0:0:0:0:0:1', '0000:0000:0000:0000:0000:0000:0000:0001'];
if (!LOCAL_HOSTS.includes(host) && (__ENV.LOADTEST_ALLOW_HOST || '').toLowerCase() !== host)
  throw new Error(`REFUSED: non-local host '${host}'. If this is a dedicated staging rig, set LOADTEST_ALLOW_HOST=${host}.`);

if (!__ENV.KEYS_FILE) throw new Error('KEYS_FILE is required (the LoadRig viewers.json).');
const KEYS = JSON.parse(open(__ENV.KEYS_FILE));
if (!Array.isArray(KEYS) || KEYS.length === 0) throw new Error(`KEYS_FILE ${__ENV.KEYS_FILE} holds no viewer keys.`);

const MAX_VUS = parseInt(__ENV.MAX_VUS || '10000', 10);
const clamp = (n) => Math.min(n, MAX_VUS);

// STAGES: override the default climb, e.g. STAGES=60s:10,60s:25,60s:50,120s:100 - used to zoom in on
// the knee once a standard run shows the first plateau already degraded, and to re-run the exact same
// shape after a fix for comparison.
function stagesFromEnv() {
  if (!__ENV.STAGES) return null;
  return __ENV.STAGES.split(',').map((s) => {
    const [duration, target] = s.split(':');
    if (!duration || !target) throw new Error(`STAGES entry '${s}' is not duration:target`);
    return { duration, target: clamp(parseInt(target, 10)) };
  });
}

const rosterLatency = new Trend('roster_latency', true);
const rosterErrors = new Rate('roster_errors');

export const options = {
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
  // The plan's climb: 100 -> 500 -> 1000 -> 2500 -> 5000 -> 10000 viewers at a 2 s cadence
  // (~9,000 requests/second at the top). Each step ramps then holds, so every plateau gives a clean
  // window to read (scrape /diag/loadmetrics?reset=true at each plateau start).
  scenarios: {
    viewers: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: stagesFromEnv() || [
        { duration: '30s', target: clamp(100) },
        { duration: '90s', target: clamp(100) },
        { duration: '30s', target: clamp(500) },
        { duration: '90s', target: clamp(500) },
        { duration: '30s', target: clamp(1000) },
        { duration: '90s', target: clamp(1000) },
        { duration: '45s', target: clamp(2500) },
        { duration: '90s', target: clamp(2500) },
        { duration: '60s', target: clamp(5000) },
        { duration: '90s', target: clamp(5000) },
        { duration: '90s', target: clamp(10000) },
        { duration: '120s', target: clamp(10000) },
        { duration: '30s', target: 0 },
      ],
    },
  },
  // The plan's first-cut thresholds (section 6). Crossing them does not abort the run - the point is
  // to find WHERE they cross, which is the ceiling the baseline records.
  thresholds: {
    roster_latency: ['p(95)<300', 'p(99)<800'],
    roster_errors: ['rate<0.001'],
  },
};

export default function () {
  const k = KEYS[__VU % KEYS.length];
  const res = http.get(`${BASE}/sessions`, {
    headers: { Authorization: `Bearer ${k.deviceKey}` },
    tags: { tenant: k.tenant },
  });
  rosterLatency.add(res.timings.duration);
  rosterErrors.add(res.status !== 200);
  check(res, { 'status 200': (r) => r.status === 200 });
  sleep(2); // the real client roster cadence (ROSTER_POLL_MS = 2000)
}

export function handleSummary(data) {
  const out = __ENV.SUMMARY_FILE || 'stage1-summary.json';
  return { [out]: JSON.stringify(data, null, 2), stdout: textSummary(data) };
}

function textSummary(data) {
  const m = data.metrics;
  const t = (name) => (m[name] && m[name].values) || {};
  const lat = t('roster_latency');
  const err = t('roster_errors');
  const reqs = t('http_reqs');
  return [
    '',
    '=== Stage 1 roster summary ===',
    `requests: ${reqs.count || 0} (${(reqs.rate || 0).toFixed(1)}/s average)`,
    `latency ms: p50=${(lat['p(50)'] || lat.med || 0).toFixed(1)} p95=${(lat['p(95)'] || 0).toFixed(1)} p99=${(lat['p(99)'] || 0).toFixed(1)} max=${(lat.max || 0).toFixed(1)}`,
    `error rate: ${((err.rate || 0) * 100).toFixed(3)}%`,
    '',
  ].join('\n');
}
