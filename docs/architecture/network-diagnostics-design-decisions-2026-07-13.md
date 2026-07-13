# Network Diagnostics - Architect design decisions (P1 through P5)

Status: design settled 2026-07-13 by the "Network Diagnostics - Architect" session (136b9960) on
machine SOREN_NORTH, in the `devthrottle` repo. Companion to the mission brief
(`network-diagnostics-mission-2026-07-13.md`) and the findings handoff
(`devthrottle_internal/docs/networking/network-diagnostics-findings-handoff-2026-07-13.md`).
Manager driving the build: session 3cc96cc5. Docs owner: session 52749c5a.

This document settles the four decisions the Manager asked the Architect to make: the keep-warm
mechanism, the route-tagging plus persistence schema, how network statistics extend the existing
"Your Throttle" instrumentation instead of forking it, and the drift-alert policy. None of these
decisions depend on the Phase 0 deploy; only their validation against the live Gateway does.

## The one idea that organizes everything: route and quality are two different things

The single most important architectural point, drawn straight from the measured proof in the
findings handoff:

- ROUTE = which front door a request came in on. The Gateway already classifies this on every
  request in `NetDiag.ClassifyClientIp(IPAddress?)`:
  - loopback -> "local"
  - `192.168/16`, `10/8`, `172.16/12`, `169.254/16` -> "lan"   (direct on the home LAN)
  - `100.64.0.0/10` (Tailscale carrier-grade address space) -> "tailscale"   (the Tailscale front door)
  - anything else -> "other"
- QUALITY = how good the connection actually was: latency, throughput, and whether the underlying
  Tailscale path is direct peer-to-peer or relayed through a DERP relay server.

These are ORTHOGONAL and must never be conflated. A phone sitting on the home Wi-Fi still reaches
the `.ts.net` front door over the Tailscale address (100.x), so its ROUTE reads "tailscale" even
while its underlying WireGuard path is DIRECT (192.168.x, 11 ms). The app-only speed test cannot
tell "warming up" from "genuinely relaying" - only the Tailscale-layer state from
`GET /diag/network` (the `PeerDiag.Direct` flag) can. Therefore:

- ROUTE is the cheap, always-available, per-request home-versus-away tag. It tags usage events.
- QUALITY is the richer measurement that comes from a diagnostic run (client speed test plus the
  server-side `GET /diag/network`). It is a separate data stream.

They share the ROUTE dimension and share one presentation surface, but they are different tables.
Trying to jam latency numbers into the usage buckets, or infer quality from route alone, is the
mistake to avoid.

Home-versus-away mapping used at presentation time (raw four-value classification is what we STORE;
the two-bucket rollup is derived when we display):
- "home" = route in { "lan", "local" }
- "away" = route == "tailscale"
- "other" = route == "other"

Away plus relay is EXPECTED and fine; the baseline for "this should be fast" is home. This is the
rule that keeps the drift alert honest.

---

## Decision 1: Keep-warm mechanism (Phase 2)

Goal, from the WHY: keep the fast path warm so the user opens the app ALREADY direct, and does not
fall back to the relay mid-use. We control no knob that forces Tailscale to go direct; the only
lever is driving and holding the upgrade that Tailscale performs on its own once real traffic flows.

Keep-warm is TWO independent, honestly-scoped pieces. Neither is a silver bullet and the design must
not overclaim (house rule: never claim "fast" without the measured proof).

### 1a. Server-side warmer (primary, always-on) - the truth source

A background service on the Gateway that periodically runs `tailscale ping <peer>` to each peer it
should keep warm. The Gateway is always up; the phone's Tailscale membership persists in the phone
OS's background VPN service even when our progressive-web-application is closed. A server-driven
ping forces hole-punching and holds the direct path so that when the user picks up the phone and
opens the app, Tailscale is already direct and the cold-start window is short or gone.

