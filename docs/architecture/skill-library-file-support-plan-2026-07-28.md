# Development plan: hold a whole skill, and put it where each agent looks

28 July 2026. Companion to the research at
`skill-file-model-research-2026-07-28.html`, which establishes the one fact this plan rests on:
every agent DevThrottle supports reads the SAME artifact - a directory with `SKILL.md` at its root
plus any files and subdirectories - so there is one shape to store and no per-agent format to
translate into.

## What we are building, in one paragraph

The Gateway library will hold a complete, standard skill directory - markdown, Python, shell scripts,
command-line programs, images, archives, anything, at any depth - instead of the flat list of four
file extensions it holds today. Agents will write skills into it from the command line and people will
write them in the Cockpit, both producing the identical thing. And when the Director launches a
session, it will put that tenant's skills where THAT agent looks for them, so Claude Code, Codex,
Gemini, Grok, pi, Copilot, Cursor and opencode each discover them natively through their own skills
machinery, with no DevThrottle command in the way.

## Assumption being made, stated plainly

Skills install at the USER level - `~/.claude/skills/` for Claude Code and `~/.agents/skills/` for
the seven agents that share that path - not into the repository being worked on, because writing into
the repository puts untracked files in the owner's working trees. The consequence is that an installed
skill is visible to every session on the machine, including sessions the Director did not launch. This
was recommended and not explicitly confirmed; it is cheap to change (one path table) if the answer is
meant to be the other way.

## Four phases, each merged to main on its own

Each phase is independently useful and independently provable. Nothing here is a big-bang cutover.

---

## Phase 1 - The Gateway can hold a standard skill directory

The store is the constraint everything else waits on, so it goes first.

**File identity becomes a relative path.** `SkillFileEntity.FileName` starts carrying
`references/tracing.md` rather than a bare name. Validation moves from "reject any separator" to
validating a path properly: forward slashes only, no leading or trailing slash, no empty segment, no
`.` or `..` segment, no backslash, no drive letter, no reserved Windows device name in any segment
(`nul`, `con`, `aux`, `prn`, `com1` and friends - this repository has been bitten by stray `nul` files
before), each segment matching the existing safe-character rule, at most five segments deep. This is
the identical defence the bare-name rule gave us, applied per segment instead of by forbidding the
separator.

**The extension allow-list becomes a small deny-list.** An allow-list cannot be completed when the
specification permits any file, and every extension we fail to guess is a skill that cannot be stored.
We deny only what is actively dangerous to write onto a Windows machine unasked, and let the rest
through.

**Files can be binary.** `SkillFileDto` gains `Encoding` - `utf8` or `base64` - and the size cap
applies to the DECODED bytes. Content stays in a text column with an explicit encoding discriminator
rather than becoming a binary column, because that is an additive migration on both database
providers instead of a type change to existing rows. A zip needs nothing special once this exists; it
is simply a binary file.

**Files can be executable.** One boolean per file, applied when the file is written on Linux and
macOS, ignored on Windows. A script without it does not run.

**The standard's frontmatter is stored, not invented.** `license`, `compatibility`, `metadata` and
`allowed-tools` become fields on the version row, so a skill authored anywhere else survives a round
trip through our library unchanged, and so `SKILL.md` can be written back out exactly as its author
wrote it. Our `summary` stays what it is - the register line - and maps to the standard's
`description`.

**Limits raised from evidence.** 200 files per version (ten times the largest real skill measured),
5 megabytes per file, 25 megabytes per version. The summary and trigger caps do NOT move: Codex caps
its own initial skills listing at 8000 characters, which is independent confirmation that those caps
are correct.

**The bundle hash covers all of it** - path, decoded content, encoding, executable bit and the new
frontmatter fields - so a change to any of them mints a new version and a client holding a stale copy
can tell.

**A migration pair, in this same change**, for the Sqlite and Postgres providers both.

Proved by: unit tests over every new validation rule in both directions (a path that must be accepted
and a path that must be refused - a guard has two failure directions); a round trip of a skill
carrying a subdirectory, a binary file and an executable script, asserting the bytes come back
identical; and a test that the built-in seeder still recognizes its own shipped content after the hash
format changes.

---

## Phase 2 - The command line round-trips a real skill directory

`skill pull` writes a directory that IS a standard skill: `SKILL.md` at the root, every file at its
real relative path, `skill.json` alongside for the register metadata that is ours and not the
standard's. `skill push` walks the tree, sends text as text and everything else base64-encoded, and
records the executable bit. `skill get` materializes the whole tree into the version-keyed machine
cache at real paths rather than flattening it into a `files/` folder.

