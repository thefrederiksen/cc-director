# How to write a rule

This is the plain explanation the owner asked for.

**Read the two headings honestly.** The first describes what the product does TODAY, on the mission
branch, and every sentence in it is true of code that exists. The second describes the way rules are
MEANT to be written, which is designed and ruled but NOT BUILT. An earlier draft of this file mixed
the two and told the owner to press a button that does not exist. That was the exact defect this
mission keeps catching in other people's work, in my own document.

---

## What a rule is

A rule is a standing instruction. It sits there costing nothing until one of your sessions goes idle
with something on its screen that looks like the thing you described. Then an agent reads that screen
and your instruction together, decides what you would have wanted, does it, and writes down
everything it saw and did.

---

## Part one - how a rule is written TODAY

Today a rule is created over the Gateway's own interface, in two calls, and there is still no Rules
page - so this is a description of the machinery rather than of a screen you can use.

**You say what you want and a model works the rest out.** `POST /gateway/rules/draft`, carrying what
has been said so far, comes back with one of three things: a rule to look at, with a plain-English
read-back of exactly what it would do; a single question, when guessing would make the rule do
something you did not ask for; or a refusal saying why it could not draft one. It stores nothing. You
confirm by posting the rule it handed back to `POST /gateway/rules`, unchanged - the two are the same
document on purpose, so what you read and what gets stored cannot differ.

A rule carries:

- **your sentence**, which is the authority and is stored exactly as you said it;
- **the screen description** - what the agent should be looking for, in plain English;
- **the trigger words** - the cheap check that keeps the rule free on the thousands of screens that
  have nothing to do with it. These are worked out from your sentence by the drafting call above;
- **the scope** - which sessions it may touch. This has to be SAID. An earlier version quietly turned
  a missing scope into "every session", which is a fail-open on the widest possible blast radius, and
  an inspection caught it. Name an agent, a repository, a machine or a mission, or say "all sessions"
  on purpose;
- **a cooldown and a daily cap**, both required.

**It is created in dry run and there is no way to create it live.** The create path has no state
argument at all, so that is enforced by the shape of the code rather than promised in a comment.

To make it live, a person promotes it: `POST /gateway/rules/{id}/promote`, carrying who is asking and
what they are agreeing to. An empty request promotes nothing.

You can read the rules with `GET /gateway/rules` and everything a rule has ever done with
`GET /gateway/rules/{id}/firings`.

## Part two - what is still missing

The drafting call exists; **the screen to use it on does not**. There is no Rules page on the desktop
or the phone, so today talking to it means posting to the two routes above. That is the next thing.

Two limits are worth knowing before you rely on a rule, because neither is obvious from the outside.

**A rule only ever looks at the screen of a session that has just stopped.** The only thing that wakes
a rule is a session crossing from working to idle, and the only thing it reads is the tail of that
session's terminal screen. So a rule can act on trouble that PRINTS SOMETHING and hands the prompt
back - which is what a provider error, an allowance notice or a question waiting for an answer all do.
It cannot see a session that hangs in the middle of a turn and never goes idle, and it cannot see
trouble that puts nothing on the screen at all. Those are not rules you can write badly; they are
rules that cannot fire.

**A rule types into a session. It cannot start one that is gone.** "Start it back up" means typing
something into a session that is sitting there idle. If the session itself has died, no rule reaches
it.

The model's own judgement is what a plain word list is not. It has declined a screen carrying its own
trigger words, on a real session, because the words were in something the session was reading rather
than in the session's report of its own state.

---

## What happens the first time a rule matches - nothing

**Every new rule starts in dry run.** It watches, it decides, it records what it WOULD have done, and
it types nothing at all. You look at what it would have done, and if you are happy you promote it.

That is the most important thing to understand about the feature, because it is what puts you between
the instruction and its first real use.

## What bounds a rule

Six things, and none of them is a list of allowed actions:

1. **Scope.** A rule only touches sessions you chose.
2. **Dry run first, always.** As above.
3. **A ceiling** - a cooldown and a daily cap, per rule per session, both required. An agent stuck in
   a loop is the worst thing that can happen here, and the cap is what makes it finite.
4. **Idle only, and it looks again right before it acts.** If the screen changed between deciding and
   typing, it abandons and says so. This has been seen happening on a real session.
5. **A full record of every firing.**
6. **Your instruction is the authority.** It carries out what you wrote. It does not invent goals,
   widen its own scope, edit its own rule, promote itself out of dry run, or write new rules.

## What it will refuse to do, and why that is the honest answer

Nothing you write, and nothing a model writes, ever runs as code on the server. The Gateway ships a
small set of checks we wrote and reviewed - is this path inside that folder, how long until this says
it will be back, how long since this first broke - and the job is to pick one and give it values.
There is no interpreter, so there is nothing to escape from.

The cost is real and you will meet it: **some rules cannot be built exactly.** When your instruction
needs an exactness none of those checks provides, the honest answers are the rule built without that
part and saying plainly what it cannot do, or a refusal saying why. You will not get a rule that
quietly approximates what you asked for. Closing a gap means us writing and reviewing a new check and
shipping it, and that friction is deliberate.

## Reading what happened

Every firing is on the rule: what was on the screen, what the agent understood, what it decided and
why, which check it ran and what that check answered, exactly what it typed, and what happened next.

**A decline is a firing too.** "This was a permission prompt, but for a path outside the repository,
so your rule says leave it" is recorded exactly like an action. That is deliberate: a rule that did
nothing because something crashed would otherwise look identical to a rule that decided not to act.

A rule that has never declined has not been shown to have a boundary.