- Implement as a `System.Threading.Timer` background service modeled exactly on
  `WebPushNeedsYouNotifier` (reentrancy guard with `Interlocked.CompareExchange`, per-iteration
  try/catch that isolates failures, self-gate when there is nothing to do). Start it in
  `GatewayHost.StartAsync` next to the existing sweeps.
- Reuse the exact `TailscaleCli.Run` runner the diagnostics already use. No new Tailscale plumbing.
- GATE which peers to warm: only ping peers that are (a) online in the last `tailscale status`, and
  (b) recently active (seen in the roster or in a diagnostic result within the last N minutes) OR on
  the home baseline. Do not ping a phone that has been dark for hours - it will not answer and it is
  pointless. Cap the peer count (reuse the existing `MaxPeersToPing` = 12 ceiling).
- Cadence: inside the direct-path decay window. Default one warm sweep every 60 seconds. Tunable.
- Best-effort ONLY. A failed `tailscale ping` is logged and skipped. Keep-warm is never a health
  gate and must never block, slow, or fail any real request.

Honest scope: whether a warmed direct path survives the seconds/minutes until the user opens the
app depends on Tailscale keepalives; the first real request the app fires also drives the upgrade
anyway. So the server warmer REDUCES the cold-start window; it does not abolish it. We measure its
effect (see Decision 3) rather than assert it.

### 1b. Client-side foreground heartbeat (secondary) - holds the path during use

While the phone or Cockpit application is open AND visible, fire the featherweight `GET /diag/ping`
on an interval. This drives a fast upgrade the instant the app opens and holds the direct path
during active use so the user does not decay onto the relay mid-session.

- Interval: default every 25 seconds. PAUSE when `document.hidden` (a progressive-web-application
  cannot run in the background, and we must not pretend otherwise - this is only a while-open hold).
- Use `GET /diag/ping` specifically (the endpoint that does no interface scan), so the heartbeat is
  near-zero cost and does not inflate any latency the user sees.
- Small hook in the shared client-core, consumed by both `apps/mobile` and `apps/cockpit`.

Forward-compatibility note: the sibling "Auto network switching / LAN-with-trusted-certificate"
mission may later remove the Tailscale hop at home entirely. Keep-warm must not assume that. Because
route classification already distinguishes "lan" from "tailscale", the warmer degrades gracefully
if a device starts arriving on the direct LAN door instead of the Tailscale door.

---

## Decision 2: Route-tagging plus persistence schema (the enabler)

Today the results live in `NetDiagResultLog`: an in-memory, per-process, capacity-50 ring, lost on
every Gateway restart. The route already rides each result as the Gateway-stamped
`NetDiagResultDto.ClientPath`. The enabler work is: make results durable, add a rollup that can
express trend and home-versus-away quality, and separately tag usage events with route.

Persistence pattern for ALL of the below: copy `CronRunHistoryStore` verbatim - a single JSON file
per store under `CcStorage.Root()` (`%LOCALAPPDATA%\cc-director`), bounded, newest-first, atomic
temp-write-plus-rename, corrupt-file quarantine, constructed in the `GatewayHost` constructor with a
test-injectable path. No SQLite (that is Vault-only). No new analytics pipeline.

### 2a. Durable diagnostic-results store (Phase 3) - replaces the in-memory ring

- New store `diagnostics-results.json`, modeled on `CronRunHistoryStore`: bounded, newest-first
  list of `NetDiagResultDto`. Raise the bound from 50 to about 200 for a useful recent history.
- `POST /diag/result` appends to it; `GET /diag/results` reads it. Same wire shapes; the only change
  is durability and capacity. The `NetDiagResultDto` (C#) and `NetDiagResult` (TypeScript,
  `client.ts`) records stay in lockstep - any field added goes to both.

### 2b. Hourly quality rollup (Phase 1 hour-only; route split deferred to Phase 4)

The flat results list is too small and too coarse for trends and for the home-versus-away quality
comparison. Add an hourly rollup mirroring `GatewayInputStatsAggregator.Hourly` (per-UTC-clock-hour
buckets, 90-day retention with pruning) holding QUALITY sums:

    count, sumLatencyMs, minLatencyMs, sumDownloadMbps, sumUploadMbps, directCount, relayCount

