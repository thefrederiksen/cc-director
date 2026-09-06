# Phase-two independent inspection

**Inspected range:** `a0aef2c74..6c60c05bb`  
**Verdict:** **FAIL — phase two is not ready to advance.** The normal terminal and fleet-message paths now carry the intended labels, and the rejected-removal containment is guarded. However, the shipped recording-stage dictation path still misclassifies mixed composition as voice, while the operator prompt endpoint trusts client-controlled fields that can relabel a person's turn as agent-driven or voice. The utterance identifier is also a replayable boolean marker rather than evidence tying a submission to one untouched transcription.

## Findings

### I2-01 — Blocker — Recording-stage Speak Send classifies typed mixtures as voice

The R10 rule and dashboard disclosure say that speech is a voice turn only when the submitted words are exactly one untouched transcription; editing it or sending it alongside typed text makes the turn typed. The implementation enforces that rule only on the synchronous PAUSED path.

The common Cockpit and phone path is different:

1. `DictationDialog`'s RECORDING-stage Send bypasses `commit` and `spokenDeliveryRef`; it emits captured audio plus `prefixText` (`packages/client-core/src/dictation/DictationDialog.tsx:419-463`).
2. The Cockpit and phone composers separately pass the typed text around the caret as `composeParts` (`apps/cockpit/src/sessions/SessionComposer.tsx:315-342`, `apps/mobile/src/components/SessionControls.tsx:223-250`).
3. The durable request persists `before`, `after`, and `prefix` (`packages/client-core/src/dictation/backgroundSend.ts:274-289`).
4. The Gateway joins all of those strings with the new transcript into one message (`src/CcDirector.Gateway/Api/GatewayDictationEndpoint.cs:537-543`) and then unconditionally sets `DeliveryUploadId` on that whole message (`src/CcDirector.Gateway/Api/GatewayDictationEndpoint.cs:607`).
5. The Director treats any nonblank `DeliveryUploadId` as `SendSource.Delivery`, hence voice (`src/CcDirector.ControlApi/SessionCommandExecutor.cs:175-181,204-229`).

Therefore `A ` + spoken words + ` B`, sent while the recorder is still running, is recorded as one voice turn even though `A` and `B` were typed. Pause/resume makes the mismatch worse: an edited paused transcript can become `prefixText`, then a recording-stage Send labels the edited prefix and the new transcript together as voice. The same content can be typed when sent from PAUSED and voice when sent from RECORDING.

The new Cockpit and phone tests expose but do not check the defect. Each recording-stage test deliberately supplies `A`/`B` and asserts only that those values reach `backgroundTranscribeAndSend` (`apps/cockpit/src/sessions/composerSpeakSend.test.tsx:145-187`, `apps/mobile/src/components/sessionControlsSpeakSend.test.tsx:116-146`). The R10 classification assertions pause first and therefore exercise only the synchronous route (`apps/cockpit/src/sessions/composerSpeakSend.test.tsx:190-253`, `apps/mobile/src/components/sessionControlsSpeakSend.test.tsx:193-222`). No test follows a mixed durable request through Gateway composition and Director attribution.

### I2-02 — Blocker — The operator prompt body can relabel the caller's own turn as agent-driven

`PromptRequest.AgentDriven` is a public request-body property (`src/CcDirector.Gateway.Contracts/PromptRequest.cs:23-35`). The authenticated operator route overwrites the client-supplied `Surface`, but it does not overwrite or reject `AgentDriven` before forwarding the same DTO (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs:2795-2808,2864`). At the Director, `AgentDriven = true` overrides the incoming source with `SendSource.Agent`, suppresses the human origin, increments the agent-driven tally, and emits an agent submission (`src/CcDirector.ControlApi/SessionCommandExecutor.cs:204-229`).

A device-authenticated caller can consequently submit its own prompt with `agentDriven: true`. The text is delivered normally, but the person's turn is counted as somebody else's agent-driven turn rather than their typed turn. This is a data-integrity failure at the public operator boundary. It does not require forging a fleet sender or crossing an account boundary.

The R12 producer test cannot catch this. It reads `GatewayEndpoints.cs` as text, counts `new PromptRequest` expressions, and searches for two literal assignments (`src/CcDirector.Gateway.UnitTests/AgentDrivenTurnChokepointTests.cs:194-233`). The behavioral tests construct `PromptRequest { AgentDriven = true }` directly at `SessionCommandExecutor`; they prove the consumer works when told, not that the mapped fleet routes are the only callers allowed to tell it. A route test that deserializes a hostile operator body would fail on the current code.

### I2-03 — Blocker — `DeliveryUploadId` is not an utterance claim and is replayable

R10 describes the returned delivery id as the identity of the utterance that was just transcribed, carried only when the submitted text is exactly that transcription. On the receiving side, the value is never looked up or verified. The only runtime decision is `!string.IsNullOrWhiteSpace(request.DeliveryUploadId)` (`src/CcDirector.ControlApi/SessionCommandExecutor.cs:175-181`). The generic operator prompt route does not clear the field (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs:2795-2808,2864`).

As a result, any nonblank constant has the same authority as a real upload id. An authenticated caller can:

- attach a made-up id to arbitrary typed or edited text and have it counted as voice;
- attach a real id to text other than the transcript that produced it; or
- replay the same id on multiple prompts, producing multiple voice turns from one transcription.

