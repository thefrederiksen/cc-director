# What a skill must be, to work in every agent we support

Research, 28 July 2026. Question put to this session: expand the Gateway skill library so it can hold
everything a real skill needs - markdown, Python, command-line programs, archives, subdirectories -
and so that both an agent and a person at the Cockpit can upload them.

---

## The headline

**The question of what shape a skill takes has already been settled outside this repository, and every
agent DevThrottle supports has adopted the same answer.** A skill is a DIRECTORY, not a file and not a
flat bag of files: a required `SKILL.md` at its root, plus any files and subdirectories it wants.

Anthropic published the Agent Skills specification as an open standard at `agentskills.io` on
18 December 2025 and handed stewardship to the Linux Foundation's Agentic AI Foundation. Within
forty-eight hours OpenAI and Microsoft had shipped support. By mid-2026 roughly forty products
implement it.

So this is not a design choice we get to make. If our library stores anything other than a standard
skill directory, then every skill in it needs converting before any agent can use it, and every skill
in the world outside needs converting before it can come in. Matching the standard is the whole game.

---

## The standard

Source: <https://agentskills.io/specification>

### Layout

```
skill-name/
  SKILL.md          required: frontmatter plus instructions
  scripts/          optional: executable code
  references/       optional: documentation loaded on demand
  assets/           optional: templates, images, data files
  ...               any additional files or directories
```

The specification's words are "any additional files or directories". There is no allowed-extension
list, no file-count limit, and no prohibition on binaries anywhere in the specification or in any
agent's documentation.

### Frontmatter

| Field | Required | Constraint |
|---|---|---|
| `name` | yes | 1 to 64 characters, lowercase letters, digits and hyphens; no leading, trailing or doubled hyphen; must match the directory name |
| `description` | yes | 1 to 1024 characters; must say both what the skill does and when to use it |
| `license` | no | a license name, or the name of a bundled license file |
| `compatibility` | no | up to 500 characters; environment requirements |
| `metadata` | no | arbitrary string-to-string map for client-specific properties |
| `allowed-tools` | no | space-separated list of pre-approved tools (marked experimental) |

Anything outside that key set fails validation. The frontmatter must begin at the first byte of the
file.

### Progressive disclosure

The standard bakes in exactly the discovery-is-cheap principle our library already follows:

1. `name` and `description` load at startup for every skill (about 100 tokens each).
2. The `SKILL.md` body loads when the skill activates (under 5000 tokens recommended, under 500 lines).
3. Everything else loads only when the task reaches for it.

Codex makes this a hard budget: the initial list of skills is capped at 8000 characters or two percent
of the context window, whichever is smaller. That is external confirmation of the summary and trigger
caps already enforced in `SkillValidation` - a long summary really is a tax paid by every session.

---

## What each agent we support actually does

Every row below was read from the agent's own current documentation, cited. Sources are listed at the
end.

| Agent | Reads the standard | Its own path | Also reads the shared path | Invocation |
|---|---|---|---|---|
| Claude Code | yes, explicitly | `~/.claude/skills/<name>/`, `.claude/skills/<name>/` | NOT today - `~/.agents/skills` is an open feature request (anthropics/claude-code issue 66352) | `/<name>`, or the model loads it when relevant |
| Codex | yes | `.codex/skills/`, `~/.codex/skills/` | yes - `.agents/skills`, `~/.agents/skills`, `/etc/codex/skills` | `$name`, `/skills`, or implicit from the description |
| Gemini | yes | `.gemini/skills/`, `~/.gemini/skills/` | yes - `.agents/skills`, `~/.agents/skills` | `/skills`, `gemini skills`, or implicit; asks for confirmation and then grants read access to the skill's directory |
| Grok | yes | `.grok/skills/`, `~/.grok/skills/` | yes - `.agents/skills`; also reads `.claude/skills` and Claude marketplaces wholesale | `/skills` in its interface |
| pi | yes | `~/.pi/agent/skills/`, `.pi/skills/` | yes - `~/.agents/skills`, `.agents/skills`; and `settings.json` can point at any directory, including `~/.claude/skills` | registers each skill as `/skill:<name>` |
| Copilot | yes | `~/.copilot/skills/`, `.github/skills/` | yes - `.agents/skills`, `~/.agents/skills`; also reads `.claude/skills` | implicit, from the description |
| Cursor | yes | `.cursor/skills/`, `~/.cursor/skills/` | not confirmed in its own documentation | skills and subagents, since Cursor 2.4 |
| opencode | yes | `.opencode/skills/`, `~/.config/opencode/skills/` | yes - `.agents/skills`, `~/.agents/skills`; also `.claude/skills`, `~/.claude/skills` | implicit |

