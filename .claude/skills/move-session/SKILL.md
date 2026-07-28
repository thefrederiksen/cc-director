---
name: move-session
description: Move a live session to another Director or slot. A move does not migrate a process - it writes a RIGHT-SIZED handover, starts a fresh session on the SAME agent and SAME model, verifies it picked the work up, and then DELETES the original. Triggers on "/move-session", "move session", "migrate session", "transfer session", "move it to the new director".
---

# Move Session

Relocate work from one Director to another - a newer build, a different slot, another machine.

**A move does not migrate a running process. It recreates the session, and then it DELETES THE
ORIGINAL.** The target starts completely fresh: no transcript, no memory, nothing but one document
you write. The source is destroyed at the end - not parked, not left renamed.

**Deleting the source is the point, not a tidy-up.** A move exists so a Director can be emptied and
shut down. A move that leaves the source alive has failed: the Director still has open sessions and
still cannot be updated.

**Verification is the ONLY gate on that deletion.** The source is deleted if and only if the target
has demonstrably picked the work up (step 4). Nothing else gates it - not the owner, not the clock.
If verification fails, nothing is deleted and the source keeps running. This is why the move has to
be good: the handover document is the only thing standing between the work and the bin.

Proven end to end 2026-07-28: a session moved onto a newer Director read its handover, correctly
restated the work and its constraints, ran the exact next action unprompted, and correctly handled
the failure that action turned up.

## When NOT to move

- **Never round-trip.** "Move it to the new Director now and back later" rebuilds the session twice
  and loses a little each time. Move it where you want it and leave it.
- **Never move a session that is working.** Wait for its turn to finish.
- **Every move costs a fresh context window.** Check usage before moving a fleet.

## The control surface is the GATEWAY, not the Director

The tunnel-only cut removed the per-session control routes from the Director's loopback floor. On
any Director port these now 404 and must not be used: `POST /handover`, `GET/PATCH /sessions`,
`/sessions/{id}/hold`, `/prompt`, `/context`.

Use the **`cc-devthrottle` command line** wherever it covers the step - it is token-free and
address-free, reaching the fleet through your own Director. Drop to Gateway REST
(`http://127.0.0.1:7878`, `Authorization: Bearer <token>` from
`%LOCALAPPDATA%\cc-director\config\director\gateway-token.txt`) only for the composer nudge and the
fallback auto-handover.

The Director loopback still serves `/healthz` - which is how you find a target Director's port and
confirm what it is running.

---

## Step 1 - Capture what the session IS

```bash
cc-devthrottle session list --json
```

Take from the source's record: `sessionId`, `repoPath`, `agent`, `currentModel`, `name`, and its
`directorId`. **The Director knows all of these. Never ask the agent for them** - it can get them
wrong, and a fact you can read is never a fact you request.

## Step 2 - Ask the source for a RIGHT-SIZED handover

Message the source asking for a document at a path you choose. Seven headings:

1. **What I am doing** - the goal in my own words, not a restated task title.
2. **Where I got to** - what is finished and PROVEN, kept separate from what is started or merely
   believed.
3. **The exact next action** - specific enough to act on without asking anyone. Commands and paths,
   not intentions.
4. **Decisions and why** - what a newcomer would otherwise re-litigate or get wrong.
5. **Traps** - dead ends already hit, things that look right and are not.
6. **State** - branch, worktree, uncommitted work, pull requests, issue numbers, running background
   jobs.
7. **What I did NOT verify** - suites never run, platforms never tried, claims taken on trust.

**Heading seven is not optional.** A document listing what passed reads as full coverage and the
reader believes the stronger claim. Learned the hard way: a handover said three test projects
passed - true - while the wider suite had never been built at all, and continuous integration had
in fact failed.

Instruct explicitly: **no secrets.** The first handover produced this way came back carrying a
virtual machine's administrator password.

### Right-sizing - the part that matters most

**A handover is a briefing, not an archive.** Big enough to continue, and nothing more. Aim for
what the next agent reads in about two minutes - roughly a thousand words. If it is longer than the
reader's first turn, it is too long.

