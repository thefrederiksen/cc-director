# Mission: a turn is pushed once, stored once, and read by everyone

**Status: ACTIVE.** Started 1 September 2026. Worktree `D:\ReposFred\devthrottle-turn-push`, branch
`mission/turn-push`, cut from `origin/main` at `b71baccbf`. Conduct: `.claude/skills/mission/SKILL.md`.
Tracking issue: see the link at the end of this file once filed.

---

## The why

The owner, on 1 September 2026, looking at a phone that read "Voice did not arrive after 19m" for a
session whose Chat tab was working fine:

> "Why wouldn't it use the same as chat? And why would it ask for the transcript every time? That path
> should not be allowed to be used. We should send the turns and then use that for both in the chat
> window and in the transcript window. And that's what the wingman should use to be fed. We need a
> major overhaul. Stop making temporary fixes. Come up with the right solution."

What is true today, measured on that session (111, `d185c3a2`):

- **The Gateway does not hold the conversation.** Every time it needs the agent's words it sends a
  command down the tunnel to the owning Director, which opens the transcript file on the owner's disk,
  parses it, and sends the result back up.
- **The Chat tab polls that round trip every 2.5 seconds** (`useSessionChat`, `GET /sessions/{sid}/history`
  over the tunnel `history` verb). Each poll re-parses the whole transcript and ships the entire history
  back. That is the delay the owner feels on the phone, and it stops dead when the machine is offline.
- **The voice path uses a DIFFERENT reader for the SAME file.** Chat's reader follows the transcript
  pointer the Claude Code hook reports; the voice path's `turns` verb rebuilt the path from the repository
  folder. When the agent entered a Claude Code worktree at 15:13 UTC, Claude Code moved the transcript to
  a new project folder; Chat followed it, voice did not, and the hosted Gateway retried the read every
  45 seconds for hours, each time getting `no_jsonl` back. Pull request 2637 patched the formula. It is
  a patch on the wrong reader and this mission removes the reader.

When this mission is finished:

- The Director is the only thing that ever reads a transcript file, through one resolver, and it
  **pushes** each completed turn up the stream it already has.
- The Gateway **stores** the conversation per account and session, and Chat, the transcript view, and
  the wingman all read those rows. No tunnel read for text, ever.
- Chat opens in milliseconds and works while the owner's machine is offline.
- A voice retry can only ever mean "the model or the speech service did not answer". The retry
  schedule and Generate button built on 1 September become exactly that retry.

---

## Design rulings

Stated by the owner unless marked *inferred*.

1. **Push, do not pull.** The Director sends the conversation to the Gateway; the Gateway never asks for
   it. Stated.
2. **One store, three readers.** Chat (phone and Cockpit), the transcript view, and the wingman read the
   same stored rows. Stated.
3. **The store lives in the Gateway database**, one table, tenant-scoped like every other row, in both
   deployment modes (Postgres hosted, SQLite self-host), with an EF migration in the same pull request as
   the model change. *Inferred* from the existing `SessionHistory` and `DictationTranscripts` pattern.
4. **Retention 90 days**, pruned by the existing `SessionHistorySweep`, matching session history and
   dictation transcripts. *Inferred*; a one-constant change if the owner wants otherwise.
5. **The Gateway holds conversation text.** It already sees every reply (it sends them to the wingman
   model) and already stores dictation transcripts, so this changes retention, not exposure. The owner
   made this call on 1 September when he said to send the turns and store them.
6. **Agent-agnostic.** The Director pushes whatever `SessionHistoryReader.Read` produces for the session's
   agent (Claude Code, Codex, Pi, Grok, Copilot). The push carries `ConversationMessage` rows, the same
   shape Chat renders today, so the client contract does not change. *Inferred*.
7. **Deterministic triggers, not sampling.** The Director pushes on: a turn ending (its own
   Working-to-Waiting edge, which it observes directly, not through a 15-second sampler on the Gateway),
   a transcript pointer change (`/clear`, compaction), reconnect (catch-up), and a slow safety sweep for
   anything missed. *Inferred*.
8. **Catch-up by watermark.** Each pushed message carries its transcript line number and context id; the
   Gateway remembers the high-water mark per session and tells the Director on `Hello`, so a reconnecting
   Director sends only what the Gateway has not seen, and a fresh Gateway gets a full backfill. *Inferred*.
9. **The formula-based transcript path is deleted from every caller.** All Director reads go through
   `SessionHistoryReader.ResolveTranscriptPath` (pointer first). The by-id scan added in pull request
   2637 survives only inside that one resolver, as the answer for a session no hook has stamped yet.
   Stated ("that path should not be allowed to be used").
10. **Clients stay dumb.** The Gateway serves the same `SessionHistoryDto` and folds `HistoryState` itself.
    Nothing in a client decides what a turn means. Standing rule 7.

---

## The work, in the order it lands

Each phase is one or two pull requests, each proven and merged before the next starts. A fix and its
guard land together. Every phase says what it does NOT prove.

### Phase 0 - the store (Gateway only, no behaviour change)

- `SessionTurnEntity`: tenant, session id, director id, context id, transcript line number, sequence,
  role, parts (JSON), timestamp, is-meta, is-sidechain, received-at. Unique on (tenant, session,
  context, line). Migration in the same pull request. Both providers.