The durable `/dictation` completion flow has its own idempotency record, but the new synchronous transcription-to-`/prompt` handoff does not use that completion gate. Returning an id from transcription is therefore correlation metadata, not attestation or a single-use claim.

The added UI tests mock `transcribeUtterance` with the fixed value `utt-77` and assert that it is forwarded. No changed test calls the real client transcription function and then verifies the id against Gateway state. Replacing the real returned id with any fixed nonempty string would leave those tests green and would not change the Director's classification. Likewise, no test covers replay or text/id mismatch.

### I2-04 — Major — The claimed single-write tally/ledger invariant can split on an observer exception

The code and mission state claim that `StampSubmission` makes the tally and submission ledger agree unconditionally. It does not make one atomic or exception-contained write:

- `StampSubmission` first calls `InputStats.RecordTurn` or `RecordAgentTurn`, then invokes `OnTurnSubmitted` (`src/CcDirector.Core/Sessions/Session.cs:2518-2542`).
- Both counter methods mutate their counters and then invoke the public `Changed` event without a guard (`src/CcDirector.Core/Sessions/SessionInputStats.cs:43-78`).
- `SendTextAsync` has already delivered the text to the backend before it calls `StampSubmission` (`src/CcDirector.Core/Sessions/Session.cs:2580-2616`).

If any `Changed` subscriber throws, the backend has received the turn and the in-memory tally has advanced, but execution never reaches `OnTurnSubmitted`; the activity outbox misses the submission and the caller sees an exception that can invite a retry. `OnTurnSubmitted` itself is guarded, but the earlier observer is not.

There is no current production subscription to `InputStats.Changed` in this tree, so this is a latent seam rather than a reproduced production incident. It still falsifies the documented invariant and leaves the advertised host persistence hook unsafe. The invariant test attaches only `OnTurnSubmitted`; it never attaches a throwing `Changed` observer.

## Direct answers to the inspection questions

- **Does the fleet-message marker reach the submission ledger, or only the tally?** In the current implementation it reaches both. The two fleet constructors set `AgentDriven` (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs:2697-2712,3986-3991`); the Director converts that to `SendSource.Agent`; `Session.StampSubmission` increments the agent lane and invokes `OnTurnSubmitted`; `ActivityEventProducer` writes `turn-submitted` with `agent-submit` (`src/CcDirector.Core/Activity/ActivityEventProducer.cs:80-117,198-210`). This conclusion is from route tracing, not from the added fleet test, which stops short of the mapped route and outbox.
- **Can a caller get its own turn counted as somebody else's by supplying a field?** Yes. `agentDriven: true` on the operator prompt body does exactly that (I2-02).
- **Does the dictation utterance claim survive edit, mix, and replay scrutiny?** No. The PAUSED single-segment checks cover a narrow happy path; recording-stage typed mixtures are labeled voice, arbitrary text can carry any nonblank id, and ids can be replayed (I2-01 and I2-03).

## Guard and mutation audit

| Change or mutation | Would the changed tests reject it? | Evidence gap |
|---|---:|---|
| Send a RECORDING-stage `A + speech + B` durable request that is ultimately counted as voice | No | Existing tests stop at the background function call. |
| Accept `agentDriven: true` from the public operator prompt body | No | Fleet tests use direct DTO construction plus source-string inspection, not route deserialization. |
| Return a fixed nonblank delivery id for every transcription | No | UI tests already use a fixed mock and the server checks only nonblankness. |
| Replay one delivery id with different prompt text | No | There is no prompt-side lookup, text binding, consumption, or replay assertion. |
| Revert the `useVoiceMode` forwarding argument at `packages/client-core/src/voice/useVoiceMode.ts:670` | No focused R10 guard found | Added R10 tests cover the Cockpit composer and phone controls, not the shared voice-mode hook. |
| Let `InputStats.Changed` throw after incrementing | No | The invariant test has no failing observer and therefore does not exercise the split. |

The source-count test also encodes a brittle inventory constant: exactly two occurrences of `new PromptRequest` in one file. A semantically equivalent factory extraction would fail, while a deserialization path that accepts an untrusted `AgentDriven` field remains green. This is proof over the wrong surface, not proof of the trust boundary.

## What did hold

- The normal terminal text and raw-byte submission paths now converge on `StampSubmission`, and the focused tests assert exact turn and character totals.
- Rejected `RemoveSession` calls no longer feed the removal tally; the focused test drives both rejected and accepted calls at the actual hub seam. This is containment only, consistent with the reconciled defect-two document; it does not establish the upstream reason a live session was selected for removal.
- The current fleet one-to-one and fanout implementations derive `AgentDriven` from authenticated route context rather than `FromSessionId`, and their marker propagates through the Director into the activity event producer. The remaining failure is that the same marker is also writable at the operator boundary.

## Verification boundary

I did not accept the mission's green-count statements as independent evidence. Focused `.NET` execution with `--no-restore` could not start because the pinned worktree has no `obj/project.assets.json`; Cockpit and phone Vitest execution could not start because dependencies are not installed (`vitest` was not found). Restoring or installing would mutate this inspection worktree, which the inspection contract forbids. The findings above are based on direct diff review, caller/consumer tracing, positive production inventories, and explicit revert/mutation analysis.

Phase two should remain failed until the two public attribution markers are Gateway-authoritative, the dictation classification rule is applied to the durable recording-stage composition path, and behavioral tests cross the real HTTP/tunnel/submission-ledger seams with hostile, mixed, edited, and replayed inputs.
