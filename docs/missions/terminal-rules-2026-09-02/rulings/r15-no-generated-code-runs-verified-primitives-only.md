# Ruling 15 - no generated code runs on the Gateway; the model picks from primitives we wrote

Owner's security direction, 2026-09-02, and the design that answers it. Supersedes the
"maybe a little bit of Python" part of ruling 14. Everything else in r14 stands.

## The owner's concern

> If we run code on the Gateway, that's not great, right, because we really have to trust that code.
> Because it's user created, users definitely cannot see the code, and they cannot change the code.
> And we need some very strict security to be allowing users to run this kind of stuff on our
> server. And the way that it's set up and the way that it's defined has to be with a large language
> model. So there's limits to what the user is allowed to do.

Correct on every point, and the mockup I showed him was worse than he knew: it displayed the
generated function in the rule editor as though it were a field, which invites exactly the surface he
is saying must not exist.

## The ruling

**No user-written code runs. No model-generated code runs. Only code we wrote and audited runs.**

The Gateway holds a small set of **verified primitives** - ordinary reviewed functions in the
codebase, shipped like any other feature. The model's job is to **choose one and supply its
arguments**. It never emits a program, an expression, a lambda, or a snippet to be interpreted.

```
is_path_inside(target, root)          -> bool
retry_delay_from(screen_text, now)    -> seconds or none
elapsed_since(first_failure, now)     -> seconds
matches_any(text, terms)              -> bool
extract_first(screen_text, kind)      -> a path, a duration, a timestamp, or none
```

A rule's derived part is therefore not source code. It is a **call**: a primitive's name plus
arguments, stored as data, validated against that primitive's signature before it is ever run.

## Why this is stronger than sandboxing generated code

A sandbox is a promise that something hostile cannot get out. This removes the hostile thing:

- **There is no interpreter.** Nothing parses or executes text at runtime, so there is no escape to
  find, no resource limit to tune, and no new class of vulnerability introduced by a feature that
  types into people's sessions.
- **The trust question disappears.** The owner's objection was "we have to trust that code". We do
  not have to trust it, because we wrote it and reviewed it. The model supplies arguments, and
  arguments are validated data.
- **The blast radius is a signature.** The worst a wrong model call can do is pass wrong arguments to
  a pure function. That is a wrong answer, not a compromised server.
- **It is testable in the ordinary way.** `is_path_inside` gets the unit tests it deserves once,
  including the `..` and symlink cases, rather than each generated variant being unreviewed.

## What the user sees, and does not

- The user writes **English only**, always. There is no field anywhere that accepts code.
- The generated call is **not presented as an editable artifact**. The rule may say in plain words
  "I check the path is inside the repo, resolving `..` and symlinks first" - that is a description,
  not a snippet, and it is not editable.
- Which primitive was used and with what arguments **is recorded in the firing record**, because an
  action nobody can reconstruct is an action nobody can supervise. Visible after the fact in an audit
  trail is not the same as an editable field before the fact.

## The cost, stated rather than hidden

**Some rules will not be expressible.** When an instruction needs an exactness no primitive provides,
the honest outcomes are:

1. Build the rule without that part and say plainly what it cannot do, or
2. Refuse to build it and say why, or
3. Add a primitive - which is a product change, written and reviewed by us, shipped in a release.

**Never route around it.** Not by asking the model to approximate the exact thing in prose, and not
by adding a general-purpose primitive whose arguments are effectively a program - a
`run_expression(expr)` or a regex primitive taking an arbitrary pattern is the interpreter coming
back with a different name. A primitive is narrow, named for a concept, and its arguments are values.

Option 3 is the intended path for real gaps, and the friction is deliberate: it puts a human review
between a new capability and the code that runs on our server.

## What binds the model instead

Since the user's limits are enforced through the model rather than through a form, the model's own
boundary is now load-bearing (r14's limit 6, restated with teeth):

- It chooses from the primitive set and cannot name anything outside it - validated by us on the way
  in, not trusted on the way out.
- It cannot add, edit or delete rules, change a rule's scope, or promote a rule out of dry run.
- It declines when an instruction is not covered, and the decline is recorded.

A rule that never declines has not been shown to have a boundary. That is the test.

## Consequences

- `creating-a-rule.html` is corrected: the editable code block goes; the rule states in words what it
  checks.
- Phase 1's stored rule shape holds a validated primitive call, not a code string. A migration that
  can store a code string is a mistake even if nothing writes one today.
- Phase 2's acceptance gains a negative control: an instruction that would need an unavailable
  primitive must produce a REFUSAL with a reason, not a rule that quietly approximates it.
