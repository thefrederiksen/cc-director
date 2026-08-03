# Phase 2b: an agent configures the product, and the write paths are driven

Two jobs, independent of each other. The first is a permission boundary the owner moved, and the
guard following it. The second is the set of write paths Phase 2 recorded, honestly, as built but
never driven - reassigned to this phase by the Architect rather than left in Phase 2's "not proven"
list.

Branch `mission/remove-network-port`. Nothing merged to main.

---

## Job one: the guard follows the owner's ruling

### What the owner ruled

In his words: the point of having agents is not to have to use the interface for most things; once
the agents are set up, he maintains and configures through them. The principle recorded in
`MISSION.md`, so it does not have to be re-decided per phase:

> **An agent may change how the product BEHAVES. It may not change WHO IS ALLOWED IN.**

### What changed in `SessionKeyGuard`

**Now allowed.** Configuration, in both directions:

| Shape | Verbs | Why |
|---|---|---|
| `/directors/{id}/settings` | GET, PUT | A Director's own settings - the ruling's first named item. |
| The closed `/gateway` settings set | GET, PUT | The application's settings: `settings`, `daily-report`, `snooze-default`, `snooze-presets`, `time-zone`, `ai-provider`, `tts-voice`, `spoken-language`, `spoken-language/voice`, `injected-text`, `transcription-mode`. |
| `/directors/{id}/handovers` | GET, POST, DELETE | Content agents produce; moving a session needs it. |
| `/directors/{id}/handovers/content` | GET | Reading one handover document. |

**Still refused**, and now tested by name against routes the Gateway actually maps:

| Shape | Why |
|---|---|
| `POST /directors/register`, `DELETE /directors/{id}/registration` | Which Directors are in the account. |
| `GET /devices`, `POST /devices/enroll-hosted`, `POST /mobile/enroll`, `POST /m/enroll`, `GET /account/devices`, `DELETE /account/devices/{id}` | Device enrolment - the owner named this one himself. A credential that can enrol a device can admit a NEW device, and could mint itself an account-wide credential and step straight out of this guard. |
| `GET /account/status`, `/account/credits`, `/account/trial`, `POST /account/email`, `/account/logout`, `/account/sign-in-start` | Account-level identity. |
| `DELETE /directors/{id}` | Force-killing a Director. Agents already have `request-deletion`. Flagged to the owner as a line he may want moved. |
| `POST /shutdown` | Turning the Gateway off. |

### The prose was rewritten, not amended - and it was wrong in three places, not one

The brief called out the comment over `IsBrowserRoute`. It was wrong in **three** places, and the
other two would have survived a narrower edit:

1. The class summary said a session key may not touch *"no Gateway or Director settings"*. That is
   the sentence a reader reasons from when classifying a new route, so leaving it would have taught
   the next reader the opposite of the ruling.
2. The comment over `IsBrowserRoute` said the rest of `/directors` *"is the owner's - registration,
   settings, handovers, force-kill - and stays refused"*. Note that the last Phase 2 commit had
   just made this sentence MORE explicit, spelling out settings and handovers as refused - so the
   wrong prose was actively being reinforced while the ruling said the opposite.
3. **The refusal message the agent actually reads.** `SessionKeyVerdict.Refuse` told every refused
   agent it *"may call the fleet's agent routes only, never the account surface"*. This is the one
   that reaches a human, in a log line and an error body, and it was the easiest to miss because it
   is a string rather than a comment.

All three now describe the same line, and the class summary carries a paragraph saying explicitly
why it was rewritten rather than amended: a wrong entry is visible in the code, whereas prose that
no longer describes the list is trusted by the next reader and carried onward.

### Two shapes that would have read as correct and been wrong

`PUT` did not previously exist as a verb in this guard at all. The obvious implementations are both
bypasses, and each has a test aimed at it:

