# Plan - trustworthy "Working" start (reviewed from the 2026-07-24 handover)

## The problem, precisely

Today the Director decides a session is "Working" from one rule in
`src/CcDirector.Core/Wingman/TerminalStateDetector.cs`: any byte out of the terminal means
Working, and ten seconds of silence means "needs you". That rule cannot tell the difference
between the agent doing real work and a background shell or the agent's own status footer
repainting the screen. On 24 July that difference cost an armed eight-hour snooze: a periodic
background repaint was read as "Working", and the Gateway's snooze observer correctly applied
its policy ("work ends a snooze") to a fact that was false.

The truthful definition we want: a session is Working while a TURN IS OPEN. A turn is opened by
a real submission or an explicit backend signal, kept alive by terminal output, and closed by
quiet. Terminal output alone must never open a turn from a settled state - for a driver we have
verified.

The one genuine hazard with that definition: some real turns start WITHOUT a submission the
Director can see - an agent re-invoked by a finishing background task, a scheduled self-wakeup,
a hook. If we gate turn starts on submissions before measuring how often that happens, we trade
a false-positive bug (phantom Working) for a worse false-negative one (real work shown as
"needs you", voice interrupting a busy agent, snoozes surviving real work). That is why the
evidence phase comes first and why the rule ships per driver behind a verified opt-in.

## Owner rulings (2026-07-24)

- The activity history IS stored on the hosted Gateway, tenant scoped, from the first
  increment. The Gateway is becoming the fleet's brain; why turns start is signal it should
  keep, combined across all customers. This supersedes the earlier draft of this plan that
  proposed Director-local storage.
- Retention is 30 DAYS, enforced by a sweeper from day one. The handover's "unbounded, decide
  later" is replaced by deciding now. If long-term learning is wanted later, derive small
  aggregates from the raw events before they age out - a future feature, not built now.

## Verification against origin/main (2026-07-24, commit 9fde3414)

The handover's code claims were checked and hold, with one correction:

- The byte rule, the ten-second quiet threshold, the resize-suppression window, the brand-new
  suppression, and the screen-body rule for continuous-idle drivers all exist as described in
  `TerminalStateDetector.cs`.
- `Session.SendInput` and `Session.SendTextAsync` are real choke points that already set
  Working on submission, and `LastOwnerTurnAtUtc` already separates owner identity from turn
  existence.
- `SnoozeLandingObserver` deletes an armed snooze on any Working observation, exactly as
  described, and its own comments acknowledge the repaint false-positive risk.
- Correction: the handover repeatedly refers to a "transcript generation" counter. No such
  signal exists in the code. The equivalent ground truth is `ConversationIngestor` storing a
  new assistant reply; use that.

## What the handover gets right - kept

1. Fix the fact, not the policy. The snooze rule is fine; the "Working" input to it is a lie.
   Do not touch snooze arithmetic or the Gateway's snooze policy.
2. Agent-specific behavior lives behind the driver abstraction, never as agent conditionals in
   shared code. The existing `EmitsContinuousIdleOutput` trait is the precedent.
3. Prove the new rule in shadow before it becomes authoritative, and opt drivers in one at a
   time with the raw-byte rule as the safe default for everyone unverified.
4. The Gateway owns the durable, tenant-scoped activity ledger; the Director captures and
   pushes through a durable outbox, following the existing prompt-log ingestion pattern.
5. Capture bounded terminal evidence when output arrives while settled. The July incident
   cannot be fully explained today because nobody kept the repaint content; that gap should
   never repeat. Never store the raw byte stream.
6. Uncertainty during an open turn preserves Working. A doubt must never hide active work.
7. The hold-endpoint race is real but separate. Do not bundle it.

## What is cut from the handover - and why

1. CUT unbounded retention. Raw activity events are purged after 30 days by a sweeper that
   ships with the table.
2. CUT the twelve-scenario live harness across all nine drivers. Only Claude needs to pass
   initially, and days of real fleet usage in shadow mode exercise the scenarios that matter
   better than a synthetic harness. Keep a short targeted checklist for the scenarios real
   usage may not cover (a long silent reasoning interval, a permission request, cancel,
   restart, resize).
3. CUT the recorded-terminal replay infrastructure for now. Build it only if shadow
   disagreements appear that cannot be diagnosed from the captured evidence.
4. CUT any new Cockpit page. A tenant-scoped read endpoint filtered by session and time range
   is enough for diagnosis in this feature.

## The plan

### Phase 1 - the Gateway activity ledger, then the Director producer (no behavior change)

Two pull requests. Nothing in this phase changes any state transition.

Pull request 1 - Gateway ledger:

- A contract in `CcDirector.Gateway.Contracts` for one activity event: who (director, session,
  machine, agent kind), when (occurred UTC, a per-director sequence), what (a closed event-type
  set), why (a closed cause set), plus the optional evidence fields (previous and new state,
  input origin, send source, detector mode and version, output byte count, before and after
  screen hashes, a bounded changed-row diff).
- Event types cover: turn submitted, backend activity started, terminal output while settled,
  activity transition, turn observed in transcript, session exited, and the snooze lifecycle
  (created, landed, ended).
