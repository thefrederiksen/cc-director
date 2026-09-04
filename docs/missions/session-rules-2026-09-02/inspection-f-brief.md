# Session Rules - inspection F brief (fix round E)

**Scope this TIGHTLY. One question per ruling: is it really closed, or has it moved?**

## The four fields

- **Landing:** F - fix round E, the round answering inspection E's four findings.
- **Branch to inspect:** `mission/rules-fix-d`, tip `5a4075b41`.
- **The diff to read:** the fix round E commits only - from the merge that carried inspection E onto
  the branch, to the tip. Not the whole feature; inspection E already read that.
- **The round's own report:** `docs/missions/session-rules-2026-09-02/fix-round-e-report.md`, and
  `inspection-e.md` beside it for the findings being answered.

## Why this exists, and it is the opposite of a formality

**A hardening at a boundary is the single most dangerous thing this mission has written, twice.**

Fix round A hardened promotion to require an authenticated caller. Correct in intent. It made the grant
read a request item nothing writes, so **every promotion over HTTP failed from that day until this
mission found it** - shipped, in a release, with every suite green, because the promotion tests
construct the grant directly instead of driving a request.

**Ruling E1 is the same shape**: a new invariant at the persistence boundary, requiring non-forgeable
evidence, guarded by a database context flag. It is well built - evidence is bound to the session AND to
the exact words, single use, spent by the write it was minted for. That is exactly the kind of thing
that works perfectly in its own tests and refuses something real.

**So the sharpest question in this inspection is not "can a rule be stored ungrounded". It is: what
LEGITIMATE write does this invariant now refuse?**

## The questions, one per ruling

1. **E1 - what does the invariant break?** Find every path that writes or updates a rule and ask whether
   it can still mint or carry evidence: the create route, promotion, any update to a stored rule's
   words, the evaluator recording a firing, migrations, any seeding or test helper, and anything the
   demonstrations will do. **A legitimate write that now refuses is a defect of exactly the shape fix
   round A shipped.** Then confirm the control test genuinely reaches storage through the real grounded
   route, so the guard cannot pass by refusing everything.

2. **E1, second half - is the evidence really unforgeable and really single use?** The constructor is
   private and minting is internal, so the boundary is assembly-level. Ask what else in that assembly
   can mint. Ask whether `TryConsume` is checked on every path that persists, or only on the one the
   test drives. Ask whether a failed write spends the evidence and strands a legitimate retry.

3. **E2 - do the clients now reject what they should, and still accept what they must?** The round
   claims runtime shape checks in both clients with null, wrong-type and malformed-child tests beside
   controls. Verify the valid controls are real - a validator that rejects everything passes every
   negative test. Then look for the shapes nobody tested: a valid record with one unexpected extra
   field, a number where a string belongs inside a record, an empty array where the page then indexes.

4. **E3 - does the extracted reader keep the production code in the path?** `GatewayRuleScreenReader`
   was extracted so it could be tested. Check the host actually delegates to it rather than keeping a
   second copy, and that the hosted test fixture no longer replaces the very thing under test. The five
   cases must include **a vanished Director distinguished from an empty screen** - if those two are
   indistinguishable, a failure looks like a session with a blank terminal, which is a state a rule can
   legitimately be authored against.

5. **E4 - does the evidence document agree with the branch?** The placeholder is replaced with a
   statement that the full parked suite was not completed by that seat, plus five chunks green at a
   named commit. Check the named commit and the chunk counts are real and that no other claim in either
   report still rests on a run nobody finished.

6. **The round's own red-first evidence.** It names commit `82ec0b65a` for its reds and four reverted
   probes, and it has a section listing tests that were NEVER watched red, stated plainly. Verify that
   list is complete rather than convenient: find a load-bearing test in this round that is not on it and
   was not watched failing.

## How to report

- **Write your review to a FILE** on the branch:
  `docs/missions/session-rules-2026-09-02/inspection-f.md`. Commit it and push it.
- **Then reply to the Architect with ONE SINGLE LINE** - fleet messages truncate at the first newline.
  How many findings, the worst one in a few words, and the file path.
- Worst first. For each: what is wrong, where (file and line), why it matters, and what would have to be
  true for it to be fine.
- **If you find nothing, say so plainly and say what you looked at.** A review with no findings and no
  account of what was examined is not evidence of anything. Finding nothing is an acceptable outcome
  here; manufacturing a finding to look thorough is not.
- **Do not fix anything.** An inspector who picks up a hammer is no longer an inspector.
- ASCII only. Never name any assistant, model, vendor or AI tool in a commit message, a document or a
  comment.