- **A `/gateway` prefix rule.** "Configuration is anything under `/gateway`" reads as correct and
  hands over `/gateway/skills/{id}/disable` and `/gateway/workflows/{id}/enable`, which are
  deliberately refused because turning a fleet-wide capability off for everyone is the owner's call.
  The settings set is therefore a literal list.
- **A settings SUBTREE.** Matching `/directors/{id}/settings` as a prefix would open anything a
  future release parks underneath it - `settings/credentials`, say - on the day it ships. Matched at
  exactly three segments instead.

### Proof, both directions, fault-injected

`SessionKeyGuardTests`: **114 pass**. The allow cases and the refusal cases are written as a matched
pair, because "settings are allowed" is a safe sentence only while "enrolment is not" is still true
and still tested - they are one decision, not two.

Tests can fail. Both directions were fault-injected, on a commit made BEFORE the fault so nothing
could be lost:

| Fault | Result |
|---|---|
| **A - remove the settings allowance** (drop the `PUT` branch's two calls, and `gateway/time-zone` from the set) | **15 failures**: 12 of the `Configuring_the_product_is_allowed` cases plus all three focused facts. |
| **B - the plausible rewrite**: `/gateway` by prefix, and settings as a subtree | **4 failures**, exactly the three tests written for that rewrite plus the pre-existing `/gateway/reports/morning` refusal. Under this fault `PUT /gateway/skills/move-session/disable` becomes ALLOWED - the bypass, caught. |

Restored, and green again at 114.

**The guard is genuinely consulted.** `AuthMiddleware` calls `SessionKeyGuard.Check` inside the
authentication path on the raw request path, so the widening is live rather than a function nobody
asks. That was verified before trusting the unit tests, and then again on the wire below.

---

### Proven on the wire, not only in unit tests

An isolated Gateway (port 7997, own storage root) and an isolated Director (slot 6, own storage
root, `CC_DIRECTOR_AGENT_ROUTES=off`), both built from this branch. The Gateway reported
`1.9.7+dddec1eee8d05bcdd11b967892ea2551400b9d59` - the guard commit itself, so the running server
carried the change under test. Nothing on the live fleet was touched: the owner's installed Director
and his slots 1, 2 and 5 were left alone, the environment was set in a wrapper script the scheduled
task runs rather than at user scope, and the scheduled task has its own name
(`phase2b-director6-launch`), never the owner's `cc-director-launch`.

Driven from inside a real session holding a real phase-1b session key:

| | Result |
|---|---|
| `GET /directors/{id}/settings`, `GET /directors/{id}/handovers`, `GET /gateway/settings`, `GET /gateway/time-zone`, **`PUT /gateway/time-zone`**, `GET /gateway/snooze-presets`, `GET /gateway/injected-text` | **200 - allowed**, 8 of 8 |
| `POST /directors/register`, `DELETE /directors/{id}/registration`, `DELETE /directors/{id}`, `GET /account/devices`, `GET /account/credits`, `GET /account/status`, `GET /devices`, `POST /account/logout`, `POST /shutdown` | **403 - refused**, 9 of 9 |

Both halves matter. The refusals are what make the allowances safe to state.

---

## Job two: the write paths Phase 2 left unproven

Phase 2 recorded three things as built but never driven. The Architect reassigned them here.

### The seven browser write verbs

Only `browser list` had ever been driven, so the read proved the route, the guard entry and the
tunnel dispatch - and nothing proved a write could carry a command.

**All seven now pass**: create, rename, start, attach, signin, stop, remove. Six in one run and
create in another, because the first run had already left a profile of that name behind.

### A single-target message that ARRIVED, framed

This is the one Phase 2 could not do at all: its rig session was a batch script with no composer, so
`EchoVerifiedSubmit` had nothing to see echoed. This rig used a real Claude Code v2.1.220 session
with a live composer.

A 200 from the send route would have been the shape of the thing standing in for the thing, so the
proof is the target's own terminal:

```
Message [message from phase2b driver run3 (SOREN_NORTH), id be8b5fb7] phase 2b arrival
probe phase2b-arrival-1785783626  (to reply: cc-devthrottle message send be8b5fb7 ...)
```

It arrived, and it is framed with the SENDING session's own name, machine and id. That framing moved
from the Director to the Gateway in Phase 2, and this is the first time anything has checked that it
still frames correctly on the way through.

**Getting there needed a fixture fix worth recording.** Both first attempts died on Claude's
trust-this-folder prompt: the sessions were alive and looked healthy, but they were sitting on a
modal with no composer, and one of them exited. `POST /sessions/{id}/prompt` cannot answer a modal -
`SubmitVerifier` waits for a turn that a trust dialog never starts. The rig's sandbox folder was
pre-trusted in the Claude config instead (backed up first, and only that folder added).

### prompt, interrupt, compact, mission attach/detach, session done

All driven end to end and passing.

**Two of these I first called rig collisions, which was a claim about causation rather than a
finding, and the Architect was right to challenge it.** Removing the collisions settled both:

- `mission attach` had failed on "phase2b probe mission is ambiguous - 2 missions match", which is
  the command line resolving a NAME before any attach logic runs. Addressed by id it attaches, and
  six consecutive reads over eighteen seconds show the attachment holding.
- `session done` had failed my read-back of `pendingDeletion`. The session now returns **404** - the
  reaper removed it, which is exactly what the command documents. My assertion was wrong, not the
  product: I checked a transient flag instead of the outcome.

One observation I could NOT explain and am not diagnosing: twice, a read immediately after an attach
returned a null `missionId` while the attach response body showed it set. I could not reproduce it in
a six-read trace. It self-corrects, and I am recording it rather than attributing it.

---

## The independent inspection: eight proved defects

The Architect passed these over mid-phase. All eight are fixed, each with a test that fails without
its fix. Defect one overlapped Job One and was folded into it.

### The four high-severity ones

**1. The guard disagreed with the real routes and silently broke agents.** The shipped command line
creates skills and workflows with `POST /gateway/skills` and updates them with `PUT .../draft`; the
guard evaluated catalogue writes only inside its POST branch, at four segments. Every create and
every draft update returned 403. Schedules were worse - the guard allowed the two GETs and nothing
else, so create, update, delete, run-now and run-history all returned 403, and the Phase 2 pass mark
happened to exercise `schedule list`, the one schedule command that worked.

**The test stayed green because it was written from the guard.** It pinned `POST
/gateway/skills/{id}/draft`, a route that exists in neither the client nor the server. Guard and test
agreed with each other about a shape nothing uses. The replacement cases are copied from the route
table and from the Python clients - the only way a test can disagree with the implementation. One
stale refusal moved sides for the same reason: `DELETE /cron/jobs/{id}` was asserted refused, which
was the test agreeing with the guard rather than with `schedule delete`.

**2. Registration was keyed on a bare session id.** `Register` looked its row up unscoped and
overwrote that row's tenant, Director and hash without comparing any of them, so a Director
submitting a session id owned by another Director or tenant commandeered it. Fixed in three places,
because the lookup alone would leave the shape that allowed it: the lookup is tenant-scoped, a row
owned by a different Director inside the same tenant is refused rather than rotated, and the primary
key is now tenant plus session. Both migrations were amended in place rather than stacked, since
neither has reached main. The key-hash index stays globally unique - a presented key is resolved by
hash before any tenant is known, so that read must still cross tenants.

**3. Reap revocation was lossy.** The reap forgot the hash first, then fired a revocation that
returned silently on a disconnected tunnel, was never awaited, and had its failures swallowed - with
nothing recording that it was still owed, because the only trace had just been deleted. A reaped
session's key stayed valid for up to twelve hours, which contradicts what Phase 1b claimed. `Forget`
now records the debt in the same call and before the hash goes; a reseed replays owed revocations
alongside the registrations it already replayed; and the debt is settled only when the Gateway
accepts the invoke. Fire-and-forget is right for a roster push, because the next snapshot re-states
the truth - and wrong for a revocation, because nothing re-stated it. The Gateway also stopped
trusting the Director's clock for expiry, and an orphan key from a launch that throws after minting
is now ended in the same catch that already releases the worktree reservation.

**4. The session key crossed an origin boundary.** Python's default opener copies `Authorization`
onto a redirected request even when host and port change. A same-origin redirect is still followed; a
cross-origin one is refused loudly rather than followed with the header stripped, because an API
answering "go and ask that other host" is either a misconfiguration or an attack and both should be
read by a person. The two tests drive real loopback servers, because the defect lived in Python's own
redirect handling and a faked redirect would have tested the fake.

### The four medium ones

**5. Tracebacks where the owner accepted a sentence.** Four clients defined their own
`GatewayError` class while calling shared helpers that raise a *different* class of the same name, so
every `except GatewayError` missed the no-Gateway failure; `browser_ops` had no catch at all. The
four are now aliases of the shared class rather than look-alikes beside it, and `browser_ops` gets
one decorator across its eight entry points - two names for one idea is what caused this, and a
second except clause per handler would leave the trap for the next handler written. Three transport
failures also escaped as tracebacks and are now sentences: a read timeout leaves `urlopen` as a bare
`TimeoutError` rather than a `URLError`; an invalid `CC_GATEWAY_URL` raises `ValueError` out of
request parsing, which is why the request is now built inside the try; and a 200 whose body is not
JSON reached the JSON decoder outside any decoding catch. Verified by running the real commands with
both Gateway variables removed - all five groups now print the sentence. The existing suite could not
have caught any of it: its fixture installs fake Gateway values for every test, so no test ever ran
without one.

**6. Fanout let a session key name someone else as sender**, which decided both the team scope the
broadcast was judged against and the rate-limit bucket. Pinned to the authenticated session. A device
key is left alone - it acts for the account, not as a session.

**7. The desktop tool-health probe still tested the removed Director contract**, supplying only
`CC_DIRECTOR_API` and reading the resulting no-Gateway answer as `CannotReachDirector` - painting the
Tools fault banner and offering install and PATH repairs for a machine with nothing wrong with it.
That answer is now its own verdict; the banner keys on `CannotReachDirector`, so the mis-paint stops
with no change to the panel.

**8. Browser authorization was a method/path cross-product**, authorizing DELETE on the collection,
POST on attach, GET on start and DELETE on signin. None is routed today, so they 404 - but the day a
route appeared at one of those shapes it would already have been open to every session key. Now
matched on method and path together, with tests on both sides so the tightening did not overshoot.

---

## What is NOT proven

**The Gateway unit suite is intermittently red on the parent commit as well**, so it is not a finding about this change. It turned out to be a finding about the GATE - ten distinct failures across six runs with no repeats, on two commits - and it is written up under "Findings about the fleet's own tooling" at the end of this report, together with the comparative landing criterion the Architect has set as a result.

**The cross-tenant registration arm is structurally defended, not untested** - the Architect has
ruled it closed and the reasoning is written out in the closeouts below. The within-tenant Director
takeover and the expiry cap both fail cleanly without their fixes.

**The fanout sender pinning now has six tests**, fault-injected - see the closeouts. It was the one
fix carried by build and code-reading alone when this report was first written.

**Nothing was driven against the live hosted Gateway.** Every wire proof here is against the isolated
rig.

**The Postgres migration is now run against a real PostgreSQL 16**, on an empty database and on one
that already has rows - see the closeouts. When this report was first written it was hand-amended and
unrun, which for a service whose hosted Gateway runs Postgres meant a defect that would have surfaced
at deploy time.

**`cc-history` and the fleet self-test remain excluded** by the Architect's earlier ruling, and
neither has been run.

**The rig is torn down, so these proofs are a record rather than a running thing.** The Director
slot 6 had already exited on its own; the isolated Gateway was stopped by path match against the rig
directory; the rig's own scheduled task was unregistered and the owner's `cc-director-launch` was
confirmed still Ready; and the Claude config was restored from the backup taken before the sandbox
folder was pre-trusted. It is reproducible from the recipe in this report plus
`scripts/phase2-gateway-proof.ps1`.

**One thing observed during teardown that I am reporting rather than explaining.** The owner's
Director in slot 5 was running at the start of this phase and had exited by the end. I issued no
kill to any process: the single shutdown call this phase made went to `127.0.0.1:7883`, which
Director slot 6's own log names as its Control API address, and that call returned "unable to
connect" because slot 6 had already exited. Slot 5 could not have been holding 7883, or slot 6 could
not have bound it. I did not cause it as far as I can trace, and I cannot say what did.

---

## Closeouts: four things the Architect would not accept as written

### One: the Postgres migration, run against a real Postgres

The hosted Gateway runs Postgres. The local suites run SQLite and would never have told me, so a
hand-amended and unrun migration is a defect that surfaces at deploy time on the live service.

Run against **PostgreSQL 16.14** in a container, using `dotnet ef database update --connection`, which
applies the real migration rather than a script I wrote about it.

**Empty database.** The whole chain applies, ending `Applying migration
'20260803154516_AddSessionKeys'. Done.` The schema it produces:

```
PK_session_keys   PRIMARY KEY   TenantId   (ordinal 1)
PK_session_keys   PRIMARY KEY   SessionId  (ordinal 2)
"IX_session_keys_KeyHash" UNIQUE, btree ("KeyHash")
```

**The model agrees with it.** `dotnet ef migrations has-pending-model-changes` against the Postgres
provider: *"No changes have been made to the model since the last migration."* That is what closes
the risk of a hand-edit: the migration and the model are one story, checked by the tooling rather
than by me reading both.

**A database that already has rows.** Migrated only as far as `20260729173140_SkillPlacementState` -
confirmed by `to_regclass('gateway.session_keys')` returning nothing, so this is a genuine
pre-upgrade state - then seeded with two tenant rows, then migrated the rest of the way. The
migration applied (it even took the exclusive migration lock, as a real deploy does), the seeded rows
survived, and the key on the upgraded database is the composite one.

**Two control checks, because a schema that looks right is not a property that holds:**

| Check | Result |
|---|---|
| Two tenants insert the SAME session id | **Both rows accepted** - the isolation the composite key exists to give |
| The same insert against the OLD single-column key | **`duplicate key value violates unique constraint "PK_session_keys"`** - so the property comes from the key change, not from something else |
| A duplicate `KeyHash` across two tenants | **`duplicate key value violates unique constraint "IX_session_keys_KeyHash"`** - global hash uniqueness was NOT broken by making the key composite |

The second row is the one that matters. It shows the old schema structurally refused what the new one
allows, which is the same fact from the other side: under the old key one session id was a single
global row, and that is what made the takeover reachable.

Both test databases were dropped afterwards.

### Two: the stats failures, settled with the control I should have run first

The Architect challenged this claim once, I flagged the missing control myself, and then shipped the
claim anyway with the gap noted. That is the same mistake with a disclaimer attached. Here is the
control.

The Gateway unit suite was run three times on the **parent commit `45b1114c5`**, in its own worktree,
with no part of this phase's work present:

| Run | Parent commit (control) |
|---|---|
| 1 | **0 failures**, 2875 passed |
| 2 | **4 failures** - `SpokenVoiceTests`, `WingmanVoiceServiceTests`, `SessionKeyAuthTests`, `MorningReportBuilderTests` |
| 3 | **2 failures** - `MorningReportWindowTests`, `VoiceUploadStoreTenantPartitionTests` |

Six failures across three runs, six different tests, none repeated - and **none of them in the stats
area** where my own runs failed. So the suite is intermittently red on the parent as well, in whatever
area the scheduler happens to squeeze, and the failures are not characteristic of any particular
change.

**The claim was right and is now evidence rather than an inference.** It also cost nothing to prove,
which was the Architect's point: a different test failing each run is equally consistent with
contention and with a real race we introduced, and those two readings are indistinguishable without
the control.

**The parked suites are green on my commit**, which is the other half of the answer:
`CcDirector.Gateway.Tests` 2455 passed, `CcDirector.Core.Tests` 4200 passed - the two suites the
default gate does not run, and the two the coverage warning named for this change.

### Three: the fanout sender-pinning test

The pin was the one fix in this phase carried by build and code-reading alone, because the decision
sat inline in an endpoint only the parked, host-bound suite could reach. It is now a pure function
beside `SessionKeyGuard`, for the reason that guard is one: the rule lives in one place and can be
tested without standing up a Gateway and a Director.

Six tests. Fault-injected by restoring the pre-fix behaviour - taking the sender from the request
body - which fails exactly the two that describe the spoof.

The one worth naming is `A_session_key_that_names_nobody_is_still_pinned_to_itself`. The obvious fix
is to override only a MISMATCH, and that leaves the rate-limit half of the finding wide open: an
absent sender is its own bucket, so a caller escapes the bucket its own id counts into simply by
omitting the field. Omitting is not honesty; it is the same evasion with less typing.

The other three keep the fix from overreaching - a device key acts for the account rather than as a
session and keeps the sender it asked for; an honest caller naming itself is not logged as an
override, or the log stops meaning anything; and a claim differing only in case or padding is the
same session, not a spoof.

### Four: the cross-tenant registration arm is structurally defended, not skipped

Recorded at the Architect's ruling, and stated plainly so no later reader mistakes it for a gap.

The cross-tenant registration test passes even with the lookup fault injected. That is not a weak
test - it is the composite primary key making the attack **unrepresentable** rather than merely
refused. Two tenants naming one session id are two rows that cannot see each other, so there is no
state in which the takeover exists to be caught by a runtime check, and no fault injection into the
lookup can recreate one. Reverting the key as well does not isolate the arm either: it makes the
whole suite fail on a schema mismatch, which is a different failure about a different thing.

**When a fix makes an attack unrepresentable rather than refused, the absence of a test for it is a
property of the design, not a hole in the coverage.** What stands in its place is the Postgres
control above, which shows the old key refusing the very insert the new one accepts - the structural
change demonstrated directly, at the layer that enforces it.

The two arms that CAN be represented at runtime - a different Director inside one tenant, and the
expiry cap - both fail cleanly without their fixes, and are tested.

---

## Findings about the fleet's own tooling

These are not footnotes about a flaky test. They are two independent reliability defects in the
instrument the whole fleet merges on, uncovered by this mission, and they belong in the QA report as
such. The Architect asked for them recorded in these terms because the second one only became
visible when the control run for something else was finally performed.

### Finding one: the local gate is not a reliable pass-or-fail signal

**The evidence.** The Gateway unit suite was run six times across two commits - three on this
mission's work, three on its unchanged parent `45b1114c5`:

| | Failures | Which |
|---|---|---|
| Mission commit, run 1 | 1 | `GatewayStatsStoreMidChainContainmentTests.HealthyStores_AreNotReportedAsIncomplete` |
| Mission commit, run 2 | 2 | `GatewayStatsSqliteAdoptionTests.Adopt_StoreWhoseTableHasTheRightNamesAndNothingElse...`, `GatewayInputStatsAggregatorTests.WingmanUsage...` |
| Mission commit, run 3 | 1 | `GatewayStatsSqliteAdoptionTests.Adopt_ViewWearingATableName...` |
| **Parent commit, run 1** | **0** | - |
| **Parent commit, run 2** | **4** | `SpokenVoiceTests`, `WingmanVoiceServiceTests`, `SessionKeyAuthTests`, `MorningReportBuilderTests` |
| **Parent commit, run 3** | **2** | `MorningReportWindowTests`, `VoiceUploadStoreTenantPartitionTests` |

**Ten failures across six runs. Ten distinct tests. Not one repeat.** Every one passes when run alone.

**What that actually means.** The usual reading - "there are some flaky tests" - is too small. On an
UNCHANGED commit this suite produced a clean green and a four-failure red from the same source. So:

- **A green is not evidence the change is sound.** It may be the run that happened to schedule well.
- **A red is not evidence the change is broken.** It may be the run that happened not to.
- The failures are not concentrated in one area, so "we know which tests are flaky" is not available
  as a workaround. It landed in stats, speech, wingman, morning-report, voice-upload and
  session-key-auth across six runs.

**Why it matters to this mission specifically, rather than being someone else's problem.**
`CLAUDE.md` rule 5a makes "`.\scripts\test-local.ps1` goes green" the gate the entire fleet merges
on, deliberately in place of waiting for continuous integration. That criterion is only as good as
the signal underneath it, and the signal is presently luck in both directions. Everything this
mission eventually lands on main will be admitted by that criterion, including work nobody re-checks
afterwards.

**How this was nearly missed, which is the part worth keeping.** I hit the red, observed that the
failing test differed each run and sat in code the mission never touched, and concluded "pre-existing
contention". That conclusion was correct. It was also **unearned** - I had not run the parent, and I
said so in the report while shipping the claim anyway. The Architect refused it twice. Running the
control cost one command and about four minutes, and it did not merely confirm the claim: it turned a
narrow excuse about three stats tests into a general finding about the gate. **A claim that explains
away a red is exactly the claim that stops anyone looking.**

### The landing criterion this forces, stated so it generalises

The Architect has set the mission's landing criterion **comparatively rather than absolutely**, and
it is written here in general terms because the method is not specific to this mission and nobody had
written it down:

> **A run on the change counts only against a run on its parent. A failure belongs to the change only
> if it does not also appear on the parent.**

Practically, on a red:

1. Cut a worktree at the parent commit (`git worktree add ../<repo>-control <parent> --detach`), so
   the comparison is like-for-like and the working tree is undisturbed.
2. Run the same suite there, **more than once**. One run cannot distinguish a clean parent from a
   lucky one - parent run 1 here was green and would have convicted this mission of a regression it
   did not cause.
3. A failure that appears on both is the gate's, not yours. A failure that appears only on the change
   is yours, and one clean parent run is not enough to say so.

This is not a licence to wave reds through. It is the opposite: it makes "pre-existing" a claim with
a cost, payable in one command, instead of a sentence anyone can write for free.

### Finding two: the gate serves stale assemblies on incremental builds

The second reliability defect in the same instrument, and the one already known when this phase
started: an incremental build can leave a project's previous assembly in place, so the suite reports
on code that is not the code in front of you.

**What I can say first-hand:** I was warned of this in the phase brief and worked around it
throughout - `rm -rf obj bin` for every project whose result I intended to trust, before every result
in this report. I did not myself hit a wrong diagnosis from it, because I never ran without the
workaround. **What I am relaying rather than attesting:** the Architect records that it cost three
wrong diagnoses earlier in this mission. That is theirs to stand behind, not mine, and I am marking
which is which rather than inheriting it as something I saw.

**The two defects share a shape, and that is the real finding.** In both cases the gate answers a
question it cannot actually answer - "is this change sound?" - and the answer arrives looking
authoritative. Stale assemblies make it answer about the wrong code; the flakiness makes it answer
about the wrong run. A tool that is wrong loudly gets fixed. A tool that is wrong *plausibly*, and
only sometimes, gets believed, and its wrong answers get written into reports as findings.

Neither is fixed by this mission. Both are recorded here because a mission that merges on this gate
should not also be the mission that quietly normalises it.
