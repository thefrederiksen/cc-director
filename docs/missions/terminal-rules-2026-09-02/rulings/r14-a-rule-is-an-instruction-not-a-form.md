# Ruling 14 - a rule is an instruction you give in English, not a form you fill in

Owner's redesign, 2026-09-02. Supersedes the rule shape in `brief.md` and the action list in
`r12`/`r13`. Rulings 1 to 13 about the SCREEN STORE are untouched - this changes what a rule is, not
where screens come from.

## The owner's words

> This kind of rule seems very rigid and not really something we'd use for a large language model.
> We have a large language model, we have an agent in the background. If we don't, we should, and it
> really should just use that with potentially a little bit of Python to write the rules. If I want
> to create a new rule, I should say it in English, it translates to understand what I want and it
> can build up the search things on the screen to say, don't run unless you see these words or this
> combination of words that triggers it, but then it should be a large language model that should
> run with maybe a little bit of Python to actually create a rule, not this rigid action and what to
> set it to, because it's way too few things we're allowed to do.

## What was wrong with the old design

Three things, and the third is the one that should have been obvious.

**1. The action space was a dropdown.** Six options. "Switch model", "Notify me", "Snooze". A user
who wants *"if it is asking permission to touch something inside my repo, approve it; if it wants
anything outside, wake me"* cannot say that at all. That is not an exotic request - it is the
ordinary shape of what people actually want, and it needs judgement.

**2. Authoring was a form.** The user had to write the model's matching description AND choose the
cost-control trigger words themselves. Those are engineering tasks, handed to the person, in a
product that has a model sitting right there.

**3. The safety claim was partly theatre, and this is the important one.** The old design said
"only things a person could have typed into that session - there is no way to run a command of your
own", as though the action vocabulary were the bound. It never was. **Typing into a coding agent is
already unbounded**: a session running with permissions will do whatever the typed text asks. Six
dropdown entries bought the appearance of a limit and none of the substance. A design whose safety
story is false is worse than one that states the real bound.

## The design

**A rule is a standing instruction, in the owner's own words, carried out by an agent.**

- **You create it by saying it.** Plain English. A model reads it, works out what you meant, and
  BUILDS the rule: the screen condition, the cheap trigger words that keep it from costing anything,
  and the plan for what to do. It shows you what it built, in your language, and you correct it.
  You never write a matching expression and you never pick trigger words.
- **When it fires, an agent carries out your instruction.** It reads the screen and does what you
  asked, composing the action - not selecting from a list. Small deterministic pieces may be
  generated code where that is the right tool; the point is that the action is derived from your
  instruction, not from an enum.
- **The record is the product.** Every firing stores what was on screen, what the agent understood,
  what it decided, what it did, and what changed. Rules that act while you are asleep are only worth
  having if the morning tells you exactly what happened.

## Withdrawn: "the judge answers with a rule id and never supplies text"

That constraint came from the transcription rule, which exists so the SPEAKER'S OWN WORDS reach the
agent unaltered. It does not transfer. A rule is not a transcript of anything - it is an instruction
the owner gave on purpose, and carrying it out is exactly the job. **Withdrawn.**

The transcription rule itself is untouched and stays absolute. This ruling changes nothing about
`dictation_transcripts`, `RawText`, or any speech path.

## The bounds that are real, since the action list is not one

State these and hold them, instead of a fake vocabulary limit:

1. **Scope** - a rule only ever acts on sessions the account chose: this agent, this repo, this
   machine, this mission. That bound is real and enforceable.
2. **Dry run first, always.** A new rule reports what it would have done and types nothing, until
   the owner promotes it. This is the bound that matters most, because it puts a human between the
   instruction and its first real use.
3. **A ceiling.** Cooldown and a daily cap per rule per session, both required. An agent in a loop is
   the failure mode with the worst tail, and the cap is what makes it finite.
4. **Idle only, and re-read before acting.** Unchanged from before and still right.
5. **A full record of every firing** - screen, understanding, decision, action, outcome - readable
   afterwards. An action nobody can reconstruct is an action nobody can supervise.
6. **The instruction is the authority.** The agent carries out what the owner wrote. It does not
   invent goals, widen its own scope, edit its own rule, or create rules.

Bound 6 is the one to write tests against, because it is the one that would decay silently.

## What this changes in the mission

- **Phase 1** is still the rule store and contract, but the stored rule is an instruction plus what
  the model derived from it - not a fixed set of typed fields with an action enum.
- **Phase 2** is no longer "one judge call answering with a rule id". It is the agent that carries
  out an instruction, and its acceptance is about faithfulness to the instruction and refusal to
  exceed it.
- **Phase 4** (authoring from a screen) becomes the natural front door rather than a convenience: the
  screen is context handed to the same conversation.
- The mockups are rebuilt. The old ones are superseded, not annotated.

Phase 0 - the screen store - is unaffected. The fix round continues exactly as ruled.