CUT, without mercy:

- **Work finished, merged and closed.** It is in the repository history. It cannot be acted on and
  it cannot go wrong.
- **Superseded decisions.** Only the decision that stands matters; the abandoned ones are noise
  unless one is a trap.
- **Exploration that led nowhere** - unless repeating it would waste the next agent's time, in
  which case it is a trap, not history.
- **Transcripts, tool output, full file lists.** The biggest source of bloat and almost no signal.
- **Anything cheaply re-derived.** Do not paste `git status`; name the branch and worktree and let
  the reader run it.

KEEP, always:

- The live frontier - what is true right now and what happens next.
- Anything that would be got WRONG rather than merely be unknown. A gap costs a question; a wrong
  belief costs a day.
- Pointers, not contents. A path, an issue number, a command.

The test before sending: **for each paragraph, what does the next agent DO differently because it
is there?** No answer means cut it.

### If the source cannot write one

A session that is unresponsive or out of context cannot answer. Fall back to the Gateway's
generated summary: `POST {gateway}/handover` with `fromSessionId` + `toRepoPath` (+ `toDirectorId`
for another Director, `toAgent`, `extraContext` opening with the moved-session statement). It
returns `targetSession.sessionId`, and creates the target **unnamed** - name it immediately.

Know what you are trading: that route produces a MECHANICAL SCRAPE - last prompt, last reply, files
touched, commands, to-do items. It carries no intent, no decisions and no traps. Use it when
nothing better is available, not by preference.

## Step 3 - Start the target on the SAME agent and the SAME model

Sessions are created on whichever Director owns you. To land on a different one, override the API
base with that Director's Control API port (read it from its `/healthz`):

```bash
CC_DIRECTOR_API=http://127.0.0.1:<targetPort> cc-devthrottle session spawn "<repoPath>" \
  --standalone \
  --agent <sourceAgent> \
  --name "<the source's ORIGINAL name>" \
  --purpose "continue <work> after being moved" \
  --prompt "<seed - below>"
```

The target keeps the source's original name. That is what makes the move invisible afterwards. Keep
the naming convention `<repo-dir> - <short description>`; if the source's name lacks the repo
prefix, add it and say so.

**On the model - do not assume, read back.** With `--args` omitted the session inherits the
Director's default model and permission mode. That is usually right and is silently wrong whenever
the source was NOT on the default. Two things to know:

- The reported model (`claude-opus-5`) is **not** the same vocabulary as the `--model` launch flag
  (`opus[1m]`). You cannot feed one into the other; map it deliberately.
- `--args` is passed through LITERALLY and applies NO defaults, so if you set it you must supply the
  permission preset too or the session blocks on a prompt for every action:
  `--args "--dangerously-skip-permissions --model <id>"`.

Then **verify by reading back**, never by trusting the flags you sent:

```bash
cc-devthrottle session list --json    # compare the target's agent + currentModel against the source
```

If they differ, stop. Delete the target and start again. A session continuing on a different model
is not the same session.

Identity is set at spawn or not at all - if the source carried a role, mission, or workflow seat,
pass `--role`, `--mission`, `--workflow-run`.

### The seed prompt

Tell it what it is, point it at the document, make it prove it read it:

> YOU ARE A MOVED SESSION. You are the continuation of a session that ran on another Director. You
> have no transcript and no memory of that work - everything you know is in one document. Read it
> now and treat it as your own history: `<path>`. Read the WHOLE document before doing anything, and
> follow its "exact next action" section - including where it tells you to wait and NOT to act. When
> you have read it, reply with a short summary of what you are working on and what your very next
> action is, so the move can be verified - then get started on that action and nothing more.

## Step 4 - Verify the pickup BEFORE touching the source

```bash
CC_DIRECTOR_API=http://127.0.0.1:<targetPort> cc-devthrottle session buffer <targetId>
```

It **passes** when the target states the work, its constraints and the next action **from the
document**, then does that action. It **fails** when it guesses, asks what it should be doing, or
starts something the document assigned to someone else.

