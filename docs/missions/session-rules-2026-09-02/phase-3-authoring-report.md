# Phase 3 - a rule you make by talking

The mission that shipped on 3 September named one thing as the gap that mattered most: **you could not
create a rule by talking to it.** This is that half.

---

## First, what was actually true, because two sources disagreed

The merged commit `d447736c1` contains the words "authoring by conversation", which suggested some of
it existed. The same mission's closing report said it did not. Establishing which, from the merged code
rather than from either document:

- The phrase appears twice in that commit and **neither is code**. Once in the mission brief listing
  the phases that were planned, once in the phase 2 report saying it was not built.
- There was **one** write route, `POST /gateway/rules`, taking the trigger words and the checks already
  worked out, from the caller. `SessionRuleEndpoints` said so in its own header: "THIS IS NOT THE
  AUTHORING CONVERSATION. Phase 3 builds the part where a person says a sentence and a model turns it
  into a rule."
- Nothing else took a sentence and produced a rule - no other route, no service, no client screen.
- The rules tests ran green on `origin/main` in a fresh worktree: **223 passed, 0 failed**. None of them
  was about authoring.

**The closing report was right and the commit title was what misled.** Recorded because the cost of
getting this wrong was a day of building on top of something that was not there.

One thing the check turned up. The store refuses a rule with no trigger words, in these words: *"a rule
needs at least one word to watch for... The words are worked out from the instruction, not chosen by
hand."* That sentence was a promise about machinery that did not exist - the product was refusing
hand-written rules on the grounds that a machine would write them, and the machine was the missing half.

---

## What is built

**A second model call, and it is not the evaluator's.** `RuleAgentContract` asks whether a standing
instruction reaches a screen that exists now. `RuleDraftContract` asks what a standing instruction IS,
before any screen exists. They share the check registry, the argument notation and the reader for a
check written as JSON - so a check means one thing in this feature - and they share nothing else.

**`POST /gateway/rules/draft`**, which stores nothing. It answers with one of three things:

| Answer | What it is |
| --- | --- |
| A proposal | The rule, plus a plain-English read-back of what it would actually do |
| A question | The one thing that has to be answered before a rule can be written |
| A refusal | Why no rule was drafted, in words the account reads |

**The proposal is exactly the body the writing route takes.** Confirming a drafted rule is posting it
back unchanged, so what the person read and what gets stored are the same document. There is no second
translation step in which a scope or a check could quietly become something else.

**The person is still between the sentence and the first real act, twice.** Drafting writes nothing; a
person stores it, and the store still has no way to create anything but a dry-run rule; a person then
promotes it. The existing ruling is untouched and this adds no third way into the table.

**A proposal is run through the store's own gate before it is shown.** `SessionRuleRecordRules.CheckRule`
is the one implementation of what a rule is, called by the store and by the database write gate; the
author calls the same one. A rule the writing route would refuse is refused at drafting time, in the
same words. Nobody is asked to agree to a rule that then turns out not to exist.

**The instruction is the person's own words and does not come out of the reply at all.** The store
treats the instruction as the authority; a model asked to restate a sentence will eventually improve it,
and an improved authority is a different authority. A reply that supplies its own `instruction` field is
ignored, and there is a test for that.

**A model that cannot be asked produces a refusal, never a rule.** No default rule, no partial rule,
nothing assembled out of the parts that could be read.

---

## The rule language is not shaped around usage limits

The owner raised a second case while this was being built, and it is a different one: *"we've had
several problems where the API stopped working and you had to start it back up."* A provider outage is
not a session calmly reporting that it is out of allowance, and the recovery is different - a limit
wants another model, an outage wants a wait and a restart, and doing the limit action during an outage
would be wrong.

The danger is not that outages are unsupported. It is that the AUTHORING QUESTION gets written around
the one case the evaluator was demonstrated on, after which every rule an account writes comes back
shaped like an allowance rule and nobody can tell whether the language was ever general. So:

- The question names **no kind of trouble** as the expected one, and carries **no worked example** - an
  example is the fastest way to make every answer look like the example. There is a test that strips the
  person's own sentence out of the prompt and asserts the framing that remains does not mention
  allowance, usage limit, credit or rate limit. Adding one line about usage limits to the prompt turns
  it red; that was checked by doing it.
- **The waiting is the cooldown**, which is already part of what a rule is. "Leave it alone for fifteen
  minutes and then try again, at most six times a day" is a rule with a cooldown of 900 and a daily cap
  of 6. No new machinery.
- A rule that stakes nothing on a check is a real rule. None is a statement, not an omission.
- `A_rule_about_a_provider_that_stopped_working_goes_through_the_same_path` drafts an outage rule and
  stores it through the same round trip as the allowance one.

### What that does NOT cover, and the owner should read this part

