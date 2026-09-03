# Fix round D - the Architect's rulings on inspection D

Inspection D returned **FAIL** with ten findings: one blocker, four high, five medium. The findings
are in `inspection-d.md` and they are accepted **in full**. Neither pull request 2671 nor 2672 merges
until this round is done.

**Read `inspection-d.md` for each finding's evidence. This file says what to DO about it.** Where a
finding could be closed either by fixing the code or by withdrawing a claim, the ruling says which,
and that choice is the Architect's, not the builder's.

Two things the inspection established that are worth carrying forward as settled:

- **No generated code runs anywhere.** An independent reader found no path that parses, compiles or
  evaluates program text; an answer can name only a registry entry and typed argument values, and no
  argument is interpreted as a pattern, expression, format string or program. That is the owner's
  hardest ruling and it is now cleared by somebody who did not write the code.
- **A draft cannot promote itself.** The draft route writes nothing, create ignores state fields, and
  the store constructs `dry_run`. The promotion problem is finding 5 and it is about EVIDENCE, not
  about a hole.

---

## RULING D2 - grounding stops being caller-asserted. The Gateway reads the screen itself.

**This is the important one, and it is a re-architecture rather than a patch.** Finding 2 shows the
headline safety claim is optional (an empty screen skips it entirely, and the Cockpit always sends an
empty screen), caller-asserted (no session id, no server-side read, so provenance cannot be
established), checked against a different text than the model saw (the whole caller string versus the
last 40 non-empty lines), and defeatable by whitespace (the proposal keeps an untrimmed trigger, the
store trims it, so a padded trigger is checked narrow and stored wide).

**Do not fix those four separately. Remove the class of defect:**

1. **The draft route takes a SESSION ID, not a screen.** Delete the screen field from the request body
   entirely. The Gateway reads that session's screen itself, through the same read the evaluator uses
   (`GatewayRuleEnvironment` and the screen-grid verb). The session agent and machine likewise stop
   being caller-supplied: they are facts the Gateway already holds about that session, and the code
   already says in its own comments that a fact we hold must not be a guess we check.
2. **There is then no path with an empty screen**, so grounding cannot be skipped. If a caller
   supplies no session, the route REFUSES - it does not fall back to ungrounded authoring. Authoring
   from memory is no longer a mode.
3. **Ground against the EXACT text the model was shown.** One function produces the screen excerpt;
   the prompt and the check both use its return value. Not two readings of one string.
4. **One normaliser for a trigger word, used by both the check and the store.** The word that was
   checked and the word that is stored must be the same string, and the way to guarantee that is for
   there to be one function that produces it - not two that agree today.
5. **Re-run grounding at the WRITE GATE.** The create route also takes the session id and re-runs the
   same check against a freshly read screen. The design says the write gate is the one door; a check
   that runs only on the draft route is a check a caller can walk around by posting to create.

This subsumes the screen-reading half of Phase 3. Say so in the report - Phase 3 becomes the
conversation loop only.

## RULING D3 - the model never chooses the agent scope

Finding 3: scope is pinned only when the origin is known, and the Cockpit always takes the unpinned
path, so the primary page can return an every-agent rule because the answer chose it.

Ruling D2 makes the origin always known, which closes most of this. Then:

- **When the origin is somehow not known, REFUSE.** Never accept the model's agent scope. Delete the
  test that blesses every-session scope from an originless answer; it asserts the defect.
- **The all-agent star is a control the account operates.** The mockup has it and the record claimed
  it was built, but the Cockpit client sends neither origin nor the all-agents flag. Put the control on
  the page and send it. Every agent must be a thing the person chose, never a thing the model chose.

## RULING D4 - `rule add` stores the document that was read, and makes no second model call

Finding 4: the command drafts, stores, and only then prints the read-back, and running `rule draft`
first does not help because `add` drafts again - so what is stored can differ from what was read.

- **`rule draft` outputs the proposal** in a form that can be handed back.
- **`rule add` takes that proposal and posts it to the create route.** It makes NO authoring call. The
  create route validates it and re-grounds it (ruling D2 item 5), so a hand-edited proposal cannot
  smuggle an ungrounded trigger past the gate.
- There must be **no command that authors and stores in one step without showing the read-back
  first**. The contract is sentence, read-back, confirmation, store. A command that collapses it is
  the confirmation promise being false, and the promise is in the product's own words.

## RULING D5 - persist the acknowledgement, and stop the page inventing it

Finding 5: the page supplies a hard-coded sentence claiming the person read the dry-run record without
requiring that record to have been opened; the server checks only that it is non-blank; and the store
persists who promoted but not the acknowledgement - while the client contract says the acknowledgement
is what the record shows.

**Fix the code, do not withdraw the claim.** Promotion is the one transition that permits unattended
typing, and it is the owner's stated boundary.

- **Persist the acknowledgement** on the rule, with its migration in the same change. A record that
  cannot show what was agreed to is not a record of an agreement.
- **The page must not fabricate it.** Show the dry-run record in the confirmation step and let the
  person promote from in front of it. What is sent must describe what was actually shown.
- **The test must assert the value sent**, not stop at opening the dialog. As written, any non-blank
  constant survives.

## RULING D6 - the ceilings get real bounds, and these are the numbers

Finding 6: the store rejects only values at or below zero, so a daily cap of 2,147,483,647 and a
one-second cooldown both pass the safety gate. "Mathematically finite" is not a safety bound.

**The Architect's bounds, chosen so a live rule cannot type more than a hundred times a day:**

| Ceiling | Bound |
| --- | --- |
| Cooldown | at least 60 seconds, at most 24 hours |
| Daily cap | at least 1, at most 100 |

