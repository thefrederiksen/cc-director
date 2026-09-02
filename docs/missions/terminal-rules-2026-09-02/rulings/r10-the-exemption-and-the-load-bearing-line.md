# Ruling 10 - the exemption is prose; and the comment credits the wrong line

Architect ruling. Small, and neither blocks the queue.

## Read and accepted

The rewrite is the best instrument on this mission. The population is genuinely derived - keys and
unique indexes off `ctx.Model`, string properties only - the hand-written part is inverted into three
exemption sets that each demand an argument rather than deciding what gets looked at, both
directions are asserted, a third assertion catches any collation that is neither `default` nor `C`,
and the rename to `OnEveryStringKeyColumnTheModelDeclares` makes the name describe the check instead
of the list. `Assert.NotEmpty(required)` guarding against a vacuous run was added without being asked
for, which is the lesson of this mission applied to the instrument itself.

Two things remain.

## 1. The `tenant_id` exemption is a claim about the whole codebase, held in a test comment

The argument is good: the Gateway mints tenant ids and every write stamps one from the resolved
tenant context, never from a request body or a push payload, so two spellings cannot both reach the
database and there is nothing for the providers to disagree about.

**But nothing enforces it.** It is prose, in a comment, in a test file - and prose has no exit code.
If one endpoint ever takes a tenant id off a payload, this exemption silently becomes wrong, and what
it is wrong about is the tenant-scoping column, on a schema where `tenant_id` leads almost every
composite key. Tenant scoping is the owner's hard requirement for this whole feature.

An exemption is a hole deliberately cut in a check. The hole is fine; an unverified premise holding
it open is not. So:

- If a test already proves tenant ids come only from resolved context - this repository has several
  tenant-boundary suites - **name it in the exemption comment**, by test name, so the exemption is
  tied to its proof and a future reader can check it in one step.
- If no such test exists, **say the premise is unverified** in the comment. An honest "this rests on a
  convention nothing enforces" is worth more than a confident sentence, because the next person
  weighing whether to widen the exemption can see what they are leaning on.

Do not add a new test for this. Linking or labelling is the whole task.

## 2. The comment credits the wrong line, and the real one looks deletable

Lines 255-257 say the non-empty guard reconciles the derived set against the catalog count, "so a
derivation that silently found nothing cannot pass". `Assert.NotEmpty` does not reconcile anything -
it catches zero and nothing else. A derivation that silently returned three columns instead of
twenty-one would pass it.

What actually catches that is the **`unexpected` block below**: columns that still carry `C` in the
live catalog but have dropped out of `required` surface there immediately. The two-way comparison is
what makes a shrunken derivation loud, and it is doing work the comment attributes to a different
line.

That matters beyond tidiness. The `unexpected` block reads like a nice-to-have - "an accidental
collation should be as loud as a missing one" - so someone trimming this file could remove it as
redundant, never knowing it is the only thing standing between a half-broken derivation and a green
run. Fix the comment at both sites: `NotEmpty` catches the empty case, the reverse comparison catches
the shrunken one, and the reverse comparison is load-bearing and must not be removed.

## Nothing else changes

Your order stands: build, regenerate the Postgres migration, `has-pending-model-changes` to
no-changes on both providers, the idempotency assertion on row 1, then the rig under ruling 8's
throwaway constraints. These two edits ride along with the collation work; neither is worth a
separate pass.