**A rule only ever looks at the screen of a session that has just stopped.** The only thing that wakes
one is a session crossing from working to idle (`RuleTurnEndLauncher`, called from that transition
alone), and the only thing it reads is the tail of that session's screen. So:

- An outage that **prints an error and hands the prompt back** is reachable. That is the common shape.
- An outage that **hangs the session mid-turn** so it never goes idle is **not reachable by any rule**,
  and not by the session supervisor beside it either - that also only runs on turn end.
- An outage that **puts nothing on the screen** is not reachable, because the screen is the only input.
- **A rule types into a session; it cannot start one that is gone.** If the session itself has died, no
  rule reaches it.

Those are not rules somebody could write badly. They are rules that cannot fire, and saying so is
cheaper than letting somebody assume they are covered.

---

## A defect this found in the existing write path

Checking that a load-bearing test could actually fail turned up a real hole in `SessionRuleWire.ReadScope`,
which the writing route has been using since phase 2.

That reader deliberately refuses a scope that says nothing, because the widest possible value is the one
an omission must never quietly become. It refused an absent scope and an empty object - but **not an
object whose four parts were all null.** Each null was read as the empty-string VALUE, so the object was
not equal to the empty one, came through as a narrow scope of four empty strings, and was then blanked
back to four nulls by the store. That is a rule acting on every session the account has, from a request
that chose nothing.

It matters more now than it did, because the thing filling that object in is a model, and an object of
nulls is exactly what something produces when it does not know the answers and fills the fields in
anyway.

- Red first: `A_scope_whose_parts_are_all_null_is_refused_rather_than_read_as_every_session` **failed**
  before the fix.
- Fixed in the one reader both routes use: a part that is there and empty is a part that was not said.

---

## The build account