From these we derive, per hour: average latency, best (minimum) latency, average throughput, and
percent-direct (`directCount / (directCount + relayCount)`). We store SUMS and COUNTS, not per-sample
arrays, which keeps the store bounded and matches the "sums and counts" style of the existing
rollups. Note honestly: from sums we can show average and minimum, NOT a true median; do not label an
average as a median. This rollup is a NEW aggregation shape (the existing Your Throttle rollups carry
no latency or throughput concept at all), and it lives here in the diagnostics store - it is not
forced into the input-stats buckets.

REVISION (build-cadence decision, 2026-07-13): the Phase 1 rollup is keyed by HOUR ONLY - NO
home-versus-away route split. An earlier draft keyed it by (hour, route), but "route" for a quality
measurement would mean the front-door classification (`ClientPath`), and keying quality by the front
door CONFLATES route and quality - the exact orthogonality trap Decision 1 forbids. A home phone
reads `route = tailscale` (front door) while its path is DIRECT, so its good direct measurement would
land in the "away" bucket and poison it. The signal the Phase 3 trend actually needs is percent-direct
over time (`directCount / relayCount`), which needs no route key. So hour-only is both simpler and
more honest for Phase 1.

The home-versus-away split moves to Phase 4 for PRESENTATION, but the underlying quality history is
captured from day one (see the retention decision below): the quality-location is judged by the
ACTUAL path (a LAN-direct `192.168.x` path is home; a relay or non-LAN path is not), NOT the
front-door `ClientPath`, and the Phase 4 presentation lands in lockstep with the `InputRoute` usage
axis.

RETENTION AND THE PATH-SPLIT (build-cadence decision, 2026-07-13). Raw per-observation retention is
RECENT-DEPTH ONLY, not 90 days: a per-75-second raw log over 90 days is hundreds of thousands of
records, and the atomic-whole-file-rewrite store rewrites the entire file on every append, so a large
file rewritten every 75 seconds is write-amplification we will not pay. The ROLLUP is the 90-day
memory; raw stays recent (roughly the last day or two, or a few hundred records) for the
recent-results view and debugging.

To keep home-versus-away QUALITY history without a heavy raw log, the location split is baked INTO the
hourly rollup now, keyed on the ACTUAL path (`isLanPath`), never the front-door `ClientPath`. Each
hourly bucket carries LAN (home) versus non-LAN (away/relay) sub-sums - `lanCount`, `sumLatencyLan`,
`minLatencyLan`, `sumDownLan`, `sumUpLan` and the non-LAN counterparts - alongside `directCount` and
`relayCount`. Three properties keep this honest and in scope: it is NOT the conflation trap (it keys
on the measured path, not the front door); it is NOT a new key dimension (still one bucket per hour,
just richer sub-sum fields, no cardinality growth); and it stays Phase-1-cheap (Phase 1 STORES the
split but does NOT render it - the Phase 3 dashboard shows overall percent-direct plus latency trend,
and Phase 4 simply turns on the home-versus-away presentation over history already accumulated).

Both producers must tag observations by the actual path: the monitor already has direct plus
`isLanPath` per tick; client speed-test results need the tag added - put the self-peer's direct plus
`isLanPath` onto `NetDiagResultDto` (additive; the mobile page already computes the self-peer for its
verdict) so a client result lands in the right home-versus-away sub-sum rather than being classified
by `ClientPath`.

### 2c. Per-user baseline (Phase 4) - "slow" is judged against THIS network

Derive a known-good baseline from the observed home-and-direct distribution: a rolling summary
(for example, the trailing average and best of home-plus-direct latency and throughput over the
last N good samples) stored in the rollup file. "Slow" then means materially worse than THIS
network's own baseline, never a fixed threshold. This baseline is exactly what the Phase 5 drift
alert compares against.

