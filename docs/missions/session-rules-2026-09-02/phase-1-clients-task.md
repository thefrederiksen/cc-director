# Phase 1 Worker task - show the text a rule types, on the page and on the command line

You are a Worker on the Session Rules mission, phase 1. The Manager's phase branch is
`mission/rules-p1`; cut your own worktree from it and work there:

```
git fetch origin
git worktree add ../devthrottle-rules-p1-clients -b mission/rules-p1-clients origin/mission/rules-p1
```

Conduct: `cc-devthrottle workflow instructions mission`. Commit on your branch and push it; do not open
a pull request and do not merge. Report to the Manager (session 773641bf) in ONE line, naming the
commit. ASCII only. Never name any assistant, model, vendor or AI tool in a commit message, a document
or a comment.

## Why

A rule now carries the exact text it types, decided when the rule is written (phase 1, already done on
the Gateway on this branch). The keystroke is the most consequential thing a rule does, and the read-back
is what a person confirms - a read-back that describes the situation but hides the keystroke asks
somebody to approve an action they were not shown. So both clients must SHOW the exact text, verbatim,
as the Gateway serves it.

## What the Gateway now serves (already on the branch, tested)

- `GET /gateway/rules` and `GET /gateway/rules/{id}`: each rule carries `textToType` (a string). It is
  empty on a rule stored before this field existed; such a rule is refused out loud by the evaluator
  until it is re-authored, and the write gate refuses to promote it.
- `POST /gateway/rules/draft`: the proposal's `rule` body carries `textToType`. Posting that body back
  unchanged stores it; a body without it is refused by the Gateway with a sentence.
- See `src/CcDirector.Gateway/Api/SessionRuleWire.cs` and `SessionRuleWireTests.cs`.

## The work

1. **client-core** (`packages/client-core/src/rules/rulesClient.ts`): `SessionRule`, `RuleWriteBody`
   and the proposal's rule carry `textToType: string`. The draft reader treats a missing `textToType`
   on the proposal's rule the way it treats a missing `scopeLabel` - an error naming the field, never an
   empty string. Test it in `rulesClient.test.ts`.
2. **Cockpit Rules page** (`apps/cockpit/src/rules/RulesView.tsx`): the proposal card shows the text
   as its own labelled line, prominently, before the trigger words - it is what the person is agreeing
   to. Each stored rule's card shows it too. A rule whose text is empty shows, in the Gateway's own
   words, that it needs re-authoring (do not compose product meaning on the client beyond that one
   label; repository rule 7). Render the string verbatim in a monospace element. Tests in
   `RulesView.test.tsx` assert the exact text is on the page for a proposal and for a stored rule.
3. **Command line** (`tools/cc-devthrottle/src/rule_ops.py`): `_describe` prints a `types` line with
   the text verbatim; `rule draft` prints it under the read-back. Tests in
   `tools/cc-devthrottle/tests/test_rule_ops.py`.
4. Run every web workspace's tests and typecheck, and `pytest tools/cc-devthrottle/tests/test_rule_ops.py`.
   The repository's local gate runs neither, so you are the only run these get.

## Watch each new test fail first

Write the assertion, run it, quote the red line in your commit message, then make it green. A test that
has never been watched failing is decoration.

## Out of scope

Anything on the Gateway. Anything on the mobile app (the Rules page is Cockpit only). Do not touch the
harness or the corpus.
