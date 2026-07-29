# Handover — Gateway activity ledger and trustworthy agent `Working` state (2026-07-24)

## Status

Design and incident investigation only. No product code, tests, migrations, or deployment were
changed in the originating session.

The next agent owns implementation. Start from a fresh worktree at the latest `origin/main`; this
shared checkout contains unrelated user work and must not be cleaned or reset.

## Read this first

This feature started with a false snooze interruption. The eight-hour timer was correct. A
background terminal-output event was classified as real agent work, and the Gateway deliberately
deleted the armed snooze because its current rule is “work ends a snooze.”

Do not change snooze arithmetic, add another timer, or special-case Claude Code. The owner’s
decisions are:

1. DevThrottle supports multiple agents. Agent-specific interpretation belongs behind the driver
   abstraction, never in shared code as `if Claude`, `if Codex`, and so on.
2. Avoid a broad or clever state-machine rewrite. Prove a narrower activity rule before making it
   authoritative.
3. The hosted Gateway owns durable fleet history. Store the activity/turn audit there, tenant
   scoped, with unbounded retention for now. A purge policy can be decided later.
4. Preserve a safe fallback for any driver that has not been verified.
5. The first production increment must be observational only: persist the evidence and run the new
   classification in shadow mode without changing `ActivityState`, Cockpit, voice, or snooze
   behavior.

## Incident and established root cause

Affected session:

- Director session: `abddd91b-5faf-4450-9e54-db0ec4293bca`
- Director: `a4f518a4-ea96-4827-a9a6-796c462b3cc9`
- Machine: `SOREN`
- Session number/name in Cockpit: `108`, `cc-consult - clickfunnels new`
- Screenshot: `D:\Personal\OneDrive\Pictures\Screenshots\Screenshot 2026-07-24 090201.png`

All times below are 24 July 2026 in Toronto/EDT:

1. At 7:41:50 a.m. Cockpit submitted an armed snooze for `480` minutes.
2. Gateway stored the correct deadline: 3:41:50 p.m.
3. At 7:56:12 a.m. the Director reported `Working`.
4. `SnoozeLandingObserver` called `ClearIfArmed`; the snooze entry was deleted.
5. At 7:56:46 a.m. the session returned to `Needs you`.
6. At 9:02 a.m. Cockpit showed `waiting 1h 5m`. That was the age of the red state since
   7:56:46, not the snooze duration.

The same session had armed snoozes cleared by `Working` at 6:56:49, 7:26:49, and 7:56:12—almost
exactly a 30-minute cadence. The screenshot showed one background shell still running. There was
no new owner turn, transcript message, or token activity at the final transition. This strongly
identifies periodic output from the background watcher or the agent TUI’s status/footer for that
watcher.

Be precise in future reporting: bytes were emitted **out of** the ConPTY. Nothing proves that bytes
were injected into the terminal. The Gateway log did not retain the exact ANSI payload, so the
precise repaint text cannot be reconstructed from the Gateway record. That missing evidence is one
reason for this feature.

The relevant diagnostic copy from the investigation is:

```text
.temp/gateway-director-2026-07-24-live.log
```

Important lines:

- `32981`: armed snooze stored until `2026-07-24T19:41:50.8969981Z`
- `32987`: `POST /sessions/{sid}/hold -> 200`
- `39692-39693`: armed snooze deleted because the session was reported working
- `39697-39698`: blue/Working display fold
- `39908`: red clock started at `2026-07-24T11:56:46.4961600Z`

The temporary log is investigation evidence, not a source file to commit.

## Current code path

### The false `Working` start

`src/CcDirector.Core/Wingman/TerminalStateDetector.cs`

- Its default rule is: any ConPTY output byte means `Working`.
- Ten seconds with no output means `WaitingForInput`.
- It contains a screen-body comparison path for drivers that declare
  `EmitsContinuousIdleOutput`, but the shared rule remains raw-byte based for most drivers.
- A Director-induced resize repaint has a short suppression window, but arbitrary TUI/background
  repaint does not.