The baseline is PER-DEVICE, keyed by Tailscale address (the phone at 44 ms direct, the laptop at
4 ms direct, and the mac at 73 ms direct are each their OWN "good" - never phone-versus-laptop, never
one global number). It records the device's baseline PATH TYPE (LAN-direct on a `192.168.x` address
versus relayed) as well as its latency and throughput, because the path type is the load-bearing
drift signal (see Decision 4).

Two correctness rules the baseline must obey:
- Build it from HOME-plus-DIRECT samples ONLY. Exclude any relayed or away sample - one relayed
  sample would poison the "good" number and make the whole thing lie.
- Keep the baseline "unknown" until it has enough good samples. During that warmup the drift state
  machine sits in UNKNOWN and never alerts - the same self-gating as when Tailscale is unavailable.
  Never compare against a half-formed baseline.

### 2d. Route-tagged USAGE events (Phase 4) - the real "extend Your Throttle" work

See Decision 3 for the full rationale. Schema decision here: add ROUTE as a THIRD axis on
`InputOrigin` - `record struct InputOrigin(InputModality Modality, InputSurface Surface,
InputRoute Route)` - with a new `InputRoute` enum { Local, Home, Away, Unknown } and wire tokens.
Route is resolved GATEWAY-SIDE exactly where `Surface` already is (the `AuthMiddleware` device stash
plus the `GatewayEndpoints` prompt stamp plus the `PromptRequest` gateway-authoritative field), so
it cannot be forged and the Director choke point simply carries whatever the Gateway stamped.
Desktop-local emit sites set Route = Local. This is the exact "add a new dimension" recipe the
existing stats code was built to accept.

Why a third axis and not a separate counter: the existing high-water fold in
`GatewayInputStatsAggregator.FoldLocked` tracks each (session, bucket-key) independently. If the
bucket key includes route, a session whose turns move from home to away simply lands its later turns
in a different bucket key; the fold still never double-counts and correctly attributes the home
turns to home and the away turns to away. Mid-session route change is handled for free. Bucket
cardinality stays trivial (2 modality x 4 surface x 4 route = 32 maximum, most empty).

Sequencing benefit: because diagnostic RESULTS already carry route (`ClientPath`), Phases 1 through
3 need no `InputOrigin` change at all. The third-axis work is isolated entirely to Phase 4 and
blocks nothing earlier.

---

## Decision 3: How network statistics EXTEND Your Throttle (Phase 4), not fork it

The rule from the brief: extend the existing instrumentation; do not build a second analytics
pipeline. Concretely, there are two data streams and each attaches to the existing system at a
different, already-established seam:

- USAGE by route (how much the product is used at home versus away): the third axis on `InputOrigin`
  (Decision 2d). It folds into the SAME `GatewayInputStatsAggregator`, serves from the SAME
  `GET /stats/data`, and renders on the SAME "Your Throttle" page - we add a home-versus-away split
  to the existing surface bars. Nothing parallel is built.
- QUALITY by route (latency, throughput, percent-direct, home versus away): the diagnostics rollup
  (Decision 2b). Your Throttle has no latency or throughput concept today, so this is genuinely new
  data - but it is surfaced as a NEW SECTION on the SAME Your-Throttle-styled page (and mirrored on
  the Cockpit "Network" view), reusing the `statsClient.ts` read patterns and the self-contained
  `/stats` page shell. Not a separate application.

Presentation of the home-versus-away quality comparison always judges home against the direct/fast
expectation and away against the relay-is-expected expectation, so the comparison and any alert are
measured against the right baseline (Decision 2c).

Honesty caveats: extend the existing `notCaptured` array in `StatsPageEndpoint.cs` with any path the
new dimension cannot yet observe - for example, desktop-local route attribution, or remote raw
keystrokes that are already attributed to the "Unknown" surface. No-fallback rule: declare what we
cannot measure rather than guessing it.

---

## Decision 4: Drift-alert policy (Phase 5)