- A tenant-scoped `activity_events` table through the Gateway database (entity derived from
  the tenant-scoped base, added to the context and the tenant filter), with SQLite and
  PostgreSQL migrations. Indexes on (tenant, session, occurred), (tenant, director, sequence),
  and (tenant, event type, occurred). Idempotent per tenant: a retried batch must not
  duplicate events.
- An authenticated batch write endpoint. Hosted with an unresolved tenant is refused, never
  mapped to Local. Bounded field lengths and batch size. Duplicates acknowledged as successful
  replays. A tenant-scoped read endpoint filtered by session and UTC range.
- A 30-day retention sweeper.
- The Gateway writes its OWN snooze lifecycle events into the same ledger: created (with
  requested minutes and deadline), landed, and ended - with the prior state and the reason
  (timer expiry, owner turn, manual release, session exit, working observation). This alone
  would have made the July 24 incident self-explanatory.
- Tests: store behavior on SQLite, PostgreSQL migration checks, hostile two-tenant isolation,
  endpoint authentication and validation, idempotent replay, sweeper.

Pull request 2 - Director producer and shadow classification:

- Stamp the last submission on the session at the two existing choke points: `SendInput` when
  it carries a submit byte, and `SendTextAsync` after the submit protocol succeeds - time,
  send source, input origin. (`LastOwnerTurnAtUtc` stays untouched; this is the turn-exists
  fact, not the owner-identity fact.)
- A shadow classifier at the terminal state detector: every time the current rule flips a
  session into Working, classify the evidence - a submission within a short window, an
  explicit backend signal, terminal output only from a settled state, or unknown - and emit
  an event. The classifier consumes facts; it must not switch on the agent kind.
- When output arrives while the session is settled and no submission explains it, capture the
  bounded evidence fields (byte count, body hashes, bounded changed-row diff, inside the
  resize-suppression window, brand new).
- Emit an event when the conversation ingestor stores a new assistant reply for a session.
  That is the ground truth that a real turn happened, used to judge the shadow rule in
  Phase 2.
- A durable Director outbox: events are minted once (id and sequence), persisted locally,
  pushed in batches, and deleted only on Gateway acknowledgement. The Gateway is the only
  durable history; the outbox is delivery state.

### Phase 2 - judge the shadow rule on real usage (no product code)

Deploy Phase 1 hosted, run normal fleet work for several days, then evaluate against these
gates for Claude, by querying the ledger:

- Every stored assistant reply has a recognized turn start (submission or backend signal)
  before it. Specifically hunt for real turns with NO submission - self-wakeups, background
  task completions, hooks - because those are what a submission-gated rule would wrongly
  suppress. Count them; if they exist, the rule needs a rescue signal (for example the
  ingestor's new-reply observation promoting the turn) before it can be authoritative.
- Every terminal-output-only classification while settled produced no stored reply afterward -
  confirming they were genuinely idle noise.
- Long silent reasoning intervals did not produce a false settled verdict inside an open turn.

The classifier's own answer is never its own oracle; the stored replies and the submission
stamps are the oracle.

### Phase 3 - authoritative opt-in per driver, plus the snooze regression proof

Two pull requests: the mechanism, then the opt-in with its evidence.

- Add a driver trait, an activity-start policy: terminal-output-fallback (the default,
  today's behavior) or submission-gated. Under submission-gated: terminal output maintains an
  already-open turn and re-arms the quiet countdown, but never opens a turn from a settled
  state; a submission or explicit backend signal opens the turn; an unknown verdict during an
  open turn preserves Working. The quiet-end rule stays exactly as it is today.
- Opt Claude in only after Phase 2's gates pass, citing the collected ledger evidence in the
  pull request. All other drivers keep the fallback until they earn their own opt-in.
- Add the end-to-end snooze regression test: arm a long snooze, emit periodic background
  output, assert the session stays snoozed and the ledger records the ignored output; then
  submit a genuine turn and assert the session goes Working and the armed snooze ends with the
  durable reason "working observation". Separately prove normal timer expiry and manual
  release still work.

### Explicitly out of scope

- The hold-endpoint race (separate, later change; the Phase 1 evidence will make it visible).
- Any change to snooze durations, deadlines, or the "work ends a snooze" policy.
- Any completion-detection rewrite; quiet-end behavior is untouched.
- Long-term aggregates for the Gateway brain (derive from raw events later if wanted).
- Storing raw terminal byte streams, or logging tenant identifiers and terminal content to
  hosted application logs.

## Definition of done

- The hosted Gateway durably retains tenant-scoped activity and snooze lifecycle events,
  purged after 30 days; retry and reconnect can neither lose nor duplicate events.
- Phantom Working from background or footer output no longer occurs for Claude sessions, and
  a recurrence would be diagnosable from the ledger alone, without free-text log archaeology.
- No real Claude turn is missed: every stored reply is preceded by a recognized turn start,
  measured over real usage, before and after the opt-in.
- Every other driver behaves exactly as today.
- Snooze timer behavior is unchanged and covered by the new end-to-end test, and every snooze
  end has a durable recorded reason.