### Real submissions are already observable

`src/CcDirector.Core/Sessions/Session.cs`

- `SendInput` detects CR/LF and sets `ActivityState.Working`.
- `SendTextAsync` sets `ActivityState.Working` after the shared submit protocol succeeds.
- Those paths cover desktop typing, Cockpit, voice, queue/framework sends, and agent-to-agent
  prompts when their call sites are correctly tagged.
- `LastOwnerTurnAtUtc` separately records whether the owner drove the turn. Do not conflate owner
  identity with whether a turn exists.

`src/CcDirector.Core/Backends/GitHubActionsBackend.cs`

- Remote backends can already report explicit activity through `ActivitySink`.

These facts support the hypothesis that terminal output should maintain an already-open turn but
should not automatically create a new turn from a settled state for a verified driver.

### Driver boundary

`src/CcDirector.Core/Drivers/IAgentDriver.cs`

- Drivers are stateless, shared behavior bundles.
- `EmitsContinuousIdleOutput` is an existing terminal-behavior trait, but it is too narrow to be
  the final semantic contract.
- Any stateful activity classifier must be instantiated per session; do not put mutable per-session
  state on the shared driver singleton.

`src/CcDirector.Core/Drivers/AgentDrivers.cs`

- This is the registry for Claude, Codex, Gemini, Pi, Cursor, Copilot, OpenCode, Grok, and generic
  drivers.
- No shared detector should switch on `AgentKind`. The registry/driver supplies the behavior.

### Snooze cancellation

`src/CcDirector.Gateway/Snooze/SnoozeLandingObserver.cs`

- `Working` or `Starting` calls `ClearIfArmed`.
- The source comments explicitly acknowledge that another agent’s message or a bare terminal
  repaint can be misread as work, but the current policy still deletes an armed snooze.

`src/CcDirector.Gateway/Snooze/SnoozeRegistry.cs`

- `ClearIfArmed` removes the durable snooze entry. This is not timer expiry.

`src/CcDirector.Gateway.Tests/SnoozeLandingObserverTests.cs` and
`src/CcDirector.Gateway.Tests/SnoozeEndToEndTests.cs`

- Tests currently require an armed snooze to be deleted on `Working`.
- The end-to-end test also requires `SnoozeExpired=false`, so Cockpit shows no “Snooze ended”
  badge for activity cancellation.

Do not change this Gateway policy in the first implementation. First make the upstream `Working`
fact trustworthy and prove it.

### Existing durable Gateway history

`src/CcDirector.Gateway/Prompts/GatewayPromptLog.cs`

- The Gateway already owns the fleet-wide prompt/reply history.
- It is tenant partitioned and intentionally retained forever.
- The Director captures and pushes; the Gateway keeps the single durable copy.

`src/CcDirector.Core/Storage/ConversationIngestor.cs`,
`src/CcDirector.ControlApi/GatewayPromptSink.cs`, and
`src/CcDirector.Gateway/Prompts/PromptEndpoints.cs`

- These are the reference pattern for Director capture, authenticated Gateway ingestion,
  acknowledgement, retry, and tenant-scoped storage.

Do not overload `PromptRecord` with state transitions. Conversation history and activity evidence
have different lifecycles and cardinality. Link them when possible, but keep a separate activity
ledger.

## Feature objective

Build a durable, tenant-scoped Gateway activity ledger that can answer:

- What caused each session to enter or leave `Working`?
- Was there a submitted turn, an explicit backend event, or only terminal output?
- Which driver and detector version made the decision?
- Did a transcript/reply later prove that a real turn occurred?
- Was a snooze active, and why did the Gateway end it?
- What bounded terminal evidence changed during an output-only anomaly?

Use that ledger to shadow-test a submission-gated activity-start rule. Only after a driver passes
the acceptance gates should it opt into the new authoritative behavior.

## Required implementation sequence

### Increment 1 — Gateway activity ledger and shadow evidence only

This increment must not change any current state transition.

#### Contracts