**If it fails, stop. Leave the source alone and fix the document.** The source is still alive and
still holds everything - this is the last moment at which that is true.

Parked-composer gotcha: state stuck `WaitingForInput` with the seed visible at the prompt means the
submit never fired. Send a raw Enter through the Gateway -
`POST {gateway}/sessions/{tid}/prompt` `{"text":"\r","appendEnter":false}` - then re-verify. (Empty
text with `appendEnter:true` is a 400.)

## Step 5 - Delete the source

Step 4 passed, so the work now lives on the target. Destroy the original. **Do not ask - the owner
asked for the move, and a move includes this.** Asking here is how a Director ends up full of parked
`[MOVED]` sessions and can never be shut down.

```bash
cc-devthrottle session rename <sourceId> "[MOVED -> <where>] <original name>"
cc-devthrottle message send   <sourceId> "Moved and verified. Do nothing further; you are being closed."
cc-devthrottle session done   <sourceId>     # the Director reaps it shortly
```

The rename is for the second or two it remains visible, so anyone watching sees why it vanished.

**If step 4 did NOT pass, delete nothing.** Leave the source running and untouched, delete the
failed TARGET instead, and fix the handover document. A source deleted behind a target that cannot
continue is the one unrecoverable outcome this skill exists to prevent.

## Step 6 - Report

Give the owner: the target's id, Director, repository, retained name, and the proof it picked up;
confirmation that the source is deleted and the old Director's session count is one lower; and
anything the document admitted it had not verified.

When the point of the move was to empty a Director, say how many sessions it has left.

---

## Traps

- **The source cannot tell you what it never checked** unless you ask. That is heading seven.
- **Background jobs do not survive.** A monitor watching a build dies with the source. The document
  must say so and give the command to re-check by hand.
- **Scratchpad files do not survive.** Screenshots and working notes in the source's temporary
  directory are gone. Name how to regenerate them, or move them somewhere durable first.
- **`session hold` queues mid-turn.** "still working; it parks when it finishes" is success.
- **Do not start a Director from your own process.** Use the launcher's `POST /launch` (port 7900,
  token from `config\launcher\launcher.json`) - it gives clean parentage. A Director started inside
  an agent's console hosts sessions that die within seconds.
- **The `[MOVED]` marker must never lie.** If you marked the source and the move actually failed,
  rename it back and say so plainly.
- **A parked source is a failed move.** If you find yourself leaving the source alive "to be safe",
  you have not moved anything - you have duplicated it, and the Director you were emptying is still
  full. Either verification passed, in which case delete, or it did not, in which case fix the
  document and try again.

## Note for the future

Written to be lifted into the central Gateway skill library. Keep it self-contained and free of
machine-specific paths so it can be served unchanged to every agent.

---

**Skill version:** 4.0 · **Updated:** 2026-07-28
**Changes in 4.0:** A move now DELETES the source automatically, gated on verification and nothing
else. This reverses 3.1's "never kill the source - the user closes it", which defeated the purpose:
the reason to move sessions is to empty a Director so it can be shut down and updated, and a parked
`[MOVED]` session leaves it just as full. Verification is the only gate, which is what makes the
handover document critical - it is the only thing between the work and the bin.
The default route is now an AGENT-WRITTEN, right-sized handover with seven headings, proven in a
real move; the Gateway `POST /handover` auto-summary is demoted to a fallback for a source that
cannot answer, with its mechanical-scrape limitation stated. Added: the right-sizing rules and the
per-paragraph test; heading seven (what was not verified) after a real document read as full
coverage while the wider suite had never run; a no-secrets instruction after one came back carrying
a password; same-agent and same-model spawn with a READ-BACK check, and the warning that the
reported model id is not the `--model` flag vocabulary; creating the target on a chosen Director
with `CC_DIRECTOR_API`; and the launcher launch route.
Kept from 3.1: the Gateway-is-the-control-surface preamble, the naming convention, and never
touching the source until the target has demonstrably picked up.
