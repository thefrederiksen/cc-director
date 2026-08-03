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

**The full Gateway unit suite fails two tests per run, and they are different tests each run.**
`GatewayStatsStoreMidChainContainmentTests` on one run; `GatewayStatsSqliteAdoptionTests` and
`GatewayInputStatsAggregatorTests` on the next. All 89 pass when run alone. Every one is in the stats
area, and this phase touched no stats file - the complete list of changed files is session keys, the
guard, the registry, the fanout handler, the tool probe, and the Python clients. I am calling this
pre-existing contention rather than a regression, and I want to be explicit that I did NOT run the
gate on the parent commit to confirm it. That is an inference from the evidence above, not a
measurement.

**The cross-tenant registration test passes even with the lookup fault injected**, because the
composite primary key defends it at the database. Reverting the key as well makes the whole suite
fail on a schema mismatch rather than on the security property, so I could not isolate that one arm
cleanly. The within-tenant Director takeover and the expiry cap both fail cleanly without their
fixes.

**The fanout sender pinning has no test.** It is a Gateway endpoint change verified by build and by
reading the code path, not by a test that fails without it - which is the standard every other fix
here met. It is the weakest item in this report.

**Nothing was driven against the live hosted Gateway.** Every wire proof here is against the isolated
rig.

**The session-keys primary key change is verified against SQLite only.** The Postgres migration and
snapshot were amended identically and by hand; no Postgres instance was run.

**`cc-history` and the fleet self-test remain excluded** by the Architect's earlier ruling, and
neither has been run.

**The rig is left running** (Gateway 7997, Director slot 6, one live agent session) so the Architect
can inspect it, and the Claude config still carries the pre-trusted rig sandbox folder, backed up
alongside it. Both need tearing down.
