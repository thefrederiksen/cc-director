# Mission Seed: Injected Text

Written 2026-07-16 by the Architect of the Gateway SQLite mission (session 8c17dc1c), handing this
to the Architect of the Injected Text mission. This is the seed, not the brief. **You write the
brief.** Everything here came from the owner directly; where I am guessing, I say so.

## The WHY - and it is a consent problem, not a feature gap

**DevThrottle silently injects text into every agent session it launches, on every machine. There is
no way to see it, no way to decline it, and it is not documented anywhere a user would look.**

That was tolerable while the text was "here is your session id and how to reach the fleet" - useful
plumbing nobody would object to. It stopped being tolerable on 2026-07-15, when I added an
**editorial policy** to it: a rule forbidding agents from putting their name on the owner's commits
(#1696, `FleetPreamble.cs`). The rule itself is the owner's and he wants it. But adding it turned an
infrastructure detail into a **policy channel**, and that is a change in kind, not degree.

The owner's reaction, verbatim: *"do we inject shit into the agent terminals? I'm not sure I'm okay
with that... if we do, we need to make it very clear in our documentation. And we might want to let
the user override this."*

**The point of this mission is to make disagreeing with us a supported action rather than a fork.**
Someone may genuinely want their agent to sign its commits as Claude Code. On their machine, that is
their call, and today they cannot make it.

**Read this next part carefully, because it sets your success bar:** the owner has explicitly NOT
decided whether the no-attribution policy should remain injected at all. He said he will decide
*after* this feature exists. So you are not defending that policy - you are building the thing that
makes it a choice. If your work makes it easy for him to delete my rule, you have succeeded, not
failed.

## What the owner asked for

His words, condensed but not reinterpreted:

1. **A new Settings tab called "Injected text."** It shows the user exactly what we inject.
2. **Let the user customize it.**
3. **If they customize it, the UI must clearly show it is THEIR text and not ours.** A user running
   custom text must never be able to believe they are on ours.
4. **We keep shipping our version from the repo, deployed with the application.** "The application
   knows what to inject and puts it in the file."
5. **If they customize, they lose our updates** to that text.
6. **Our updates are always downloaded and always there - just not used.** So a user on a custom
   version can always see the current default and adopt it if they want.
7. **Variables.** The session id and friends appear as editable placeholders - his example was
   `[SESSION_ID]`-style square brackets. The user must be able to edit the prose around them and
   still have the session id injected.
8. **Yours or ours. No merging.** He said "the simpler way" deliberately - do not invent a
   three-way reconciliation.

## What this means in code, so you do not spend a day rediscovering it

All verified against `origin/main` at `9ff2c640` on 2026-07-16. **Re-verify before you rely on any
of it** - main moves, and stale citations have cost this repository real time.

- `src/CcDirector.Core/Sessions/FleetPreamble.cs` - the text itself. Today it is **built in C# with
  string interpolation**. That is the crux of the work: to be user-editable it must become a
  **template with placeholders, rendered by substitution**. That is not a small refactor and its
  tests (`FleetPreambleTests`) assert on the built string.
- `src/CcDirector.Core/Claude/ClaudeHookInstaller.cs` - how it reaches Claude: a `SessionStart` hook
  returning `additionalContext`, via a settings file passed with `--settings` that **merges** with
  the user's own hooks rather than replacing them. It is a documented extension point, not
  keystrokes into a terminal. Say so in the documentation - the distinction matters and users will
  assume the worse thing.
- `src/CcDirector.Core/AgentPlugins/AgentPluginMetadata.cs` - `FleetPreambleStrategy`: `None`,
  `NativeHook` (Claude, Codex, Gemini, Cursor), `EventBus` (OpenCode), `Extension` (Pi),
  `InstructionFile` (Grok, Copilot). **One text, four delivery mechanisms, seven agents.**
- `src/CcDirector.Core/Codex/CodexHookInstaller.cs`, `src/CcDirector.Core/Pi/PiPreambleWriter.cs` -
  the other installers.
- `GET /sessions/{sid}/fleet-preamble` - the Control API endpoint non-Claude agents use.
- Tests: `FleetPreambleTests`, `FleetPreambleEndpointTests`, `PiPreambleWriterTests`.
- **`None` means "this agent cannot inject", NOT "the user declined."** There is no opt-out today. I
  checked.
- **It is not in the public documentation.** The only mention is `docs/public/api/01-control-api.md`,
  an endpoint description. A user has no way to learn this happens.

## Three questions the owner has NOT answered - they are yours to settle or to ask him

1. **Scope across agents.** One text, four mechanisms, seven agents. Does a custom version apply to
   all agents at once, or per agent? I would start with all-at-once because it matches "yours or
   ours", but I am guessing.
2. **The boundary of the tab.** Is "Injected text" only the fleet preamble, or *everything*
   DevThrottle puts into an agent's context? My opinion: a tab called "Injected text" that quietly
   omits some injected text is a worse lie than no tab. Find out what else we inject before you
   scope this.
3. **The self-harm case.** If a user deletes the fleet commands from their custom text, their agents
   lose fleet awareness and will not know how to reach each other. That is their right. The tab
   should probably show them that is what they are doing rather than let them discover it later.

## How this mission runs

- **A Codex reviewer session gates every commit.** Spawn it as a REAL DevThrottle session
  (`--agent Codex`), not a background process. It reviews code BEFORE you commit it, and it writes
  its reviews to files under `docs/architecture/` - never to its terminal, because the session
  terminal buffer captures only spinner redraw and the findings are unrecoverable from it. The
  owner's words: this "usually has given us better quality code." He is right; on the last mission
  Codex found defects nobody else did.
- **One worktree for the whole mission:** `D:\ReposFred\devthrottle-injected-text`, branch
  `feat/injected-text`, cut from `origin/main` at `9ff2c640`. Never use the shared checkout
  `D:\ReposFred\devthrottle`.
- **Commit freely in the worktree.**
- **DO NOT push and DO NOT merge to origin/main without notifying the owner FIRST.** He wants to be
  told before anything reaches origin. This is his explicit instruction and it is the one hard gate.
- **No agent attribution in anything you write** - no `Co-authored-by` naming any assistant, no
  "Generated with" line, in any commit, pull request, issue or document. This is the owner's
  standing rule and it now ships in the preamble you are about to make editable. Check your own text
  before every commit.
- Plain ASCII everywhere. Plain English, no abbreviations.

## Learn from how the last mission went wrong

I am the Architect who just spent a full day on the SQLite mission and produced a superb design
document and almost no working code. **Do not repeat it.**

- **Ship increments to main; do not write a thirty-revision brief.** My brief went through fifteen
  revisions before a line of product code existed. Several revisions caught real defects. The
  aggregate was still a failure, because the owner wanted stats in a database and got a document.
- **Check the premise before doing the work.** I spent most of that day designing a careful
  migration to preserve the owner's old data. Late in the day he said, unprompted, that he did not
  want it kept - which deleted the hardest half of the design. I never asked. Ask.
- **The owner is the only source of the owner's intent.** When you are guessing what he wants, say
  you are guessing, and ask - one plain question at a time, no jargon, no numbered menus.
- **Verify, do not trust - including your own checks.** Three times in one day a check reported
  success because it could not see the thing it was looking for: an assertion that could not match
  the defect, a "watch it fail" that failed for the wrong reason, and a grep whose filter excluded
  the only lines that could match. **Empty output is not evidence until you have shown the check can
  produce output.** Red-watch your grep the way you red-watch your test.
- **Prefer the design where the mistake is impossible over the one where it is merely avoided** -
  but only where the failure would be **silent** and the cost is the owner's data or trust. Where it
  fails loudly, a test is proportionate and the elaborate design is waste.

## What "done" looks like

The owner opens Settings, sees a tab called Injected text, reads exactly what we put into his
agents, and can either accept ours or write his own - with no doubt about which one is live. And
then he decides, freely, whether my no-attribution rule stays in it.
