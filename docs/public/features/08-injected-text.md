# Injected Text

**DevThrottle gives every agent it starts a short piece of text before your first
message. This page tells you exactly what that text is, why it is there, how it
reaches the agent, and how to change it or remove it.**

You can read the live text at any time in **Settings, Injected text**. Nothing on
this page is hidden from that tab, and the tab always wins: it shows what your
agents are actually getting on this machine right now.

## What we inject, and why

When DevThrottle starts a session, the agent has no idea it is part of a fleet. It
does not know which session it is, which machine it is on, or that there are other
sessions it can talk to. Every session already carries that information in its
environment, but an agent does not read environment variables unless something
points at them.

So we hand the agent a short preamble at startup that tells it:

- which session it is (its name, its identifier, the machine, the repository),
- who you are, if you are signed in, so that "email me" means you and is not
  guessed from the database,
- the `cc-devthrottle` commands it can use to reach the rest of your fleet,
- a warning not to broadcast to every session on every machine,
- our request that agents do not put their own name on your commits.

That last item is a policy rather than plumbing, which is precisely why this tab
exists. See "Our editorial policy" below.

## How it reaches the agent

**We do not type into your terminal, and we do not modify what you type.** The
text is handed over through each agent's own documented startup extension point:

| Agent | How the text is delivered |
|---|---|
| Claude Code | A `SessionStart` hook returns it as `additionalContext`. The hook file is passed with `--settings`, which MERGES with your own hooks and never replaces them. |
| Codex | A `SessionStart` hook entry merged into your `~/.codex/hooks.json`. |
| Pi | A file passed to `--append-system-prompt` at launch. |
| Gemini, Cursor, OpenCode, Grok, Copilot | Nothing is injected today. |

For Claude and Codex the hook fires at startup, resume, clear, and compact - the
four moments an agent's memory of the fleet would otherwise be empty - so the text
is re-supplied after `/clear` and after auto-compaction.

There is one more channel: when you are running our text, every session also gets
an environment variable called `CC_FLEET_TOOLS` listing the same `cc-devthrottle`
commands. Nothing in DevThrottle reads it - it is there purely for the agent to
find - so it is our words reaching your agent, exactly like the preamble, and it
reaches every agent including the ones with no preamble at all.

**It follows the same choice.** If you run your own text, we do not set it. Otherwise
you could delete the fleet commands from your version and we would quietly put them
back through a channel this page never showed you.

## Reading it, and replacing it

**Settings, Injected text** shows the live text. A coloured banner at the top says
whose text it is, and it is the one thing on the screen you cannot misread: if you
are running your own version, it says so.

- **Write my own version** starts you from a copy of ours, which you can edit
  freely.
- **Use the DevThrottle text** goes back to ours. Your version is kept, not
  deleted, and you can switch back to it.
- **Show the current DevThrottle text** shows the version we ship today, side by
  side, even while yours is live.

It is yours or ours - never a mixture. We do not merge your text with ours.

### When a change takes effect

**Sessions you start after saving get your text.** Sessions already running keep the
text they were given until they clear or compact, at which point Claude and Codex
are handed the current version.

One detail, because it is the kind of thing you should hear from us rather than
notice: the `CC_FLEET_TOOLS` variable described above is set once when a session's
process starts and cannot be changed afterwards. So a session that was already
running when you switched to your own text keeps that variable until you restart
it. It contains only the command list - never any of our policy text - but if you
want a clean break, restart the session.

### The trade

**If you write your own version, you stop receiving our updates to this text.**
That is the deal, and it is deliberate: we will not silently edit words you wrote.

Our updates still arrive with every DevThrottle update and are always written to
disk where you can read them, so you can see what changed and adopt it if you
want. You just have to choose to.

### Placeholders

Your text may use these, and DevThrottle fills them in for each session:

| Placeholder | Becomes |
|---|---|
| `[SESSION_ID]` | The session's full identifier |
| `[SESSION_SHORT_ID]` | The first eight characters, which the fleet commands take |
| `[SESSION_NAME]` | The session's name, or `(unnamed)` |
| `[MACHINE]` | The machine it runs on |
| `[REPO_PATH]` | The repository or working directory |
| `[USER_NAME]` | Your name, if you are signed in |
| `[USER_EMAIL]` | Your email, if you are signed in |

Anything between `[IF_SIGNED_IN]` and `[END_IF]` is used only when someone is
signed in, and dropped entirely when nobody is. Use it for any sentence that
mentions `[USER_NAME]` or `[USER_EMAIL]`, so it disappears cleanly rather than
rendering as an empty gap.

Bracketed words that are not in the table above are ordinary text and are sent
exactly as written - our own text starts with the literal `[CC Director fleet]`.
The one consequence: there is no way to write a placeholder that does NOT expand.
`[[SESSION_ID]]` renders as the identifier in square brackets.

### Removing things is allowed

You can delete any of it, including all of it. Two consequences worth knowing
before you find out later, both of which the tab warns you about while you edit:

- **Remove the `cc-devthrottle` commands** and your agents are no longer told how
  to reach each other. They will not message, list, or coordinate with the rest of
  your fleet.
- **Remove `[SESSION_ID]`** and an agent will not know which session it is.

Those are your choices to make. DevThrottle will not overrule them.

### If your text goes missing

If you are running your own version and DevThrottle cannot read it, **it injects
nothing and tells you.** It does not quietly fall back to our text. You turned our
text off; a file error is not consent to turn it back on.

## Our editorial policy

The shipped text asks agents not to put their name on your work - no
"Co-authored-by" trailer naming an assistant, no "Generated with" line. Several
agents are told by their own vendor to add those by default, and that default is
wrong for a repository that belongs to you.

We think it is a good rule. It is also **our opinion arriving inside your agent**,
which is a different kind of thing from telling an agent its session identifier,
and you did not ask for it.

So: it is in the tab, in plain sight, and you can delete it. If you want your
agent to sign its commits, that is a legitimate choice on your machine, and
DevThrottle should not be the reason you cannot make it.

## Where it lives on disk

Under your DevThrottle data directory, in `injected-text/`:

- `ours.txt` - the text we ship. Rewritten on launch, so it is always current.
  This file is a copy for you to read; editing it changes nothing, because the
  version we ship lives in the application. Edit your own version in the tab.
- `yours.txt` - your version, if you have written one.

Which one is live is recorded in `config.json` under `injected_text.use_yours`.
