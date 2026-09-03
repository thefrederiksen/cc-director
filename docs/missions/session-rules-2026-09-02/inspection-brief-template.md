# Session Rules - the inspection brief

The Architect hands this to an independent inspector from a DIFFERENT agent family from the one that
built the work, once per landing. The inspector does not build and does not fix. It reads, it hunts,
and it writes its verdict to a file.

Fill in the four fields at the top and hand the whole thing over.

---

## The four fields

- **Landing:** _A / B / C / D_
- **Branch to inspect:** `mission/session-rules-p<N>`
- **Diff to read:** `git diff origin/main...mission/session-rules-p<N>`
- **The mission's own report:** `docs/missions/session-rules-2026-09-02/phase-<N>-report.md`

## What you are inspecting

A feature called Session Rules. A rule is a standing instruction the account writes in plain English.
A model reads that sentence and builds the rule - a description of the terminal screen to watch for,
the cheap trigger words that keep it from costing anything, and which verified checks to run. When a
session goes idle, cheap code decides whether any rule could apply; if one could, an agent reads the
screen and the instruction together and decides what to do, including declining. It then types into
the session. Every firing is recorded.

Read `docs/missions/session-rules-2026-09-02/brief.md` and `plan.md` on the branch for the design and
the owner's rulings.

## BE ADVERSARIAL, AND DO NOT TRUST THE MISSION'S OWN REPORT

That report is self-testimony: written by the people who did the work, about their own work. It is
exactly as persuasive as it is unreliable. Read the CODE and the TESTS, and treat every claim in the
report as something to be checked rather than something to be summarised.

## The sharp questions

1. **What does this claim that the code does not support?** Take each claim in the report and find
   the line that makes it true. If you cannot, say so.
2. **Where could a constant be substituted and the suite stay green?** Replace a value the tests
   assert on with a wrong one, in your head or in the tree, and ask which test catches it. A test
   nothing can break is not a test.
3. **Which check passes when nothing ran?** A check whose pass condition is an ABSENCE - no error
   logged, no exception thrown, grep found nothing, the list came back empty - certifies a run that
   never happened. Find every one of those and name it.
4. **Is any list hand-kept that could be derived?** Names of primitives, of files, of cases. A
   hand-kept list drifts from the thing it describes and nothing notices.
5. **No generated code, anywhere.** The owner's ruling is that NO user-written and NO model-written
   code runs on the Gateway - only reviewed functions we wrote, chosen by name, with argument VALUES
   supplied. Hunt for anything that parses, compiles, evaluates or interprets text at runtime; a
   column, field or contract shape that COULD carry a program, expression, lambda, pattern or format
   string even if nothing writes one today; and any general-purpose primitive whose arguments are
   effectively a program - a regex taking an arbitrary pattern, an expression evaluator under another
   name. Any of those is a finding.
6. **Does it ever decline?** The rule that matters most is that the instruction is the authority: the
   agent carries out what the account wrote and does not invent goals, widen its own scope, edit its
   own rule, promote itself out of dry run, or create rules. Given a screen an instruction does not
   cover, it must DECLINE and record why. A feature that never declines has not been shown to have a
   boundary. Find the test that proves it, and check that it would fail if the decline were removed.
7. **What is unguarded?** Tenant boundaries, the dry-run gate, the cooldown and the daily cap, the
   re-read of the screen before acting.
8. **Was each test watched failing first?** A test written after the code, that has never been red,
   proves nothing about the code. The report is supposed to quote both the red and the green. Check
   the quotes are of real runs, on the commit named.

## How to report

- **Write your review to a FILE** in the branch:
  `docs/missions/session-rules-2026-09-02/inspection-<landing>.md`. Commit it and push it.
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