Add a dedicated contract in `CcDirector.Gateway.Contracts`, for example:

```csharp
public sealed record ActivityEventRecord
{
    public required Guid EventId { get; init; }
    public required long DirectorSequence { get; init; }
    public required DateTime OccurredUtc { get; init; }
    public required string DirectorId { get; init; }
    public required string SessionId { get; init; }
    public string? Machine { get; init; }
    public string? AgentKind { get; init; }
    public string? ContextId { get; init; }
    public required string EventType { get; init; }
    public string? PreviousState { get; init; }
    public string? NewState { get; init; }
    public required string Cause { get; init; }
    public string? InputOrigin { get; init; }
    public string? SendSource { get; init; }
    public string? DetectorMode { get; init; }
    public string? DetectorVersion { get; init; }
    public long? TranscriptGeneration { get; init; }
    public long? OutputByteCount { get; init; }
    public string? BeforeScreenHash { get; init; }
    public string? AfterScreenHash { get; init; }
    public string? BoundedScreenDiff { get; init; }
}
```

Names may improve during implementation, but preserve the facts. Use closed constants/enums for
event type and cause rather than arbitrary strings where the wire format permits it.

Minimum event types:

- `turn-submitted`
- `backend-activity-started`
- `terminal-output-while-settled`
- `activity-transition`
- `turn-observed-in-transcript`
- `turn-completed`
- `session-exited`
- `snooze-created`
- `snooze-landed`
- `snooze-ended`

Minimum causes:

- `owner-submit`
- `agent-submit`
- `framework-submit`
- `backend-signal`
- `terminal-output-only`
- `quiet-threshold`
- `driver-completion`
- `owner-turn`
- `manual-release`
- `timer-expired`
- `session-exit`
- `working-observation`

#### Durable store

Create a new tenant-scoped `activity_events` table through `GatewayDatabase`/EF Core rather than an
in-memory ring.

Requirements:

- Derive the entity from `TenantScopedEntity`.
- Add it to `GatewayDbContext` and `ApplyTenantScope`.
- Add matching SQLite and PostgreSQL migrations/snapshots.
- Primary key: code-generated `EventId`.
- Enforce idempotency per tenant. A retried batch must not duplicate events.
- Index `(tenant_id, session_id, occurred_utc)`.
- Index `(tenant_id, director_id, director_sequence)`.
- Index `(tenant_id, event_type, occurred_utc)` for shadow analysis.
- Retention is unbounded in this increment. Add no sweeper or cap.

Do not reuse `DirectorEventLog`; that is an in-memory, capped diagnostic ring. Do not reuse the
governance event ledger; agent activity is a different domain and volume.

#### Ingestion

Add an authenticated batch endpoint, for example:

```text
POST /activity-events/batch
```

Requirements:

- Resolve the tenant at the authenticated Gateway boundary.
- Hosted unresolved tenant: 403, never Local.
- Self-host: `TenantId.Local`.
- Validate bounded field lengths and batch size.
- Return the number accepted plus enough acknowledgement for the Director to drop acknowledged
  outbox records.
- Duplicate event IDs are successful idempotent replays, not errors.
- Add hostile two-tenant tests: tenant A cannot read, overwrite, or collide with tenant B.

Add a tenant-scoped read endpoint for diagnosis, filtered by session and UTC range. It is not
necessary to build a Cockpit page in the first increment.

#### Director producer and retry

The Director sees the raw facts and must emit them:

- submission events at the existing input choke points;
- backend activity events;
- terminal-output-only candidate starts;
- actual `ActivityState` transitions;
- transcript observations at conversation ingest.

Use a temporary durable outbox until the Gateway acknowledges each event. The Gateway remains the
only durable history; the outbox is delivery state and deletes acknowledged records. Activity
events cannot always be reconstructed from an agent transcript, so an in-memory-only retry is not
sufficient if “keep the history” is the contract.

Event IDs and Director sequence values must be minted once before entering the outbox and preserved
across retries.

#### Gateway-produced snooze events