The point of the whole mission: no user is ever silently slow at home. But the alert must NEVER fire
during the normal Tailscale cold-start warmup, and must NEVER fire for a genuinely-away device that
is correctly relaying. Design:

### Truth source and definition of drift

- The always-on truth source is the server-side scheduled monitor: `GET /diag/network` run on a
  timer (the same `System.Threading.Timer` background-service pattern; may be the same service as
  the warmer or a sibling). Each run is logged into the diagnostics rollup.
- DRIFT = a HOME device (a peer whose own baseline shows it is normally direct on the LAN) is
  observed `Direct == false` (relaying through a DERP relay), OR materially worse than that device's
  own baseline, for K CONSECUTIVE monitor runs spanning at least T minutes, where T is comfortably
  past the cold-start window. Defaults: K = 3 consecutive checks, T >= 5 minutes. A single relayed
  sample is warmup and is ignored.
- A device that is genuinely AWAY and relaying is NOT drift - that is expected and fine.

The drift DISCRIMINATOR keys off PATH TYPE compared to the device's own baseline path, NOT off raw
latency. From the server-side `GET /diag/network`, a home device shows `path = 192.168.x` (a
LAN-direct address); a relayed device shows `path = DERP(region)`. The primary, unambiguous drift
signal is therefore: a device whose BASELINE path was LAN-direct now shows DERP. That one comparison
cleanly separates the two cases we must never confuse:
- "a home device fell back to the relay" -> DRIFT (this is the thing the whole mission exists to catch).
- "a device is genuinely away and is correctly direct-over-the-internet or relaying" -> NOT drift,
  never flag it (away-plus-relay is expected).
Latency-worse-than-baseline is only a SECONDARY signal and needs a generous margin - the phone's
44 ms direct baseline must not flag drift at 50 ms. `Direct == false when the baseline path was
LAN-direct` is the clean primary; treat a modest latency rise on an otherwise-direct path as noise,
not drift.

### Two realities from live `tailscale status --json` on the Gateway machine (2026-07-13)

Grounding the discriminator in what Tailscale actually exposes (verified live against the four real
peers), two facts constrain the design:

- The `Relay` field is a TRAP. Every peer, INCLUDING the three that were actively DIRECT, carried
  `Relay: "tor"`. So `Relay` is just the peer's NEAREST DERP relay, present whether or not the peer
  is relaying. Reading `Relay` as "is relaying" misclassifies every peer. The direct-versus-relay
  truth is `CurAddr` (a `192.168.x:port` value means direct on the LAN; empty means relay or
  offline) plus the authoritative `tailscale ping` path parse (`direct = the path does not start
  with "DERP"`). The existing `TailscaleDiagnostics` already keys off `CurAddr` and the ping path,
  not `Relay` - keep it that way; never let the `Relay` field drive a direct-versus-relay decision.
- `Addrs` and `Endpoints` are NULL for every peer in `status --json`. So there is no idle
  "LAN endpoint candidate" to read while a peer is relaying. Once a device is relaying we cannot see
  its physical LAN presence from `status` at all. The only per-peer signals available are `CurAddr`,
  `Online`, `LastSeen`, and `LastHandshake`.

### Home-determination (the crux of "never a false alert")

"A home device fell back to the relay" (DRIFT) and "the user left the house and is now away and
relaying" (NOT drift) look IDENTICAL on the wire - both simply show the peer is no longer direct on
a `192.168.x` address. Because `status` does not expose physical-LAN presence once relaying, we
cannot perfectly separate the two. The trap to avoid: using "current path is LAN-direct" as the
home-signal would gate out the exact event we exist to catch (the instant a home device drifts to
DERP it would be reclassified as away). So home-determination must be INDEPENDENT of the current
relay state.

