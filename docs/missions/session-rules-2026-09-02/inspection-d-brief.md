# Session Rules - inspection D brief (authoring by conversation)

## The four fields

- **Landing:** D - authoring by conversation, the Rules page, and the rule command group.
- **Branch to inspect:** `rule-authoring-by-conversation` (two commits on top of `origin/main`).
- **Diff to read:** `git diff origin/main...rule-authoring-by-conversation`
  - Commit 1 (pull request 2671) is the Gateway half.
  - Commit 2 (pull request 2672) is the two clients plus the mission record.
- **The mission's own report:** `docs/missions/session-rules-2026-09-02/phase-3-authoring-report.md`

## What you are inspecting

A feature called Session Rules. A rule is a standing instruction the account writes in plain English.
When a session goes idle, cheap code decides whether any rule could apply; if one could, a model
reads the screen and the instruction together and decides what to do, including declining. It then
types into the session. Every firing is recorded.

THIS landing is the AUTHORING half - turning a sentence into a rule. New in it:

- `RuleDraftContract` - the prompt and the reader for the authoring call, including the grounding
  check (trigger words must appear on the real captured screen) and the agent-scope handling.
- `RuleAuthor` - the call itself, wired straight to the thinking model so a timeout can be told apart
  from a refusal.
- `SessionRuleEndpoints` / `SessionRuleWire` - the draft route and the wire shapes.
- `apps/cockpit/src/rules` and `packages/client-core/src/rules` - the Rules page and its client.
- `tools/cc-devthrottle/src/rule_ops.py` - the `cc-devthrottle rule` command group.

Read `docs/missions/session-rules-2026-09-02/brief.md`, `plan.md` and
`implementation-plan-for-the-architect.html` on the branch for the design and the owner's rulings.

## BE ADVERSARIAL, AND DO NOT TRUST THE MISSION'S OWN REPORT

That report is self-testimony: written by the people who did the work, about their own work. It is
exactly as persuasive as it is unreliable. Read the CODE and the TESTS, and treat every claim in the
report - and every claim in the two pull request bodies - as something to be checked rather than
summarised. The pull request bodies claim a green local gate, a green web run across every
workspace, and a green Python run. Check the claims that are about the CODE; you are not asked to
re-run the suites.

## The sharp questions

1. **The grounding check is the headline safety claim. Break it.** The check refuses a drafted rule
   whose trigger words are not on the captured screen. Ask: what makes a trigger word get past it?
   The Architect has already spotted that the whole check is skipped when the example screen is empty
   (`RuleDraftContract`, the `screenToCheck.Length > 0` guard) and that nothing forces a screen to be
   supplied. Confirm or refute that, and then find the ones nobody has spotted - case, whitespace,
   substring matching, a word that matches something the model itself put in the screen, a screen the
   caller supplies that is not the session's real screen at all.

2. **Which check passes when nothing ran?** A check whose pass condition is an ABSENCE - no error, no
   exception, grep found nothing, the list came back empty - certifies a run that never happened. The
   grounding check above is exactly this shape when the trigger word list is empty. Find every one
   and name it.

3. **Does the AUTHORING call ever refuse?** The evaluator's boundary is that it declines. Authoring's
   boundary is that it refuses to draft. Find the tests for each refusal path - unknown check, empty
   scope, empty read-back, empty question, invented trigger word, unparseable answer - and for each,
   check that the test would go RED if that refusal were deleted. A refusal nothing can break is
   decoration.

4. **Where could a constant be substituted and the suite stay green?** Replace a value the tests
   assert on with a wrong one and ask which test catches it.

5. **No generated code, anywhere.** The owner's ruling: NO user-written and NO model-written code
   runs on the Gateway - only reviewed functions we wrote, chosen by name, with argument VALUES
   supplied. The authoring call is a model producing a rule, so this is the highest-risk place in the
   whole feature. Hunt for anything that parses, compiles, evaluates or interprets text at runtime; a
   field or contract shape that COULD carry a program, expression, lambda, pattern or format string
   even if nothing writes one today; and any check whose arguments are effectively a program - a
   regex taking an arbitrary pattern, an expression evaluator under another name. Ask specifically:
   can the model's answer reach `RulePrimitiveCall` arguments in a way that widens what a check does?

6. **Can authoring promote?** The owner's ruling is that an agent credential can do everything except
   promote a rule out of dry run. A drafted rule must be stored as a dry run and no route reachable
   from authoring may arm it. Find the guard, and check what happens if a caller supplies a promoted
   state in the draft or store payload.

7. **Can authoring widen its own scope?** The agent part of the scope is meant to be taken from the
   real session, not from the model's answer, unless the account chose every agent. Check the model
   cannot override it, and check what happens when the origin is unknown.

8. **What is unguarded?** Tenant boundaries on the new routes (a route added to the Gateway is not
   automatically covered by the session-key guard in this repository - check it explicitly), the
   dry-run gate, the cooldown and the daily cap, and whether the new draft route can be reached
   cross-tenant. `CensusRouteTenancyProbeTests` claims to prove this; check it proves it for the NEW
   route and not only the old ones.

9. **The clients are supposed to be dumb.** The Gateway owns every verdict; the page and the command
   line render it. Find any place the page or the command line decides what a state MEANS - composes
   its own refusal text, infers a scope, defaults a ceiling, or re-derives a label - as opposed to
   laying out what the Gateway already decided.

10. **Was each test watched failing first?** A test written after the code, that has never been red,
    proves nothing. Where the report quotes a red, check the quote is of a real run on the commit
    named.

## How to report

- **Write your review to a FILE** on the branch:
  `docs/missions/session-rules-2026-09-02/inspection-d.md`. Commit it and push it.
- **Then reply to the Architect with ONE SINGLE LINE** - fleet messages truncate at the first
  newline, so a multi-line reply arrives as a heading and nothing else. One line: how many findings,
  the worst one in a few words, and the file path.
- Order your findings worst first. For each: what is wrong, where (file and line), why it matters,
  and what would have to be true for it to be fine.
- If you find nothing, say so plainly and say what you looked at - a review with no findings and no
  account of what was examined is not evidence of anything.
- **Do not fix anything.** An inspector who picks up a hammer is no longer an inspector. Findings go
  back to a builder.
- ASCII only. Never name any assistant, model, vendor or AI tool in a commit message, a document or
  a comment.