Two facts fall out of that table and they matter more than anything else in this document.

**First: one artifact serves all eight.** There is no per-agent skill format to translate into. A
single standard directory is literally the same bytes for every agent. Whatever per-agent work exists
is about WHERE the directory is put on disk, never about what is in it.

**Second: `.agents/skills` is the shared path for seven of the eight, and Claude Code is the
exception.** Claude Code does not scan `~/.agents/skills` today; the request to do so is open and
unshipped. So any placement scheme needs the shared path plus `~/.claude/skills` for Claude Code - two
locations, not eight. Claude Code does follow directory symlinks and loads a target reached from more
than one location only once, so one materialized copy with links pointing at it is a clean fit.

---

## What our store cannot represent today

Measured against `SkillValidation` on `origin/main`:

| Rule today | The standard | Consequence |
|---|---|---|
| File names must match `^[A-Za-z0-9._-]+$` - a path separator is refused outright | `scripts/`, `references/`, `assets/` and any other directory | Cannot store a conforming skill at all. Our own `agent-expert` (nine files under `agents/`) and `playwright-cli` (seven under `references/`) cannot go in |
| Extensions limited to `.py .md .txt .json` | any file | No shell scripts, no JavaScript, no TOML or YAML config, no images, no archives, no compiled programs |
| At most 20 files per version | no limit | `agent-expert` is at 11 today and the cap is a wall we will hit, not a safety margin |
| File content is a text string in the payload | binaries are ordinary files | An image, a zip, or a compiled program cannot survive the round trip |
| No frontmatter fields stored | six defined fields | `license`, `compatibility`, `metadata`, `allowed-tools` are all lost; we synthesize a `SKILL.md` from name and summary instead of holding the author's own |
| No file mode stored | scripts are executable | On Linux and macOS a materialized script needs the executable bit or it will not run |
| Body limit 200 kilobytes, file limit 256 kilobytes | none stated | Generous enough; not the problem |

Measured evidence from real skills, this machine, today:

- This repository's `.claude/skills`: 47 files, 46 markdown and 1 Python, largest 41.4 kilobytes,
  maximum one directory level below the skill root, largest skill 11 files.
- The user-level `~/.claude/skills`: 31 files, 28 markdown, 2 Python, 1 compiled Python cache file,
  largest 27.3 kilobytes, largest skill 5 files.

So today's real skills are markdown-heavy and shallow. The limits that bite are the path separator and
the extension list, not size. But the moment skills carry a working command-line program - which is
what was asked for - the extension list, the binary gap and the executable bit all bite at once.

---

## What I recommend we build

**Hold a standard skill directory, verbatim, and stop inventing a shape.**

1. **A file is a relative path, not a bare name.** Store `references/tracing.md`, not `tracing.md`.
   Validate as a path: forward slashes only, no leading slash, no `.` or `..` segment, no drive letter,
   no backslash, no trailing slash, no reserved Windows device name (`nul`, `con`, `aux`, `prn` and
   friends - this repository has been bitten by stray `nul` files before), each segment matching the
   existing safe-character rule, and a depth cap of 5 segments. This is the same defence the current
   bare-name rule provides, applied per segment instead of by banning the separator.

2. **Drop the extension allow-list; keep a small deny-list.** The allow-list cannot be completed - the
   standard permits any file - and every entry we fail to guess is a skill that cannot be stored. Deny
   the handful that are actively dangerous to write onto a Windows machine unasked, and let everything
   else through.

3. **Carry `SKILL.md` as the body, and hold the real frontmatter.** Store `license`, `compatibility`,
   `metadata` and `allowed-tools` as first-class fields so a skill round-trips without loss, and so a
   skill authored elsewhere can be pushed into our library and pulled back out identical. Keep our
   `summary` and `triggers` as what they already are - the register line - and map `summary` to the
   standard's `description` when writing `SKILL.md` out.

4. **Support binary files.** Add a content encoding to the file record - `utf8` or `base64` - and store
   bytes rather than a string. This is what makes images, archives and compiled programs possible. A
   zip is then simply a binary file; nothing special is needed for it.

5. **Store a file mode.** One boolean, executable or not, applied when the file is written on Linux and
   macOS. On Windows it is ignored.

6. **Raise the limits, and pick them from evidence.** Suggested: 200 files per version (ten times the
   largest real skill), 5 megabytes per file, 25 megabytes per skill version. Keep the summary and
   trigger caps exactly as they are - Codex's own 8000-character listing budget says they are right.

