# Mission: Pre-Release Must-Fix

**Status: FINISHED** (opened 2026-07-15, closed 2026-07-15 - all four defects landed on origin/main
the same day)
**Mission id:** `3bf418cd-444c-48fd-b113-46b5e055fd8d`
**Mission worktree:** was `D:\ReposFred\devthrottle-prerelease-check` (removed at close)
**Mission branch:** was `mission/pre-release-must-fix`, cut from origin/main at `967c07e3` (deleted
at close - every fix landed through its own pull request)

**How it ended:** all four fixes were built by the Manager, independently inspected by Codex, and
landed by the Architect. Item 1 merged as `322a66a0` (pull request 1680). Item 2 merged as
`ba72b017` (pull request 1681) - inspection round 1 rejected it and forced a per-source ingest gate,
which the Manager correctly keyed on SOURCE rather than session. Item 3 merged as `7a49dd06` (pull
request 1682) - inspection round 1 rejected it too, because the verb's own error branch carried the
same markup-crash defect; the wider same-pattern sweep is issue 1683. Item 4 merged as `4bde4b1f`
(pull request 1684), inspection-clean on round 1. The inspector found real, confirmed defects in two
of the four pull requests; nothing it flagged was a false alarm on re-verification.
**Conduct:** this brief describes the WORK only. How a mission is run - roles, authority, landing,
inspection, the report - lives in `.claude/skills/mission/SKILL.md` and is not restated here.

---

## THE WHY

The owner wants to release DevThrottle as soon as possible. A full pre-release review of the 93
commits merged over 2026-07-14 and 2026-07-15 (two independent reviews: a Claude agent fleet reading
the code, and Codex reading the bundled diffs) found the release is in good shape EXCEPT for four
confirmed defects that must not ship. Every other finding was classified next-release.

When this mission is finished: the four defects below are fixed on origin/main, each with a test
that was watched failing, and the release can be cut without shipping a known data-corruption path,
a known data-loss path, a crashing fleet verb, or a verb that silently does more than it was asked.

Each finding was verified against the real code at `967c07e3` - callers read, guards checked, and
for items 3 and 4 the failure was reproduced. These are not diff-reading guesses.

---

## The work, in landing order (one pull request per item)

### 1. Prompt-log dedupe watermark is built on a per-process hash - every Director restart re-pushes entire conversation histories

- **Where:** `src/CcDirector.Core/Storage/ConversationIngestor.cs:317`
  (`IngestState.Key` uses `text.GetHashCode()`).
- **What is wrong:** the watermark ("which messages have I already pushed to the Gateway") is
  persisted to `prompt-ingest-state.json` precisely so a restart cannot re-push a whole history -
  the class comment says exactly that. But .NET randomizes `string.GetHashCode()` on every process
  start, so after any restart every recomputed key differs from every persisted key,
  `AlreadyWritten` answers false for everything, and the first turn end after startup re-pushes the
  ENTIRE conversation history. The Gateway prompt log appends blindly (no dedupe on receipt,
  retention deliberately unbounded), so ten restarts means ten copies of everything. For the agents
  whose ingest scope is per-repository (Gemini, Copilot, OpenCode), merely opening a new session on
  a repository re-pushes months of that repository's history.
- **Why the existing tests pass:** all dedupe tests run inside one process, where the hash seed is
  constant. The exact case the persisted file exists for - a process restart - is untested.
- **The fix:** replace `text.GetHashCode()` with a stable content hash (SHA-256 of the text,
  truncated hex is fine). Keep the key shape otherwise.
- **Accepted consequence (ruled, do not re-litigate):** existing watermark files hold old-format
  keys, so the FIRST run of the fixed build re-pushes once more - identical to what every restart
  does today, and never again after that. Do not build a migration for the old keys.
- **The test that must exist:** a test that persists keys with one hash function and reads them
  back where `string.GetHashCode()` would give a different answer - i.e. a test that fails against
  `GetHashCode()` by construction (simulate the restart; two live processes is the honest version
  if the harness allows it).

### 2. A failed prompt-log write is acknowledged as success - permanent silent history loss

- **Where:** `src/CcDirector.ControlApi/GatewayPromptSink.cs:46-52`.
- **What is wrong:** `GatewayPromptLog.Append` on the Gateway swallows write failures and returns a
  partial count, and `POST /prompts` returns 200 either way - that half is truthful (the count is
  real). The defect is the Director half: the sink logs `Gateway wrote {written} of {count}` and
  then **returns true anyway**, so `ConversationIngestor` marks every record as pushed. The
  Director keeps no copy by design, so records the Gateway never stored are permanently absent from
  the single copy. The sink's own contract comment ("false ... means not recorded and the caller
  must not mark them done") promises the opposite of what the code does.