CORRECTION (from the code review of `NetDiagDrift.cs`, 2026-07-13): a time-based recency window does
NOT solve this. An earlier draft of this decision proposed "seen LAN-direct within the last 30
minutes AND continuously online" as the home-signal. Reviewing the concrete Decide-machine showed
that is unsound: drift accrues in K>=3 observations over >=5 minutes, but a 30-minute recency window
is WIDER than that accrual, so it does not separate "a home device fell to relay" from "the user
left the house and is now on a cellular relay." Trace: the user leaves at T0 (last-seen-LAN-direct
freezes at T0), the phone reconnects on a DERP relay over cellular, and the monitor's ticks at
T0/+3/+6 all still satisfy "seen home within 30 minutes" - so the machine drifts and fires "your
home network is slow" while the user is simply driving away on a perfectly good connection. Leaving
the house is a daily event, so this would cry wolf constantly. Any time window W just creates a
W-long false-alert hole after leaving; a window cannot fix this.

ROOT CAUSE: from Tailscale state alone, "home plus fell-to-relay" and "just-left-home plus on-relay"
are genuinely indistinguishable. The fix requires an INDEPENDENT physical-presence signal.

SETTLED RESOLUTION - Address-Resolution-Protocol presence with a Media-Access-Control identity match:

- The Decide-machine's home-gate is a `HomeLanPresent` input, NOT a time window. It is true only
  when the device is positively confirmed on the home LAN this tick.
- The Gateway is physically on the home LAN, so when a home-baseline device is seen relaying, it does
  a best-effort Address-Resolution-Protocol probe (on Windows, `SendARP` via `iphlpapi`) to the
  device's last-known `192.168.x` address. Address Resolution Protocol is answered at layer two by
  the device's network card regardless of application state, sleep, or whether Internet Control
  Message Protocol is disabled, and regardless of the Tailscale path - so it is the honest "is this
  device on my LAN right now." A device that left the house does not answer on the home LAN.
- REQUIRED refinement: gate on MAC IDENTITY MATCH, not merely "the probe resolved something." The
  cached `192.168.x` is a LAST-KNOWN address; a Dynamic-Host-Configuration-Protocol lease can expire
  and that address be reassigned to a DIFFERENT device, which would answer the probe and falsely read
  the departed device as home. So we cache the device's MAC (the probe returns it) from LAN-direct
  sightings and require the probe to resolve to THAT SAME MAC. A different or absent MAC means "not
  present" -> UNKNOWN, never alert. Cheap (the MAC is already in hand) insurance against a real, if
  rare, false positive.
- MAINTENANCE: refresh the (address, MAC) pair on EVERY LAN-direct sighting - that is exactly when
  Tailscale's `CurAddr` gives the current `192.168.x` and the MAC can be recaptured, so the cache
  never goes stale while the device is home.
- The presence probe is needed only for the PRIMARY relay-drift signal. The SECONDARY latency signal
  already requires `CurrentIsLanPath == true` (the device IS LAN-direct), so presence is self-evident
  there and needs no probe.
- Residual risk: a router/access-point could briefly proxy-answer Address Resolution Protocol for a
  just-departed device; this is small and bounded by the K>=3 / >=5-minute persistence plus the
  MAC-identity match plus the device dropping off the access point shortly after leaving.
- Scoping primary drift alerting to STATIONARY devices that cannot leave (a Mac mini, a desktop) is
  now OPTIONAL belt-and-suspenders, not required, once Address-Resolution-Protocol-plus-MAC is in.

We deliberately accept MISSING some genuine home-drift rather than ever crying wolf - the missed case
is still caught by the in-app status pill the moment the user looks, whereas a false "your home
network is slow" alert erodes trust in every future alert.

A related review finding on the same machine: the resolution note (`ShouldResolve`) must fire ONLY
on the transition Drifted -> Ok (recovery actually observed - direct restored), NOT on Drifted ->
Unknown (Tailscale down, or home-evidence lost). Emitting "recovered" merely because we lost the
ability to judge is a false all-clear; when we can no longer judge, go quiet.

Empirical confirmation (2026-07-13, phone on home Wi-Fi): a direct Address-Resolution-Protocol probe
from the Gateway resolved the phone's `192.168.x` to a stable per-network-name MAC (Internet Control
Message Protocol also answered, but Address Resolution Protocol is the robust layer-two signal). So
this approach is confirmed viable and the stationary-device fallback is not needed.