This is what makes "an agent can author a skill and post it to the library" true, which was half the
original ask.

Proved by: pull, edit, push, pull again, and assert the second pull is byte-identical to the first,
with a binary file and a subdirectory present in the fixture.

---

## Phase 3 - The Director installs skills where each agent looks

At session launch, for the agent kind being launched:

1. Fetch the tenant's enabled skills from the Gateway.
2. Materialize each into one DevThrottle-owned directory, keyed by skill and version, verified against
   the bundle hash before anything is written.
3. Make it visible at the agent's own path - `~/.claude/skills/<id>` for Claude Code, `~/.agents/skills/<id>`
   for the seven that share that path - by directory link where the platform allows it (a junction on
   Windows needs no administrator rights; Claude Code's documentation states it follows a directory
   symlink and loads a target reached from several places only once), falling back to a copy where it
   does not.
4. **Leave alone any name already present that we did not write.** The standing rule is that the
   library is an additional source and a machine's own skills win a name clash; installing over one
   would break that silently. Ours are marked, so ours are the only ones we ever touch or remove.
5. **Reconcile, do not add.** A skill switched off or deleted on the Gateway disappears from disk on
   the next launch. An additive copy would leave withdrawn instructions lying about, which is the
   exact failure the central library exists to prevent.
6. **If the Gateway cannot be reached, the session still launches** - with nothing installed, saying
   so plainly in the log, and with our previously installed copies removed rather than left looking
   current.

Proved by: launching a real session per agent kind on this machine for the agents actually installed
here, and reading the agent's own skills listing to see the skill - not by asserting a file exists on
disk. Whether the other agents follow directory links the way Claude Code documents is unverified and
gets checked against the installed binary before we depend on it.

---

## Phase 4 - The Cockpit can author the files

The Skills page grows a file tree for a skill the tenant owns: create a file at a path, edit a text
file in the browser, upload a binary or drag in a whole folder, delete, and download the skill as a
zip. Built-ins show none of it, because the Gateway already sends `editable: false` and the client
renders that verdict verbatim rather than deriving it.

Proved by: driving the real Cockpit in a browser against a real Gateway - create a skill, add a
subdirectory and a binary, publish, then pull the same skill from the command line and assert it
matches.

---

## As built: where the work departed from this plan

Three decisions were made while building that this plan did not anticipate. They are recorded here
rather than left to be rediscovered from the code.

**The authoring directory keeps our metadata in `skill.json`, and `SKILL.md` holds the body alone.**
The plan implied a pulled directory would be a standard skill, frontmatter and all. It is not, and
deliberately: a pull followed by a push has to be byte-exact, and composing frontmatter on the way out
then parsing it on the way back in is a lossy round trip through a YAML dialect. So the authoring
layout keeps the two separable, and the STANDARD SKILL.md - frontmatter and body together - is composed
at the moment a skill is written for an agent to read. Authoring fidelity and standard conformance are
two different jobs and now have two different files doing them.

**The frontmatter `name` is the skill's id, not its display name.** The standard requires a lowercase
slug matching the directory name, so "Move a session" would fail validation in every agent. The
display name stays ours.

**Installing copies rather than links - since superseded.** The plan proposed directory links with a
copy where the platform would not allow one. Copies were built first, on the grounds that a symlink
needs elevation on Windows. That is true and it is incomplete: a directory JUNCTION needs neither
administrator rights nor Developer Mode. So the placement was settled on 29 July 2026 as one
materialized copy in the shared `~/.agents/skills`, with a per-skill junction (Windows) or symlink
(Linux, macOS) for the two agent families that do not read that path. Three copies is three things
that can drift. See `skill-placement-2026-07-29.html` for the decision, the per-agent path table and
the proof that Claude Code discovers a skill through a junction.

## What this deliberately does not do

- **It does not solve prompt injection.** Skills are a documented injection vector, and shipping
  executable content widens what a bad skill can do. What this plan adds is visibility - the Cockpit
  shows which files a skill carries and which are executable before anyone publishes it - and whole-tree
  hash verification before anything is written to disk. Whether a tenant's skills may carry executables
  at all is a policy question for the owner, and it should be answered before the feature exists rather
  than after.
- **It does not change the discovery economics.** The register still carries one line per skill.
  Installed skills cost disk and one fetch at launch, not context: every agent loads only name and
  description at startup from an on-disk skill, which is exactly the progressive disclosure the
  standard specifies.
- **It does not touch the built-ins' read-only rule.** Customizing one is still cloning it.
