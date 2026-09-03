# Session Rules - inspection C, the brief

Written ahead of time by the Architect so the inspection can be seated the moment fix round B
reports. Scoped DELIBERATELY TIGHTLY: this is the third inspection of the same landing, and a
review that re-reads everything from the start each time costs more than it finds.

**Read `inspection-brief-template.md` first** - the conduct, the sharp questions, and how to report.
This file only says what is different about inspection C.

---

## What is in scope

**Only the fix round B diff**, plus whatever it touched:

    git diff <fix round B base>...<fix round B head>

Phases 1 and 2 have each been inspected once and fix round A has been inspected once. Do not re-open
findings that were already disposed of unless the fix round B diff DISTURBED them.

## The one question, asked twelve times

`inspection-b.md` lists twelve findings, worst first. `fix-round-b-report.md` says what the mission
claims it did about each. **Do not read the second and summarise it. Take each finding from the
first, go to the code, and answer one question:**

> Is this really CLOSED, or has it MOVED?

A fix that relocates a defect is the most common outcome of a fix round and the hardest to see,
because the report describes the fix and not the residue. Specifically:

- **Finding 2, the promotion boundary.** The previous repair produced a public factory that proved
  only that two caller-supplied strings were non-blank. If the new evidence can still be constructed
  by production code that is not an authenticated request handler, the boundary is still a
  convention. Try to mint it.
- **Finding 3, the ceiling race.** The previous inspector proved this by synchronising two passes and
  watching both act with a daily cap of one. Reproduce that shape against the fixed code. A lock that
  serialises the pass but not the cap read, or that is per-process while the record is shared, has
  not closed it.
- **Finding 4, the record written after the keystroke.** Check the failure paths, not the happy one:
  if the record is written first and the keystroke throws, does the record still say what happened?
- **Finding 1 and finding 11, the evidence and the runner.** This is the THIRD time red-first
  evidence has been found unreproducible on this mission. Verify by CHECKING OUT the named commits
  and RUNNING the named exact command. Do not read the table. If a row still names a working tree
  rather than a commit, that is a finding on its own.

## The standing question that outranks the twelve

**Which check passes when nothing ran?** This mission has now found that shape three times - a
filtered run collecting zero tests and exiting 0, a guard that could not see inside async methods
and reported that as clean, and a guard that saw only one namespace. Each time it was fixed and each
time another instance was already sitting somewhere else. **Assume there is a fourth and go and find
it.** An empty result is a broken instrument, never a clean run.

## The owner ruling to keep hunting

No user-written and no model-written code ever runs on the Gateway - only reviewed functions we
wrote, chosen by name, with argument VALUES supplied. Anything that parses, compiles, evaluates or
interprets text at runtime is a finding, as is any contract shape that COULD carry a program,
pattern or format string even if nothing writes one today.

## What a PASS means here, and say it plainly

If you would let this land, say so in those words and say what you ran to earn that. If the answer
is that the remaining findings are real but not blocking, say WHICH and why - the Architect is going
to have to write what is not proven into a report the owner reads, and a finding you thought was
minor is better in that list than absent from it.

## Reporting

Write to `docs/missions/session-rules-2026-09-02/inspection-c.md`, commit on branch
`inspect/session-rules-c`, push it.

**Then send the Architect ONE SINGLE LINE - and send it BEFORE you stop.** A previous inspector on
this mission finished its review, pushed it, and was reaped without ever reporting; the mission sat
idle for over two hours because the Architect was waiting on a message that was never coming. One
line: how many findings, the worst in a few words, whether you would let it land, and the file path.

ASCII only. Never name any assistant, model, vendor or AI tool anywhere.
