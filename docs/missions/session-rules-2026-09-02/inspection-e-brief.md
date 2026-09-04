# Session Rules - inspection E brief (fix round D)

## The four fields

- **Landing:** E - fix round D, the round that answers inspection D's ten findings plus ruling D11.
- **Branch to inspect:** `mission/rules-fix-d`
- **Diff to read:** `git diff origin/main...mission/rules-fix-d`
- **The round's own report:** `docs/missions/session-rules-2026-09-02/fix-round-d-report.md`

## Why this inspection exists, and it is not a formality

**A fix round is new writing and carries a new writer's risk.** This mission has already proved that on
itself, twice, and both times the fix was more dangerous than the thing it fixed:

- **Fix round A** hardened promotion so that it required an authenticated caller. Correct in intent.
  It made `RulePromotionGrant` read a request item (`DeviceKeyId`) that nothing anywhere writes - the
  middleware writes `cc.auth.DeviceKey` - so **every promotion over HTTP has been refused as having no
  caller ever since**, and it shipped to `main` in the release that announced the feature. No test
  caught it because the promotion tests construct the grant directly instead of driving a request.
- **The session-key guard** was an allow list that nobody added the rule routes to, so the entire
  agent-facing command line answered 403 while every suite stayed green.

So: **the fix round you are inspecting is exactly the kind of writing that produced both of those.**
Read it that way.

## What was fixed - the shape of the round

Inspection D returned ten findings; the Architect ruled on each in `fix-round-d.md` and added D11. The
largest is D2, a re-architecture rather than a patch: the draft route no longer accepts a
caller-supplied screen at all. It takes a session id, the Gateway reads that session's screen itself,
and the same grounding check runs again at the create route, which is the one write gate.

## BE ADVERSARIAL, AND DO NOT TRUST THE ROUND'S OWN REPORT

That report is self-testimony. It is written by the people who did the work, about their own work, and
it is exactly as persuasive as it is unreliable. It claims each of the eleven rulings is CLOSED and
quotes mutation probes with commit hashes. **Check the code, and check that the probes say what the
report says they say** - the report itself admits its probe commits stacked because a revert step used
a flag that does not exist, so the failure sets grow down the list. That is precisely the condition
under which a quoted red can be attributed to the wrong change.

## The sharp questions

1. **Is grounding now actually unbypassable?** This is the round's headline. The claim is that there is
   no path with an empty screen and no overload without one. Try to find a path to a stored rule whose
   trigger words were never checked against a screen the Gateway read: a create body that names a
   session but is refused-then-retried, a draft accepted while a session dies, a screen read that
   returns something empty and is treated as a pass, a word that matches only because the excerpt
   contains text the model itself wrote. The check must run at BOTH the draft route and the write gate.

2. **Which check passes when nothing ran?** A pass condition that is an ABSENCE - no error, nothing
   found, an empty list, a seam not called - certifies a run that never happened. The grounding check is
   this shape when the trigger word list is empty, and the screen read is this shape when the excerpt is
   empty. Find every one and name it.

3. **The seam that nobody drove.** The report admits `GatewayHost.ReadRuleScreenAsync` - the production
   roster locate and tunnel read - is exercised by NO test, and that every draft-route test substitutes
   the reader seam. Read that code and say what would break if it were wrong: does it locate in the
   caller's tenant, can it return another account's session, what does it do when the Director is gone,
   and can any of its failure modes be mistaken for a successful empty screen?

4. **Was the promotion fix real, and is it now the only thing?** Ruling D11 refuses promotion at the
   grant on the credential, deliberately redundant with the route guard. Check both exist, that the
   grant's test does not depend on the route guard being correct in order to pass, and that a device key
   can now genuinely promote - the previous fix broke that for everyone and no test noticed.

5. **Where could a constant be substituted and the suite stay green?** Inspection D's finding 9 was
   exactly this and the round claims to have closed it with two tenants and two agents. Verify at the
   far side, not at the call site. Then look for the NEXT one.

6. **Do the clients still decide anything?** Ruling D8 says the Gateway stamps the scope and wait
   labels and both clients render the stamped string. Find any remaining place where a page or the
   command line composes product meaning, infers a scope, defaults a ceiling, or turns a missing field
   into a clean empty result.

7. **Are the new bounds enforced where it matters?** Ruling D6 set cooldown at 60 seconds to 24 hours
   and the daily cap at 1 to 100. Check they are enforced at the STORE, not only at authoring, so a
   direct create cannot walk around them.

8. **No generated code, anywhere.** Inspection D cleared this and the round has since rewritten the
   authoring contract. Re-check it: anything that parses, compiles, evaluates or interprets text at
   runtime, or a field or contract shape that COULD carry a program, expression, lambda, pattern or
   format string even if nothing writes one today.

9. **Was each new test watched failing first, on the code it names?** The report quotes probes. Check
   the quoted red belongs to the change it is attributed to, given the admitted stacking. A test that
   has never been red with the reported symptom proves nothing.

10. **What does the report claim that the code does not support?** Take each CLOSED and find the line
    that makes it true. If you cannot, say so.

## How to report

- **Write your review to a FILE** on the branch:
  `docs/missions/session-rules-2026-09-02/inspection-e.md`. Commit it and push it.
- **Then reply to the Architect with ONE SINGLE LINE** - fleet messages truncate at the first newline,
  so a multi-line reply arrives as a heading and nothing else. One line: how many findings, the worst
  one in a few words, and the file path.
- Order your findings worst first. For each: what is wrong, where (file and line), why it matters, and
  what would have to be true for it to be fine.
- If you find nothing, say so plainly and say what you looked at - a review with no findings and no
  account of what was examined is not evidence of anything.
- **Do not fix anything.** An inspector who picks up a hammer is no longer an inspector.
- ASCII only. Never name any assistant, model, vendor or AI tool in a commit message, a document or a
  comment.
