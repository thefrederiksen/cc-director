# Skills

**A skill is a folder of instructions an agent can pick up and follow. DevThrottle
holds your skills centrally, downloads them to each machine while it runs, and
writes them where every agent already looks - so a skill is fixed once and is
live everywhere, on every agent, with nothing for you to install or update.**

You manage them in the Cockpit under **Skills**.

## What a skill is

A skill is a directory with a `SKILL.md` file at its root, plus any files it
needs - reference notes, scripts, images, data files, whole subdirectories. That
shape is not ours. It is the Agent Skills open standard, and every agent
DevThrottle runs reads exactly the same directory, byte for byte.

This is the single most useful fact about skills: **there is no per-agent
format**. One skill works in Claude Code, Codex, Gemini, Grok, pi, Copilot,
Cursor and opencode without being converted, adapted or duplicated. The only
thing that differs between agents is which folder the directory has to appear in,
and DevThrottle handles that for you.

A skill may carry real programs, not just prose. Files can be binary, and a file
can be marked executable so a script in a skill actually runs. That is the point
of downloading them at all: a skill that carries Python or a command-line program
cannot be run from a website.

## How your agents get them

Two separate steps, deliberately kept apart so that nothing to do with the
network can ever slow down or block a session starting.

**Step one - DevThrottle downloads them.** As soon as DevThrottle connects to the
Gateway, and about once a minute for as long as it is running, it fetches every
skill you have switched on and stores it on the machine. This happens quietly in
the background, nowhere near your sessions.

**Step two - DevThrottle puts them where the agent looks.** When a session
starts, the stored skills are written into the folders that session's agent
reads. This step never touches the network, so it cannot delay a launch. If the
Gateway is unreachable, the session still starts, using whatever the last
download brought down.

The practical result: **a session opens with skills that are at most about a
minute old.**

## Where they land on your disk

Every skill is written **once**, into `~/.agents/skills` - the shared folder that
the agent ecosystem standardised on.

| Agent | Reads `~/.agents/skills` | What DevThrottle does |
|---|---|---|
| Codex | Yes | Nothing - it finds the folder on its own |
| Gemini | Yes | Nothing |
| Grok | Yes | Nothing |
| pi | Yes | Nothing |
| Copilot | Yes | Nothing |
| opencode | Yes | Nothing |
| Claude Code | No | One shortcut per skill into `~/.claude/skills` |
| Cursor | Not documented | One shortcut per skill into `~/.cursor/skills` |

Six of the eight need no configuration whatsoever. Claude Code does not read the
shared folder - the request for it to do so is open and unshipped - and Cursor
does not document doing so, so each skill also gets a shortcut in their own
folder pointing at the same single copy.

The shortcut is a directory junction on Windows and an ordinary symbolic link on
Linux and macOS. Neither needs administrator rights. It matters that these are
shortcuts and not second copies: **there is only ever one real copy of a skill on
your machine, so there is nothing that can quietly drift out of step.**

Skills are written under your home folder, never into the repository you are
working in, so they never appear as untracked files in your working trees. The
consequence is worth knowing: a downloaded skill is visible to every session on
that machine, including sessions DevThrottle did not start.

## Your own skills are never touched

The central library is an **additional** source of skills, not a replacement.

- A skill of your own in `~/.claude/skills` or in a repository's `.claude/skills`
  keeps working exactly as it did.
- If one of your skills has the same name as a central one, **yours wins.**
  DevThrottle leaves it alone and records that it did.
- DevThrottle marks everything it writes and only ever changes or removes its own
  entries. It never replaces your skills folder itself.

## Switching one off actually removes it

DevThrottle matches what is on the machine to what the Gateway is currently
serving, rather than adding to what is already there. So a skill you switch off,
archive or delete in the Cockpit is **removed from disk** the next time a session
starts on that machine.

This is the reason skills are held centrally in the first place. A withdrawn
instruction that keeps working from a leftover file on one machine is exactly the
failure a central library exists to prevent.

## What the agent is told at startup

Your agents do not receive the full text of every skill - that would be a large
tax on every session for capabilities most sessions never use. They receive one
line per skill: its name and a one-line summary. The agent reads a skill's actual
instructions only when it decides the skill is relevant to what you have asked.

This is the progressive disclosure the standard specifies, and it is why a
skill's summary is worth writing carefully: it is the only part every session
pays for.

## Writing and changing skills

Two ways in, both producing the same thing:

- **In the Cockpit**, under Skills: create a skill, add files and folders, edit
  text in the browser, upload a binary, and publish.
- **From the command line**, with `cc-devthrottle skill pull`, `push` and
  `publish`. An agent can author a skill and publish it to your library itself.

Drafts are private to you. Publishing makes a skill live across your whole fleet
immediately - no release, and nothing to update on any machine.

Built-in skills are read-only. To change one, clone it and edit your copy.

## If a skill does not appear

- **Is DevThrottle running and connected to the Gateway?** The download happens
  in DevThrottle, so a machine where it is not running gets nothing new.
- **Is the skill switched on?** Only enabled skills are downloaded.
- **Did the session start before the skill was published?** Skills are placed at
  session start. Start a new session and it will have it.
- **Do you already have a skill of that name?** Yours wins, deliberately. Rename
  one of them if you want both.