The Gateway itself owns snooze decisions, so it must append its own events to the same ledger:

- request accepted/deferred/armed, including requested minutes and deadline;
- deferred snooze landed;
- owner turn superseded it;
- manual release;
- timer expiry;
- session exit;
- deletion caused by a `Working` observation.

Include the prior snooze state, resulting state, deadline, and end reason. This would have made the
July 24 incident self-explanatory without reconstructing it from free-text logs.

#### Bounded terminal evidence

Do not upload or retain every PTY byte.

For `terminal-output-while-settled`, retain:

- byte count;
- before/after normalized screen hashes;
- a bounded normalized changed-row diff or snapshot;
- whether the cursor/body changed;
- whether the event was inside a Director resize-suppression window.

Prompt text is already tenant-scoped customer history, but terminal evidence can contain secrets.
Keep it tenant scoped, bounded, and absent from ordinary process logs. Do not log raw tenant IDs or
terminal content to hosted application logs.

### Increment 2 — Shadow activity-start classification

Still do not change authoritative `ActivityState`.

For every current transition into `Working`, compute a shadow cause:

1. A successful submission was observed.
2. An explicit backend activity start was observed.
3. Only terminal output was observed while the session was settled.
4. The driver cannot decide (`unknown`).

Recommended driver-facing shape:

```csharp
public enum AgentActivityEvidence
{
    Unknown,
    Working,
    Settled,
}
```

If stateful interpretation is required, add a per-session tracker created by the driver, for
example `IAgentDriver.CreateActivityTracker(...)`. Drivers are shared singletons; mutable tracker
state must not live on `IAgentDriver` itself.

The shared detector consumes evidence. It must not switch on `AgentKind`.

Shadow output belongs in the Gateway activity ledger, not only `FileLog`. Include detector version
and driver kind so results remain interpretable after upgrades.

### Increment 3 — Live and replay verification

Do not enable new behavior based only on unit tests.

#### Replay

Use `TerminalSessionRecorder` captures and `TurnReviewLog` records as replay inputs. Add the July 24
incident capture if it can be recovered from the `SOREN` machine.

For each replay, compare:

- current raw-byte decision;
- shadow driver decision;
- known submission events;
- transcript generation/reply evidence;
- final prompt-ready state.

The incident replay must classify the periodic watcher/footer output as
`terminal-output-while-settled`, not a submitted turn.

#### Live driver harness

Run the same contract against every installed driver intended to opt in:

1. Normal typed prompt.
2. Cockpit prompt.
3. Voice/dictation prompt.
4. Queue/framework prompt.
5. Agent-to-agent prompt.
6. A 45–60 second silent reasoning/tool interval.
7. Continuous streaming output.
8. Permission request.
9. Cancel and interrupt.
10. Process exit and restart.
11. Terminal resize/reconnect/full repaint.
12. An idle background shell printing periodically for at least one hour.

Independent ground truth is required:

- successful submission timestamps establish turn start;
- agent transcript/rollout completion records establish completion where available;
- controlled helpers create external `started` and `finished` markers around long silent work;
- the activity classifier’s own answer is never its test oracle.

#### Acceptance gates per driver

A driver may opt into new authoritative behavior only when:

- every observed real turn has a recognized start before its first reply;
- every transcript-generation increment has a preceding shadow `Working`;
- no controlled silent turn becomes settled before its external `finished` marker;
- idle footer/background/resize output creates zero shadow turns;
- permission, cancel, exit, and restart scenarios match the expected lifecycle;
- reconnect and Gateway retry do not duplicate or reorder its durable ledger.

Any unexplained `current=Working, shadow=Settled` disagreement blocks that driver.

### Increment 4 — Per-driver authoritative opt-in

Only after a driver passes Increment 3:

- Expose a driver activity-start policy such as `TerminalOutputFallback` versus
  `SubmissionGated`.
- Default remains `TerminalOutputFallback`. Unverified and generic drivers keep today’s behavior.
- For `SubmissionGated`, terminal bytes may maintain an already-open turn and refresh the existing
  quiet observation, but terminal output alone may not create a turn from a settled state.
