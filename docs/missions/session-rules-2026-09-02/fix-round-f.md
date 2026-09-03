# Fix round F - the Architect's rulings on inspection F

Inspection F read fix round E and returned **two findings: one high, one medium**
(`inspection-f.md`). Both accepted. Both were proven with executable probes, not asserted.

**First, what it CLEARED, because that is load-bearing.** The sharp question I set was not "can a rule
be stored ungrounded" but "what LEGITIMATE write does the new invariant now refuse" - the shape that
broke promotion for months under fix round A. The answer, checked rather than assumed:

- The production writer inventory is exactly three: create, promote, delete. The evaluator writes firing
  records, not rule rows; there is no seeder and no update route.
- **The feared refusal did not reproduce.** Promotion of a loaded rule and deletion both persisted, so
  the primitive collection's value comparer does not report unchanged trigger words as modified. The
  builder's own parting suspicion was investigated and is clear.
- `TryConsume` is reached only by create, and no current route retries with the same object, so a spent
  token strands nothing.
- `GatewayRuleScreenReader` is the production composition and the host delegates to it, with a vanished
  Director distinguished from an empty screen.
- The E4 numbers are real and production plus tests are byte-identical from the reported green commit to
  the tip.

So E1's invariant does not break anything legitimate. It just has a hole in it.

---

## RULING F1 - the evidence must be an immutable SNAPSHOT, not a read-only view of a mutable list

Finding 1, and it is the whole invariant. `Minted` puts the normalised words in a `List<string>` and
exposes that same object as `IReadOnlyList<string>`. **`IReadOnlyList<T>` is a read-only VIEW, not an
immutable collection** - a caller casts it back to `List<string>` and edits it. `Covers` then compares
the words being stored against the evidence's *current* contents rather than an immutable record of what
was actually found on the screen.

The probe minted evidence for one phrase, cast, replaced the element with a phrase never on the screen,
and called `SessionRuleStore.Create`. **The row was written.** The structural guard stayed green because
the caller neither constructed nor minted a second token - it edited a valid one.

- **Hold a private immutable snapshot** taken at minting. Anything exposed is immutable or a defensive
  copy. Compare against the snapshot, never against anything a caller can reach.
- **The regression test mutates the exposed collection after minting and proves BOTH halves**: the
  changed word is refused, AND no row is written. Assert the row count, because an exception thrown after
  a write is not the same as a write that never happened.
- **Sweep the rules surface for the same pattern.** Anywhere else a checked-then-trusted value is exposed
  as `IReadOnlyList<T>`, `IEnumerable<T>` or an array over a mutable backing store, name it in the report
  even if you conclude it is harmless. This trap is not specific to this type.

## RULING F2 - a required child that is absent is a broken answer, and is NEVER filled in

Finding 2. The wire contract requires all four scope children and the Gateway projects all four, but the
browser client treats `undefined` exactly like a legitimate `null`, and the command line checks a scope
part's type only when the key exists. So a response omitting `scope.agent` is accepted by both - and the
browser client **manufactures `agent: null`, which is the WIDEST value that part can have.**

- Both readers require each of the four keys, then accept only a string or an explicit null.
- **Never invent a value for a missing field.** Absence is a broken instrument, and inventing the
  permissive value for it is the worst available guess.
- The malformed-child matrix gains a missing scope child, beside a valid non-empty control.
- Note the internal contradiction this produced and make sure it cannot recur: the Gateway's stamped
  `scopeLabel` could say a rule is narrow while the reconstructed scope said that part is unrestricted.
  **When the Gateway has already stamped a label, the client renders the label** - that is ruling D8 and
  it is still the rule.

## RULING F3 - sweep for the CLASS, because this is the fourth instance of it

**Stop fixing these one at a time.** Four times now, in one feature, an absent or unreadable value has
been turned into a permissive or positive one:

| Where | Absence became |
| --- | --- |
| A write with no scope (fix round A) | Every session |
| A draft with an empty screen (ruling D2) | Grounding skipped entirely |
| A present-but-null `rules` field (ruling E2) | "No rules yet" |
| A missing `scope.agent` (this round) | `agent: null`, unrestricted |

That is not four slips, it is one habit. **Sweep the whole rules surface** - the Gateway projection, both
clients, and the store - for every remaining place where a value that is absent, null, empty or
unreadable is converted into a value that means something. For each, say whether it is correct and why.

**Report the sweep's METHOD and its COVERAGE, not just its result.** "I looked and found none" is itself
an absence-shaped claim and this mission does not accept those: say what you enumerated, how you
enumerated it, and what would have been missed. If the enumeration can be derived from the code rather
than hand-kept, derive it.

---

## The gate

- `.\scripts\test-local.ps1` green.
- `Gateway.Tests` **filtered to your change only** - ruling E4 stands, no seat runs the full parked suite.
- Every web workspace and `npm run typecheck`, because F2 touches both clients.
- `pytest tools/cc-devthrottle/tests/`.
- Watch every load-bearing test fail first, with the command output and the broken commit quoted, and
  revert each probe before the next. Keep the "never watched red, stated plainly" section - it is the
  best thing in the last report.
- ASCII only. No mention of any assistant, model, vendor or AI tool in a commit message, a document or a
  comment.

## How to finish

Push on `mission/rules-fix-d` and report to the Architect in ONE SINGLE LINE. Write the detail to
`fix-round-f-report.md`, one section per ruling. Do not open a pull request and do not merge.