7. **Upload from both directions, same shape.** The command line already round-trips a directory
   (`skill pull` and `skill push`); it needs to walk subdirectories and send binaries as base64. The
   Cockpit needs the same operations: create a file at a path, edit a text file in the browser, upload
   a binary or a whole folder by drag-and-drop, delete, and download the skill as a zip. Both write the
   identical payload, so neither can produce a skill the other cannot read.

None of this changes the discovery model: the register still carries one line per skill, and the body
and files are still fetched only when a skill is used.

---

## The one thing this research cannot decide

Once a skill is a standard directory, we can put it in each agent's own skills directory - and every
agent then discovers it natively, listing it under its own `/skills` command, with no DevThrottle
command needed. That is a genuinely better experience than "run our command to fetch the body".

But it reverses a standing ruling. The rule today is that NOTHING is deployed to a machine, not even a
small bootstrap file, and the reason given was that such a file would only ever be read by Claude Code
while reaching every agent family is the point. **That reason no longer holds** - all eight families
read the same directory format now, and seven share one path. The ruling may still hold for other
reasons: files written onto a machine go stale, and a stale skill that looks current is precisely the
failure the central library exists to prevent.

So this needs a decision, and it is not mine to make. It is written up as the open question at the end.

---

## Security: this is the part that gets more dangerous

Today a skill in our library is instructions. The moment it carries executable code, the library
becomes a channel that puts runnable programs on every machine in the fleet, fetched from a server.

Published research is unambiguous that skills are an effective prompt-injection vector: malicious
instructions hidden in a skill file or its referenced scripts can exfiltrate internal files and
passwords, and a benign approval can be made to carry over to a related harmful action
(arXiv 2510.26328). There is a further paper specifically on supply-chain security for agent skills
(arXiv 2603.00195).

What we already have that helps: every version is content-hashed, the hash is checked when the local
cache is verified, versions are immutable, tenants are isolated, and the built-in skills are
read-only. What is worth adding before executable content ships:

- Verify the content hash over the WHOLE tree, including binary files, before anything is written to
  disk - and refuse rather than repair on mismatch.
- Show a person, in the Cockpit, exactly which files a skill carries and which are executable, before
  they publish it. An executable file arriving in a skill should be visible, not incidental.
- Decide deliberately whether a tenant's own skills may carry executables at all, or whether that
  needs switching on. This is a policy question, not a code question, and it should be answered before
  the feature exists rather than after.

I am not proposing we solve prompt injection. I am proposing we do not quietly widen the blast radius
while thinking we are only adding file types.

---

## What I did not verify

- **Everything above is from documentation, not from binaries on this machine.** The agent-expert
  reference in this repository warns explicitly that these systems churn and that "by analogy to
  Claude" claims have already proved wrong once - Grok was mis-recorded as a hook injector on exactly
  that reasoning. Before code depends on a discovery path, that path should be checked against the
  installed binary.
- Cursor's support for `.agents/skills` is not confirmed in Cursor's own documentation; I found it only
  in third-party summaries.
- I did not test whether any agent rejects a skill directory that contains a binary or an archive. The
  specification permits it and no agent forbids it, but permitted-in-writing is not the same as
  observed-working.
- I did not measure how large a skill can get before an agent's own handling degrades.
- The proposed limits (200 files, 5 megabytes, 25 megabytes) are reasoned from measured real skills,
  not from any published limit. No agent publishes one.

---

## Sources

- Agent Skills specification: <https://agentskills.io/specification>
- Claude Code skills: <https://code.claude.com/docs/en/skills>
- Claude Code `.agents/skills` feature request: <https://github.com/anthropics/claude-code/issues/66352>
- Codex skills: <https://developers.openai.com/codex/skills>
- Gemini skills: <https://geminicli.com/docs/cli/skills/> and
  <https://github.com/google-gemini/gemini-cli/blob/main/docs/cli/skills.md>
- Grok skills: <https://docs.x.ai/build/features/skills-plugins-marketplaces>
- pi skills: <https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/docs/skills.md>
- Copilot skills: <https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills>
- Cursor skills: <https://cursor.com/docs/skills>
- opencode skills: <https://opencode.ai/docs/skills/>
- Prompt injection through skills: <https://arxiv.org/abs/2510.26328>
- Supply-chain security for agent skills: <https://arxiv.org/pdf/2603.00195>
- Ecosystem and adoption count: <https://agentman.ai/blog/agent-skills-ecosystem-report-2026>