- A successful submission or explicit backend/driver signal starts `Working`.
- `Unknown` during an open turn must preserve `Working`; uncertainty must not hide active work.

Keep the current quiet/end behavior in the first authoritative cut unless a driver has independently
verified positive completion evidence. Do not combine the snooze incident fix with a fleet-wide
completion-classifier rewrite.

### Increment 5 — Snooze regression proof

Add end-to-end coverage:

1. Arm an eight-hour snooze.
2. Emit periodic background/footer terminal output.
3. Assert the session remains snoozed and the ledger records ignored output evidence.
4. Submit a genuine new turn through a verified driver.
5. Assert the session enters `Working`.
6. Under the current Gateway policy, assert the armed snooze ends with the durable reason
   `working-observation`.
7. Separately prove normal deadline expiry and manual release.

The test must assert the Gateway ledger, registry state, session DTO, and Cockpit-facing fields agree.

## Secondary bug discovered during the incident

The first snooze attempt at 6:40 a.m. exposed a separate `/hold` race:

- the hold endpoint used a stale pushed-session owner-turn baseline;
- the observer removed the snooze 172 ms after it was written;
- the endpoint still returned HTTP 200 from its local `decided` value;
- a second click was required.

This is real but is not the cause of the 7:41 snooze cancellation. Do not bundle its fix into the
activity-ledger feature. The ledger should make the race visible; fix it in a separately reviewed
change by re-reading authoritative snooze state before responding or making baseline/write
validation atomic.

## Explicit non-goals

- No Claude-specific condition in shared code.
- No immediate fleet-wide switch away from raw-byte fallback.
- No snooze-duration or clock changes.
- No new Cockpit activity-history page in the first increment.
- No storage of every raw PTY byte.
- No retention/purge policy in the first increment.
- No attempt to infer owner identity from output.
- No bundling of the `/hold` response race.

## Minimum test suites

At minimum, add or extend:

- Core activity tracker/detector unit tests.
- One contract suite exercised by every registered driver/tracker.
- Session submission-origin tests for raw Enter and `SendTextAsync`.
- Director outbox retry/idempotency tests.
- Gateway activity store tests on SQLite.
- PostgreSQL migration/model tests.
- Hosted two-tenant isolation tests.
- Endpoint authentication and batch validation tests.
- Snooze observer/end-to-end tests.
- Recorded terminal replay tests.
- Live harness instructions and captured results for each opted-in driver.

Run the full Core and Gateway test suites before merge. Treat a driver opt-in as a separate reviewable
change from the ledger/shadow infrastructure.

## Delivery order

Prefer separate PRs:

1. Contracts, tenant-scoped Gateway ledger, migrations, endpoints, and isolation tests.
2. Director activity producer, durable outbox, Gateway acknowledgement, and shadow events.
3. Replay/live harness and per-driver verification evidence.
4. First verified driver opt-in plus snooze regression test.
5. Additional driver opt-ins, one or a small compatible group at a time.

Deploy the ledger/shadow increments before any behavior change and collect hosted evidence across
normal work. The behavior PR must cite that evidence and list which driver acceptance gates passed.

## Definition of done

The feature is done only when:

- the hosted Gateway durably retains tenant-scoped activity and snooze lifecycle events;
- retry/reconnect cannot lose or duplicate events;
- the July 24 failure is diagnosable from structured history without free-text log archaeology;
- at least one driver has passed replay and live acceptance gates;
- that driver ignores idle background/footer output without missing any controlled real turn;
- unverified drivers retain the safe current fallback;
- snooze timer behavior remains unchanged and covered;
- the deployed hosted version is verified against the merged commit.

## Hard rules

- Preserve unrelated work in the shared checkout.
- Use a fresh worktree from current `origin/main`.
- Verify current source and deployed behavior; this handover records the July 24 state and may age.
- Keep all hosted reads/writes tenant scoped and deny unresolved hosted tenants.
- Do not add authorship or generation attribution.