- **The fix:** `return written == records.Count;`. The false path already retries at the next turn
  end. Note: within one process the keys are stable, so a re-push after a partial write only
  re-sends what was reported unwritten plus what dedupe cannot distinguish - acceptable; duplicates
  on failure recovery are better than silent loss (and item 1 makes keys stable across restarts).
- **The test that must exist:** a sink test whose fake Gateway reports fewer written than sent, and
  asserts `PushAsync` returns false. Revert the fix, watch it go red.

### 3. `cc-devthrottle session buffer` crashes on bracketed text and rewraps every line at 80 columns when piped - reproduced

- **Where:** `tools/cc-devthrottle/src/session_ops.py:290-306` (`read_session_buffer`, the final
  `console.print(text)`).
- **What is wrong:** the buffer text is raw terminal content from another session, printed through
  Rich with markup and wrapping enabled. Two reproduced failures: (a) any token shaped like a
  closing tag - `[/tmp/x]`, `[/INST]` - raises `rich.errors.MarkupError` as an uncaught traceback;
  (b) when stdout is not a terminal (exactly how a Manager agent calls it), Rich rewraps every line
  at 80 columns and silently eats style-shaped tokens like `[bold]`. The same file already fixed
  this hazard for the JSON output of `list_sessions`, with a comment explaining why.
- **The fix:** print the buffer with plain `print()` (the `list_sessions` precedent). Leave `--json`
  output untouched.
- **The test that must exist:** the tool's test suite feeds buffer text containing `[/tmp/x]` and a
  200-character line through the verb with stdout captured as a pipe, and asserts the text comes
  back byte-identical. Both assertions must fail against `console.print`.

### 4. `session prompt --no-submit` silently submits when the target session lives on another machine

- **Where:** `src/CcDirector.ControlApi/ControlEndpoints.cs:705` (relay call drops the flag) and
  `src/CcDirector.ControlApi/GatewayClient.cs:248` (`SendPromptToFleetAsync` hardcodes
  `AppendEnter = true`).
- **What is wrong:** the local-target path honors `AppendEnter`; the Gateway relay path for a
  remote target does not carry it at all and hardcodes true. An agent staging text in a remote
  session's composer for review actually SUBMITS it, and the tool reports success.
  `FleetPromptRequest.AppendEnter`'s own doc comment claims it "is passed straight through" - it is
  not, on the relay path.
- **The fix:** add the append-enter parameter to `GatewayClient.SendPromptToFleetAsync` and pass
  `req.AppendEnter` through from `ControlEndpoints.cs:705`.
- **The test that must exist:** a relay-path test asserting the outgoing `PromptRequest` carries
  `AppendEnter = false` when the fleet request said so. Must fail against the hardcoded true.
- **Known adjacent defect, explicitly NOT in scope:** the same two paths disagree on `AgentDriven`
  (local path counts the prompt as human, relay marks it agent-driven), skewing statistics only.
  That is a next-release issue, not this mission. Do not fix it in the same pull request.

---

## Design rulings already made

1. Stable hash means a content hash (SHA-256, truncated is fine) - not `ToHashCode`, not FNV rolled
   by hand, nothing seeded per process. (Stated by the Architect.)
2. No migration for old watermark files; one extra re-push on first run of the fixed build is
   accepted. (Stated by the Architect.)
3. The Gateway `POST /prompts` endpoint stays as it is - the returned count is truthful and the
   Director-side comparison is the fix. (Stated by the Architect.)
4. `session buffer` keeps its output contract: what the terminal held is what the caller gets,
   byte-identical, wrapped by nobody. (Inferred from the verb's purpose and the `list_sessions`
   precedent in the same file.)
5. Four pull requests, one per item, in the order above - items 1 and 2 touch the same subsystem
   but are separate defects with separate proofs. (Stated by the Architect.)

## Out of scope - do not do these

- The command-timeout wording in `DirectorCommandRouter.DescribeTimeout` ("The command was not
  carried out" overclaims). Filed as a next-release issue by the Architect.
- Every finding classified "should fix next release" in the pre-release review (stats map leak,
  role-stamp ordering race, snooze restart durability, snoozing a crashed session, Gateway-less
  ingest cost, snooze label rounding, Voice tab dropping snoozed-ready sessions, `session hold`
  pending text, overlay z-order, fleet map refresh guard, dead lint gate, `AgentDriven` asymmetry,
  `/repos` redirect tab). The Architect files these as issues; the Manager does not touch them.
- The pre-existing dictation-loss cluster (#1590, #1593, #1595).
- Anything in the release process itself (tagging, notes, deployment).

## Inspection note (mission-specific fact, not conduct)

The Inspector for this mission is Codex. On this machine `codex exec` cannot run any shell command
(the Windows sandbox helper is missing), so the Architect bundles each pull request's diff plus the
touched files inline into the prompt and pipes it in. The Architect calls each inspection after the
Manager reports a pull request ready; findings go back to the Manager.

---

*When this mission ends, this document's status line must be updated to say so, in the past tense.*