Refuse outside those, in plain English, naming the value and the bound. These are deliberately
generous next to the design's own examples (ten minutes apart and five a day; fifteen minutes and
six). **Record in the report that these are the Architect's numbers and that the owner can widen
them** - an invented bound presented as his decision is the defect this mission keeps naming.

## RULING D7 - an answer that cannot be read is a refusal, never a 500

Finding 7: the wire's number reader calls the 32-bit integer accessor on any JSON number, so a decimal
or an out-of-range integer throws, and neither the draft route nor the create route catches that class.
Authoring's stated boundary is that anything it cannot safely read becomes a stated refusal - so this
is the boundary failing, and it loses the reason on the way out.

Read numbers defensively and refuse with a sentence. Test a decimal and an out-of-range integer, on
BOTH routes.

## RULING D8 - a missing field is a broken instrument, and the clients stop deciding what things mean

Finding 8, and it is two faults.

- **Absence is not emptiness.** Both clients read a missing rules, rule or firings field as an empty
  result, which surfaces as "No rules yet" or "It has not fired yet" - an absence-shaped check
  reporting a positive fact when the data never arrived. The TypeScript list already rejects a missing
  rules field, which proves the stricter instrument exists and was simply not applied. Apply it
  everywhere, in both clients.
- **The clients are composing product meaning.** Scope labels and wait units are built in
  `rulesClient.ts`, the unstored scope is reinterpreted again in `RulesView.tsx`, and the Python client
  independently renders an empty scope as every session - so the two clients can disagree about the
  same state. That is the repository's rule 7 being broken. **The Gateway stamps the finished scope
  label and wait label onto the rule it serves, and both clients render the stamped string.**

## RULING D9 - prove the tenant and the agent are not constants

Finding 9: every authoring test uses one tenant and the asking seam discards it, so substituting a
constant leaves the suite green; the agent-origin tests use one agent value; and the cross-tenant probe
does not cover the draft route, which the census explicitly excludes and which the record admits never
ran over HTTP.

- **Two distinct tenants through the draft route over HTTP**, asserted at the far side.
- **Two distinct agent origins through the contract**, asserted at the far side.
- **Put the draft route into the census and the tenancy probe.** A route excluded from the census is a
  route the census cannot vouch for, and the probe was advertised as the proof.

## RULING D10 - the evidence rules apply to this round too

Finding 10: the record says all 28 new tests were green on their first run, documents three mutations
of which one stayed green, and quotes no command output or broken commit - so the branch cannot
establish that even the quoted reds came from the code named. The report also contradicts itself on
whether the multi-turn conversation was proven live.

- **Every load-bearing test in this round is watched red first**, with the command output and the
  commit of the broken tree quoted. A fix round is new writing and carries a new writer's risk.
- **Correct the contradiction in `phase-3-authoring-report.md`** to one authoritative statement. Two
  sentences that cannot both be true are worse than the weaker one alone, because a reader spends
  their scepticism somewhere else.
- Do not retrofit reds for tests written in the previous round. Say plainly which tests have been
  watched failing and which have not. **An unproven claim stated plainly beats a proven-looking one.**

---

## Order of work

1. Finding 1 is already in flight on `mission/rules-guard`. Do not duplicate it.
2. Rulings D2, D3, D4 together - they are one re-architecture of the draft and create routes and
   splitting them creates a window where grounding is half-moved.
3. Ruling D5 and D6 - both touch the store, so one migration.
4. Rulings D7, D8, D9.
5. Ruling D10 throughout, not at the end.

## The gate

- `.\scripts\test-local.ps1` green, and `-Gateway` green because this round touches the routes and the
  census. The Postgres proof rig must be up or the run is red for reasons that have nothing to do with
  you - container `cc-pg-test` on port 55432 was up on 2026-09-03.
- Every web workspace and `npm run typecheck`, because rulings D4 and D8 touch both clients. A partial
  web run is a known false green here.
- `pytest tools/cc-devthrottle/tests/` for the command line.
- One migration for the store changes, in the same change. Test whether a migration is PRESENT ON
  `origin/main`, never difference from the merge base.
- ASCII only. No mention of any assistant, model, vendor or AI tool in a commit message, a document or
  a comment.

## How to finish

Push on the fix-round branch and report to the Architect in ONE SINGLE LINE - fleet messages truncate
at the first newline. Write the detail to
`docs/missions/session-rules-2026-09-02/fix-round-d-report.md`, one section per ruling, saying for each
whether it is closed and what proves it. Name that file in your one line. Do not open a pull request
and do not merge; only the Architect lands work on main.

---

## RULING D11 - added after the guard fix, because the guard turned out to be the ONLY thing standing there

The guard fix (`dd78fd878`, finding 1) closed correctly, but its report named a fourth thing it could
not close and was right to: **`RulePromotionGrant` would accept a session key.** The route guard is
therefore the single mechanism preventing an agent credential from arming a rule - and the owner named
that exact transition as the one real exposure in the whole feature.

One mechanism is not enough for the one thing that matters most, and this repository has a name for
why: authentication is not authorization. The route list said "this credential may not come through
this door" and nothing at the destination asked "who is this?". That is the shape the original blocker
had - a decision made in one place that a second place never learned about.

**Refuse promotion at the grant as well, on the credential itself, not on the route.** A session key
that somehow reaches `RulePromotionGrant` is refused there with its own sentence. The two checks are
deliberately redundant: the route guard is the boundary, and this is the thing that still holds when a
future route change moves the boundary without anybody noticing.

Test it directly - construct a promotion attempt carrying a session-key identity and assert the
refusal - so it does not depend on the route guard being correct in order to pass.
