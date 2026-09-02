# How to write a rule

This is the plain explanation the owner asked for. It is written from the design and will be checked
against the real screen before it goes into the report; anything in it that the real screen does not
do will be corrected or removed, not left standing.

---

## The short version

**You write a rule by saying what you want, in English. That is the whole of your job.**

You do not write a matching expression. You do not choose trigger words. You do not pick an action
from a list. You say the sentence, it asks about anything genuinely unclear, and it shows you what it
built in your own words before anything is saved.

## What a rule is

A rule is a standing instruction. It sits there costing nothing until one of your sessions goes idle
with something on its screen that looks like the thing you described. Then an agent reads that screen
and your instruction together, decides what you would have wanted, does it, and writes down
everything it saw and did.

## Writing one

Open Rules, press "Tell it a new rule", and say it. For example:

> When I run out of allowance on a model, switch me to Opus and carry on with whatever you were doing.

Or:

> If it is asking permission to touch something inside the repo it is working in, approve it. If it
> wants anything outside that, leave it and wake me.

Then it comes back to you. It will ask about the parts where guessing would make the rule do
something you did not ask for - and only those parts. On the second example it might ask whether a
COMMAND that touches something inside the repo counts the same as a FILE, because you did not say and
the answer changes what the rule does.

When you have answered, it shows you the rule: your sentence, kept exactly as you said it, and
underneath it what it worked out - the screen it will be watching for, and the words that must be on
that screen before it even looks properly. Those words are what keep the rule free on the thousands
of screens that have nothing to do with it. You never have to think about them.

## What happens the first time it matches - nothing

**Every new rule starts in dry run.** It watches, it decides, it records what it WOULD have done, and
it types nothing at all. You look at what it would have done, and if you are happy you promote it.

That is the single most important thing to understand about the feature, because it is the one that
puts you between the instruction and its first real use. Nothing types into one of your sessions
until you have seen what it intends to do and said yes.

## What bounds a rule

Six things, and none of them is a list of allowed actions:

1. **Scope.** A rule only touches sessions you chose - this agent, this repository, this machine,
   this mission.
2. **Dry run first, always.** As above.
3. **A ceiling.** A cooldown and a daily cap, per rule per session, both required. An agent stuck in
   a loop is the worst thing that can happen, and the cap is what makes it finite.
4. **Idle only, and it looks again right before it acts.** If the screen changed between deciding and
   typing, it abandons and says so.
5. **A full record of every firing** - the screen, what it understood, what it decided, what it did,
   what changed.
6. **Your instruction is the authority.** It carries out what you wrote. It does not invent goals,
   widen its own scope, edit its own rule, promote itself out of dry run, or write new rules.

## What it will refuse to do, and why that is the honest answer

Nothing you write, and nothing the model writes, ever runs as code on our server. The Gateway ships a
small set of checks we wrote and reviewed ourselves - is this path inside that folder, how long until
this says it will be back, how long since this first broke - and the agent's job is to pick one and
give it values. There is no interpreter, so there is nothing to escape from.

The cost of that is real and you will meet it: **some rules cannot be built exactly.** When your
instruction needs an exactness none of those checks provides, you get one of two honest answers -
the rule built without that part, saying plainly what it cannot do, or a refusal saying why. You will
not get a rule that quietly approximates what you asked for. If a gap is worth closing, closing it
means us writing and reviewing a new check and shipping it, which is deliberate friction: it puts a
human between a new capability and the code running on the server.

## Reading what happened

Every firing is on the rule. It says what was on the screen, what the agent understood, what it
decided and why, which check it ran and what that check answered, exactly what it typed, and what
happened next. A decline is a firing too - "this was a permission prompt, but for a path outside the
repository, so your rule says leave it" is recorded exactly like an action.

That last part is deliberate. A rule that has never declined has not been shown to have a boundary.