- `SessionTurnStore`: append a batch idempotently, read a session's messages in order, read the
  per-session watermark, prune older than retention. Retention wired into `SessionHistorySweep`.
- Contract: `PushedTurn` DTO in `CcDirector.Gateway.Contracts` mirroring `ConversationMessage`.
- **Proof:** unit tests on append idempotency, ordering, watermark, prune; migration applies on SQLite
  and Postgres design-time. **Not proven:** anything live.

### Phase 1 - the Director pushes, the Gateway stores

- Hub method `PushTurns(sequence, sessionId, contextId, PushedTurn[])` on `DirectorHub`, bound tenant,
  same sequence discipline as `PushDelta`, writes through `SessionTurnStore`, returns the accepted
  watermark. `Hello` returns `{ sessionId: watermark }` for the Director's live sessions.
- Director side: `TurnPusher` in `CcDirector.ControlApi`. Reads through `SessionHistoryReader.Read`,
  pushes messages above the watermark. Triggers per ruling 7. Backfill on connect. Bounded batch size.
- **Proof:** rows for a live session appear within seconds of a turn ending; kill the stream, run two
  turns, reconnect, only the missing turns arrive; `/clear` starts a new context id and the old rows
  stay. Revert the trigger and watch the test go red. **Not proven yet:** any reader uses the rows.

### Phase 2 - Chat reads the store

- `GET /sessions/{sid}/history` served from `SessionTurnStore`; `HistoryState` folded on the Gateway from
  the stored messages plus the pushed session state (`HistoryStateDeriver` already lives in Core).
- The `history` entry is removed from `TunnelCatchAllDispatch`; the Director's `history` verb goes with
  it once nothing calls it.
- **Proof:** Chat renders identically against a fixture session (same DTO); Chat renders with the
  Director offline; the round trip measured in milliseconds, not the tunnel's. **Not proven:** pushing
  chat changes to phones without polling (out of scope, see below).

### Phase 3 - the wingman is fed from the store

- A turn landing in the store IS the turn-end signal for narration: `PushTurns` raises the event the
  `TurnEndWatcher` sampler used to approximate. The 15-second sampler stays only for the display state
  it also drives, or goes if nothing else needs it.
- `WingmanVoiceService.GenerateOnceAsync` takes its last reply and recent context from the stored
  messages. An adapter builds the `TurnWidgetDto` list the translator already accepts, so the translator
  and its prompts do not change. The `turns` tunnel read is deleted from the voice path.
- The idle sweep becomes "narrate stored turns that have no audio", gated by the retry schedule from
  the 1 September branch (`feat/voice-retry-schedule-then-button`, rebased onto this), with its two
  review findings fixed: a spent schedule re-arms after a long interval, and the attempt is recorded
  before the in-flight marker is released.
- **Proof:** the session-111 shape - a session whose agent entered a worktree - narrates; a forced model
  timeout shows the retry schedule counting on the phone and the Generate button once spent. **Not
  proven:** narration for agents whose transcript reader is not yet supported.

### Phase 4 - one resolver, deletions

- Every remaining caller of `ClaudeSessionReader.GetJsonlPath` goes through
  `SessionHistoryReader.ResolveTranscriptPath`. The formula is no longer public.
- `turns` verb: deleted if no caller remains after Phase 3; otherwise it reads through the resolver.
- The Gateway's 45-second transcript re-read sweep is gone; what remains is the narration sweep.
- **Proof:** `git grep GetJsonlPath` outside the resolver returns nothing; the local gate and the parked
  Core and Gateway suites green; the session-111 scenario re-run end to end.

### Phase 5 - record and report

- This brief updated to past tense, the handoff notes, the inspections, and the evidence files landed
  on `main` as the last slice.
- The QA report to the owner: what it does for him, what is proven, what is not.

---

## Out of scope

- Pushing chat updates to phones over SignalR. The 2.5-second poll stays, but it now hits the store.
- Terminal streaming. Untouched.
- New agent kinds. Whatever `SessionHistoryReader` supports today is what gets pushed.
- Changing the wingman's prompts or what it says.

---

## Seats and their names

**The mission's name is "Turn Push", and every seat on it is named `Turn Push - <Role>`** - mission first,
a dash, then the role, as the conduct file requires. The seats:

- `Turn Push - Architect` - holds the design, this brief, and the merge authority. One at a time.
- `Turn Push - Manager` - drives a phase. Seated per phase from `handoff.md`, killed when the phase ends.
- `Turn Push - Worker` - one task, then gone.
- `Turn Push - Inspector` - a different agent family, reads the diff, writes its review to a file.

A seat whose name does not start with `Turn Push -` is not on this mission, whatever it is doing. This
matters on a machine running several missions at once: the roster is how the owner sees which seats belong
to which piece of work, and a seat named for its repository or its symptom tells him nothing about why it
exists.

## How this mission is run

Standalone-with-review per slice, seated in this worktree, with a Codex inspector run inline on each
pull request before it merges (the reviewer is a different agent family; it writes to a file, not the
chat). Re-seat with a fresh session from this brief and `handoff.md` at any phase boundary where the
context has grown. Conduct: `.claude/skills/mission/SKILL.md`.