Monitor contract for the presence signal, and two accepted conservative limitations (all surfaced by
the code review; the Decide-machine itself is approved):
- The monitor must make `HomeLanPresent` ROBUST to a single blip. A sleeping phone under power-save
  can miss one Address-Resolution-Protocol answer; because the Decide-machine resets the episode on
  any Unknown tick, an un-smoothed probe would whipsaw a genuine active-use drift out of accrual. The
  monitor should briefly retry the probe, or treat a positive result as valid for a short window (for
  example "present within the last ~120 seconds"), before declaring absent. Note the natural upside:
  a phone that is genuinely asleep and not being used simply will not sustain presence, so alerts
  self-scope to "awake, home, and actively being slowed" - which is exactly when the user cares.
- LIMITATION 1 (accepted): if an Unknown tick lands between drift and recovery, the resolution note
  is suppressed (the transition into Ok no longer sees a Drifted prior). Worst case is a MISSING
  all-clear after a real alert, never a false one. Do NOT carry the alerted flag across Unknown to
  "fix" this - that would reintroduce a false-all-clear risk.
- LIMITATION 2 (accepted): the same episode-reset-on-Unknown means a flaky probe delays or prevents
  a real alert. This is the conservative direction (miss over false alarm) and the in-app pill still
  catches the missed case.

### No flapping: a pure state machine

Model the alert decision on `WebPushNeedsYouNotifier.Decide` - a pure, unit-testable transition
function `(observation, state) -> (shouldAlert, nextState)`. Enter the DRIFT state only after K
consecutive bad checks; clear it only after M consecutive good checks (hysteresis). Emit ONE alert
per drift episode (edge-triggered), and one resolution notice when it clears. No per-check spam.

### Channels (escalation, least intrusive first)

1. Always, on entering drift: emit a fleet doorbell event via `DirectorEventLog.Record(...)` - the
   same channel `GatewayCronNotifier` already uses - so the in-app surfaces and the Cockpit show an
   ambient "home network is slow" indicator with the specific cause.