| Run | Result |
| --- | --- |
| Rules tests on `origin/main`, before any change | 223 passed, 0 failed, exit code 0 |
| Rules tests, after the feature | 251 passed, 0 failed, exit code 0 |
| Rules and speech-contract tests together | 387 passed, 0 failed, exit code 0 |
| Local gate, `.\scripts\test-local.ps1` | all 9 projects Completed; **4832 passed, 2 skipped, 0 failed** |
| Every web workspace, `npm run typecheck` | 4 workspaces, 0 errors |
| Every web workspace, `npm test` | 91 + 8 + 33 + 2 test files, 0 failed |
| The parked suites, `-Parked` | **ran for the first time.** `Gateway.Tests` 2322 passed / 2 failed (the two census rows above, now fixed); `Core.Tests` 4372 passed / 2 failed (`LoopbackPeerResolverTests`, which PASS when run alone - they read the machine's live connection table and lose to a full parallel run) |

### The three mutation checks - because everything passed first time

All 28 new tests were green on their first run, which on its own proves nothing. The three that carry
the most weight were each watched failing against a deliberately broken build, then restored:

| Guard | Break | Result |
| --- | --- | --- |
| The store's gate runs before a rule is offered | Skip `WhyTheStoreWouldRefuse` | **FAILED** as intended |
| The question presumes no kind of trouble | Add "Most instructions are about a usage limit" to the prompt | **FAILED** as intended |
| A narrow scope survives the round trip | Write the unset scope parts as null | **PASSED** - so the claim was wrong |

The third is recorded because it did not do what it was written to do. The comment on that test asserted
a trap that the store already handles by normalising blank to null, so the test could not have caught
it. The comment was corrected rather than left standing, and chasing why led to the real defect above.

---

## Driven against a real model, and what that found

Everything above is proven in the suite against canned model answers. That proves the path carries a
rule; it does not prove a live model writes good ones. So the real drafting prompt was put to the real
hosted model - `devthrottle/wingman` at `https://devthrottle.com/api/v1`, through the same
`HostedInferenceBrain` the Gateway wires, with `RuleAuthor`, the registry, the validator and the
store's gate all being the production code. Only the credential lookup was hand-wired.

**It works, and the answers are good.** What came back:

| Said | What came back |
| --- | --- |
| "When a session runs out of its allowance, switch it to another model and carry on." | ASKED BACK: *"Which model should it switch to?"* - correct; the sentence does not say |
| ...answered "Opus." | A rule: watches for allowance / quota / rate limit / usage limit / limit reached, 120 seconds apart, 5 a day |
| "When the provider stops working, wait a while and then start the session back up." | A rule: watches for provider / rate limit / 429 / service unavailable / connection refused / ECONNREFUSED / ETIMEDOUT / overloaded / quota exceeded, 120 seconds apart, 15 a day |
| "If a session is sitting there asking me whether it can edit a file inside the repository it is already working in, just say yes." | A rule: watches for edit / allow / permission / modify / approve, 5 seconds apart, 50 a day |

The multi-turn conversation is proven live and not only against a canned answer: the question came
back, the answer went in, and the second turn produced a storable rule whose instruction is the
person's own words.

### The defect this found: about half of these calls run out of time

The same sentence, asked five times in a row: **three ran out of the sixty-second limit and two
answered.** A one-line control question to the same endpoint in the same runs answered in 3 to 17
seconds every time, so the endpoint is up - it is this prompt, at 3,354 characters, that the hosted
model does not reliably finish inside a minute.

A wrong hypothesis is recorded here, because correcting it is what found the real shape. Across two
runs, the sentences that named a model vendor timed out while a vendor-less one succeeded, which looked
like the content mattering. A four-case isolation - two different vendors named, one with no vendor,
and the first vendor again - then timed out on ALL FOUR, including the exact wording that had succeeded
minutes earlier. The pattern is not the content. It is intermittent slowness on a prompt of this size,
and the five-attempt run above is what actually characterises it.

**What was fixed here, and what was not.** Running out of time is now reported AS a timeout, saying to
try again, rather than as "the model gave no answer at all": a person who waits a minute was being told
something true about the call and misleading about their situation. The underlying slowness is NOT
fixed and is not this phase's to fix - it is the hosted wingman's latency on a prompt of this size, and
it wants a faster model for this call, a longer limit, or a streamed answer. It is stated rather than
papered over.

---

## A defect found in the route census, and fixed

Running the parked `CcDirector.Gateway.Tests` - which the mission's own record says had never run -
turned it **red on main**, from phase 2 rather than from this work.

`ContextLessRouteCensusTests` holds a closed inventory of every route that takes a path parameter but
no `HttpContext`, because such a route cannot resolve a tenant from the request. Phase 2 mapped three
of them - `GET` and `DELETE /gateway/rules/{id}` and `GET /gateway/rules/{id}/firings` - and never
entered them, so a route family reachable on the multi-tenant deployment had no written verdict and no
executed cross-tenant probe. That is precisely what the census exists to prevent, and it was invisible
because the suite is parked.

Fixed as the census itself instructs: a probe FIRST, then the rows.
`SessionRules_AreNotReachableAcrossTenantsEvenHoldingTheOtherTenantsRuleId` enrolls two tenants on one
real hosted Gateway, seeds a rule for each over real HTTP, and has tenant A - holding tenant B's real,
Gateway-minted id - fail to read it and fail to delete it, with B's rule re-read intact afterwards and
B then performing that same delete itself, so the refusal is a refusal and not an inert route.

It was watched failing: removing `ApplyTenantScope<SessionRuleEntity>` from `GatewayDbContext` turns it
red, and restoring it turns it green.

`GET /gateway/rules/{id}/firings` is entered with a code-read verdict and is stated as NOT executed: no
route can write a firing - only the evaluator does - so both tenants read an empty list, and two empty
lists prove nothing about a partition.

---

## The Rules page

There was no Rules page, on either surface. There is one now, in the Cockpit, in the rail beside
Workflows and Skills - the shelf of things you define and the fleet then works by.

The page is the composer first and the ledger second. You say what you want in a box; the read-back
comes back in the largest text on the page with the rule under it; storing it is a separate button that
says out loud that storing does not turn it on. Below that, one card per rule with a spine that says at
a glance whether it can act at all, the promote behind a confirmation, and every firing on demand - a
decline styled exactly like an act, because a rule that decided not to act must not read the same as
one that did nothing because something broke.

The API client lives in `client-core`, not in the Cockpit, so the phone can mount the same page later
without a second copy of it.

---

## What is NOT proven

- **Every model answer in the SUITE is a canned string.** The suite proves the path carries a rule; the
  live run above is what proves a real model writes usable ones, and that is four sentences, not a
  sample.
- **Nothing has been drafted through the PAGE against a live Gateway.** The page is proven by rendered
  tests over a mocked client, and the model path by the probe above. The two halves have not been run
  end to end in one browser against one running Gateway.
- **The read-back can promise more than the rule enforces.** The permission rule's read-back said it
  applied "only to files within the repository", and the rule asked for NO check - `is_path_inside`
  exists for exactly that and was not named. Nothing unsafe follows, because the evaluator's own agent
  still judges the screen; but a person reading that sentence would believe a containment the rule does
  not itself test.
- **Trigger-word quality is unbounded.** Nothing stops a model proposing a word so common that the rule
  costs a model call on every screen. One live answer proposed "allow", "edit" and "modify", which are
  ordinary words on a coding session's screen.
- **Nothing ran hosted, against Postgres, or over HTTP.** The draft route is covered by unit tests over
  its reader and its author, not by a request through the live middleware.
- **The multi-turn conversation is untested against a real model.** A question coming back is proven as
  a reading; nobody has watched a model actually ask one and then answer it.
- **Trigger-word quality is unbounded.** Nothing stops a model proposing a word so common the rule costs
  a model call on every screen. The read-back is what a person would catch it with, and that relies on
  the person reading it.
- The gaps the mission already listed are unchanged: no session genuinely out of allowance has been
  recovered end to end.