2. On sustained drift: send an owner email via `AccountNotifyClient.SendOwnerAsync(...)` carrying
   the SPECIFIC fix (for example: "your phone is relaying through the Toronto relay while on the
   home network; keep the app open, grant Tailscale local-network access, make sure no exit node is
   routing home traffic, and enable UPnP on the router so hole-punching works"). Handle the
   not-signed-in (401) case explicitly - no silent fallback. At most one email per episode, plus a
   resolution note when it clears; a daily cap so a network broken for hours does not spam.
3. Do NOT hijack the phone "needs you" badge. That badge is count-driven off the session roster and
   semantically means "a session is waiting for you" (it is a wait-time queue). Overloading it with
   network drift would conflate two unrelated meanings. The phone signal for drift is the doorbell
   event plus the always-visible status pill from Phase 1, not the needs-you count.

### Anti-false-alarm guarantees (the reason this policy exists)

- Never alert during cold-start (the K-consecutive requirement).
- Never alert on away-plus-relay (expected; judged by route).
- Judge "slow" against the device's OWN baseline, not a fixed number.
- Edge-triggered: one alert per episode, one resolution note.
- The monitor self-gates: if no home device is present, or Tailscale is unavailable, the state is
  "unknown", not "drift" - it stays quiet rather than guessing.

---

## Monitor implementation notes (Phase 1, from the code review of `NetDiagMonitor.cs` / `LanPresenceProbe.cs`)

The server-side monitor was reviewed and approved against its contracts (direct-versus-relay from the
ping path parse never the `Relay` field; `CurrentIsLanPath` from the `CurAddr` private-range check;
`HomeLanPresent` from an Address-Resolution-Protocol probe resolving the same cached MAC; tick
cadence <=90 seconds; offline peers gated to Unknown before the Decide-machine). Three durable points
came out of that review:

- SAFETY INVARIANT: the positive-presence smoothing cache (120 seconds) MUST stay comfortably shorter
  than the drift-duration floor (5 minutes). That ordering is precisely what makes "the user left the
  house" watertight: a departed device ages out of the presence cache in ~120 seconds, well before
  drift can fire (three consecutive bad observations over 5 minutes), so it can reach at most a brief
  Suspect and never Drifted. If anyone raises the cache window past the drift floor, the leave-the-
  house false-alert hole silently reopens. Keep an assertion or comment guarding this.
- PERSISTENCE (guardrail C): the monitor is the always-on producer of continuous quality data, so its
  per-device observations (direct-versus-relay, latency, route) must be appended to the durable
  results store / hourly rollup starting at the FIRST live redeploy - not deferred to Phase 3. If the
  monitor only writes to the file log, then the baseline lives only in memory (a Gateway restart
  wipes it, forcing a re-warmup) and all quality history until Phase 3 is thrown away, which is the
  exact loss guardrail C exists to prevent. Two constraints on WHAT to persist:
  - The BASELINE is a separate derived value, NOT something to reconstruct from the hourly rollup. A
    rollup stores hourly SUMS (which cannot yield the median the baseline uses) and blends away/relay
    data. Persist the baseline as its own small per-device value (is-LAN-direct, typical latency,
    sample count), or persist the bounded recent HOME-plus-DIRECT-plus-LAN good-sample list and let
    the baseline computation run unchanged. Keep the home-plus-direct-only filter on anything
    reloaded - never seed a baseline from away or relay observations. The rollup is for trend and
    quality DISPLAY; the baseline is a different shape - do not conflate them.
  - DO NOT persist or restore the drift state machine across a restart. Persist only the baseline and
    the (cached-LAN-address, cached-MAC) presence identity. Restoring a mid-episode Drifted state with
    a stale first-bad timestamp could immediately re-fire an alert or mis-time the 5-minute floor on
    boot. A restart starts every device fresh at Unknown/Ok and re-accrues from live observations -
    the baseline seeds instantly (no re-warmup) while the drift-episode clock begins clean.
- MINOR HARDENING: an online peer that was not pinged (its direct-versus-relay verdict is null, which
  happens only past the per-tick ping cap or on a ping failure) should be gated to Unknown like an
  offline peer - judge only peers with a confirmed ping verdict. Latent for a small fleet, but tidy.

## Phase-by-phase landing summary

- Phase 0 (Manager, owner-gated): deploy the built foundation; validate `GET /diag/network` against
  the live Tailscale. Design does not depend on it; validation does.
- Phase 1: app-open auto-run plus an always-visible status pill (green direct/fast, amber warming,
  red relaying/slow); the server-side scheduled monitor (Decision 4 truth source), which flags a
  problem only when relaying PERSISTS.
- Phase 2: keep-warm (Decision 1) - server-side warmer plus client-side foreground heartbeat;
  contextual fix guidance when relaying persists.
- Phase 3: durable results store plus hourly quality rollup (Decisions 2a, 2b); Cockpit Network
  dashboard with status light, live per-device table, and trend over time.
- Phase 4: network-statistics surface styled like Your Throttle (Decision 3); home-versus-away usage
  via the `InputOrigin` third axis (Decision 2d); home-versus-away quality via the rollup; per-user
  baseline (Decision 2c).
- Phase 5: drift alert (Decision 4).

## Open items to confirm (with the Manager / owner, post-deploy)

1. Confirm the real peer/route classification against live `GET /diag/network` output - especially
   that a home phone reads route "tailscale" with `Direct == true` (the orthogonality assumption).
2. Confirm keep-warm cadence (60 s server / 25 s client foreground) is acceptable and does not annoy
   Tailscale or drain phone battery in practice; tune after a live observation.
3. Confirm the owner-email cadence default (one per episode plus resolution, daily cap) matches the
   owner's tolerance, consistent with the "email only on drift, always self-reap" convention.
