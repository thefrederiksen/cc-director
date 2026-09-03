# Session Rules - Inspection B

## Verdict

**CHANGES REQUIRED**

Landing inspected: `ffe1a74ebeb11e355d9936704fa8a454aedfaa60` on `inspect/session-rules-b`.

The repair closes several concrete defects from inspection A, including the false red-first commits for the validator and store, the exact primitive registry, null rejection, the agent prompt's limited screen view, and the store's missing-scope creation path. The landing is not ready to merge. The phase 2 evidence is not fully reproducible, the human promotion boundary is still a convention, overlapping passes can exceed both action bounds, and an action can occur before its firing record is durably accepted.

## Findings, worst first

### 1. CRITICAL - The phase 2 red evidence is not reproducible as reported

**What is wrong:** Two phase 2 red claims are not durable evidence. At commit `62133c497`, the repository's documented Rules filter collected **125** tests and reported **47 failed, 78 passed, exit 1**, not the claimed **47 failed, 55 passed**. Neither report names a different exact filter that produces the claimed result. The send-outcome red row names `(working tree)` rather than a commit, so it cannot be checked out at all.

**Where:** `docs/missions/session-rules-2026-09-02/phase-2-report.md:37`; `docs/missions/session-rules-2026-09-02/qa-report.md:584-589`.

**Why it matters:** Red-first evidence is the proof that the tests could detect absent or wrong behavior. A count that does not reproduce and an uncommitted red state cannot support that claim. Under the mission's evidence rule, this truth defect outranks an ordinary missing test.

**What would make this ready:** Name the exact command for every red run, make every red state a commit (or mark it `PENDING`), rerun each named command at that commit, and reconcile the recorded TRX totals and exit code with the actual output.

### 2. CRITICAL - Any Gateway code can mint the alleged human promotion grant

**What is wrong:** `RulePromotionGrant.FromAuthenticatedRequest` is public and accepts an arbitrary identity string and acknowledgement string. It proves only that two caller-supplied strings are nonblank. It has no authenticated request, verified principal, unforgeable capability, or dependency on the endpoint. The test helper demonstrates the bypass by minting a grant from constants outside a request.

**Where:** `src/CcDirector.Gateway/Rules/RulePromotionGrant.cs:60-75`; `src/CcDirector.Gateway.UnitTests/Rules/RulePromotionBoundaryTests.cs:54-56`; `src/CcDirector.Gateway/Rules/SessionRuleStore.cs:180-211`.

**Why it matters:** Automated Gateway code can call the public factory with invented strings and promote a dry-run rule. The boundary therefore does not put a person between a standing instruction and its first real use, despite comments saying nothing automated can promote.

**What would make this ready:** Make promotion require evidence that automated callers cannot construct, issued only after authentication and an explicit human action. Keep the store boundary narrow enough that a plain identity or acknowledgement string cannot substitute for that evidence. Add a structural negative test proving non-endpoint production code cannot obtain or manufacture the capability, plus a positive endpoint test.

### 3. CRITICAL - Overlapping passes can type twice past both cooldown and daily cap

**What is wrong:** Every turn-end signal starts a separate fire-and-forget task. Candidate selection reads prior firings before the agent call and the send, with no per-session serialization, reservation, transaction, or atomic cap update. Two passes can both observe zero actions and both act.

**Where:** `src/CcDirector.Gateway/GatewayHost.cs:2560-2574`; `src/CcDirector.Gateway/Rules/RuleCandidateFilter.cs:127-153`; `src/CcDirector.Gateway/Rules/RuleEvaluator.cs:193-196,286-302`.

**Observed proof:** An inspection-only concurrency test synchronized two evaluations inside `FiringsFor` for one live rule with `dailyCap = 1` and `cooldownSeconds = 300`. Both returned `Acted`; the fake environment observed two sends and two firing records. Result: **1 passed, 0 failed, exit 0**. The temporary probe was removed after inspection.

**Why it matters:** Cooldown and daily cap are explicit bounds on autonomous typing. They fail at exactly the overlap the host permits.

**What would make this ready:** Serialize evaluation and action per tenant/session, or atomically reserve an action under the same durable boundary that enforces cap and cooldown. Add a deterministic concurrent test that requires exactly one send and one action record.

### 4. CRITICAL - Typing happens before the product can accept its firing record

**What is wrong:** The evaluator sends text first and records the firing afterward. The reply contract accepts a missing or blank `reason`, grounding treats it as valid, and the live path can therefore type. The production store then rejects the blank reason. Database errors and a rule deleted during the asynchronous gap create the same ordering failure.

**Where:** `src/CcDirector.Gateway/Rules/RuleAgentContract.cs:189-201`; `src/CcDirector.Gateway/Rules/RuleReasonGrounding.cs:47-54`; `src/CcDirector.Gateway/Rules/RuleEvaluator.cs:286-310`; `src/CcDirector.Gateway/Rules/SessionRuleStore.cs:249-325`.

**Why it matters:** The firing record is the accountable product. A real side effect can occur with no durable statement of screen, reasoning, checks, text, or outcome. The host catches the resulting exception and only writes a log line.

**What would make this ready:** Validate the complete firing draft before any send, reject an ACT with a blank reason at the reply boundary, and use a durable intent/outbox protocol whose state exists before the external side effect and is reconciled afterward. Test store rejection and rule deletion across the async gap with a real store-backed environment.

### 5. HIGH - The claimed structural write gate can be bypassed and is incomplete

**What is wrong:** The gate runs only from `SaveChanges` and inspects only tracked `SessionRuleEntity` writes. The public context and public DbSets remain available. An ORM bulk `ExecuteUpdate` performs SQL immediately and bypasses `SaveChanges`, so it can move a dry-run row to `live` without `PromotionInEffect`. Direct writes to `SessionRuleFirings` never enter the rule gate at all. Even tracked rule writes validate only tenant, call names, initial dry-run state, and promotion marker; they do not enforce a nonblank instruction, screen description, trigger words, explicit scope, positive cooldown, or positive daily cap.

**Where:** `src/CcDirector.Gateway/Data/GatewayDbContext.cs:53-120,312-316`; `src/CcDirector.Gateway/Data/GatewayDatabase.cs:478-500`; `src/CcDirector.Gateway/Rules/SessionRuleStore.cs:123-145,249-325`.

**Observed proof:** Inspection-only tests created a rule through the store, promoted it with `ExecuteUpdate`, and then read it back as live; another test saved an empty invented firing directly through `SessionRuleFirings`. Both paths succeeded. Together with the malformed-check probe below: **3 passed, 0 failed, exit 0**. The temporary probes were removed after inspection.

**Why it matters:** The comment says this is where rule writes "cannot be gone round," but production APIs can go around it. The original missing-scope and invented-firing defects remain possible through those APIs.

**What would make this ready:** Encapsulate the entities/context so feature writes cannot issue bulk SQL or directly write either table, enforce all record invariants at a boundary shared by every allowed write path, and add adversarial tests for bulk updates and direct firing writes. Narrow or remove any public write surface that cannot be guarded truthfully.

### 6. HIGH - Idle is checked before a long asynchronous gap, not before typing

**What is wrong:** Session activity is read once before the agent call. Immediately before typing, the evaluator re-reads only the terminal screen. A new owner turn can make the session Working while the visible rows are temporarily unchanged, and the stale evaluation will still type.

**Where:** `src/CcDirector.Gateway/Rules/RuleEvaluator.cs:179-194,264-287`; `src/CcDirector.Gateway/GatewayHost.cs:2562-2574`.

**Why it matters:** "Idle sessions only" is a primary safety bound. Screen equality is not proof that activity is still idle, especially during prompt submission or before new output appears.

**What would make this ready:** Re-read authoritative session facts immediately before the send and abandon unless the same session remains idle and eligible. Add a deterministic test that changes activity to Working while leaving the screen unchanged during the agent call.

### 7. HIGH - Grounding accepts an ACT reason with no checkable screen evidence

**What is wrong:** If the reason contains no quoted passage, grounding returns `IsGrounded = true` and says there was nothing to check. The test explicitly blesses that behavior. The evaluator consequently permits an invented unquoted factual reason to authorize an ACT.

**Where:** `src/CcDirector.Gateway/Rules/RuleReasonGrounding.cs:47-54`; `src/CcDirector.Gateway.UnitTests/Rules/RuleReasonGroundingTests.cs:65-74`; `src/CcDirector.Gateway/Rules/RuleEvaluator.cs:231-259`.

**Why it matters:** The repair report marks A12 closed, but only the easy case of a false quotation is refused. An agent can avoid the check by quoting nothing. That is another absence being treated as positive grounding.

**What would make this ready:** Require every ACT reason to provide structured, checkable evidence derived from the exact screen slice supplied to the agent, and refuse an ACT when that evidence is absent. Keep decline permissive but record absence explicitly. Add an ACT test whose plausible-sounding reason has no citation and must not type.

### 8. HIGH - Malformed or missing check collections silently become no checks

**What is wrong:** The reply parser reads checks only when `checks` is an array; a missing property or an object is silently converted to an empty list. The create endpoint does the same and also silently drops non-object array members. This contradicts the endpoint comment that both paths use the same reader and preserve one meaning.

**Where:** `src/CcDirector.Gateway/Rules/RuleAgentContract.cs:172-187`; `src/CcDirector.Gateway/Api/SessionRuleEndpoints.cs:181-190`.

**Observed proof:** An inspection-only test submitted an ACT reply with `checks` as an object. The reply was accepted with zero checks. It was part of the **3 passed, 0 failed, exit 0** probe noted in finding 5.

**Why it matters:** A malformed safety check can disappear and the action can proceed as though no check was requested. This is especially dangerous for path containment, freshness, and failure detection primitives.

**What would make this ready:** Make the property required with array shape on both inputs, reject every malformed element, and use one strict parser. Add negative tests for missing, null, object-shaped, scalar, and mixed-element check collections at both boundaries.

### 9. HIGH - An unreachable prompt can be recorded as typed

**What is wrong:** A missing route before the call is classified `NotSent`, but every false return from `PostPromptAsync` is classified `NotConfirmed`. That lower layer deliberately maps an absent tunnel result and a remote refusal to the same false tuple. The evaluator records `typedText` and says "typed into the session" for all `NotConfirmed` results.

**Where:** `src/CcDirector.Gateway/Rules/GatewayRuleEnvironment.cs:154-168`; `src/CcDirector.Gateway/Api/SessionVerbClient.cs:99-111`; `src/CcDirector.Gateway/Rules/RuleEvaluator.cs:289-310`.

**Why it matters:** A disconnect between route lookup and send, an absent tunnel result, a missing session, or another remote refusal can mean nothing was typed. The durable record then states an external side effect without evidence.

**What would make this ready:** Preserve distinct transport outcomes end to end. Record typed text only after positive acknowledgement that the prompt was accepted; otherwise record `NotSent` or an explicit unknown outcome without claiming the text landed. Cover route-loss and remote-refusal cases with production-environment tests.

### 10. HIGH - The type-nothing guard does not cover the whole feature it claims to guard

**What is wrong:** `IsFeatureType` includes the Rules namespace and specially named data entities. It excludes `SessionRuleEndpoints` in the API namespace and the rule launch code in `GatewayHost`, both listed as phase 2 feature pieces. Typing or command-routing code placed there can remain green.

**Where:** `src/CcDirector.Gateway.UnitTests/Rules/RulesTypeNothingGuardTests.cs:240-251,308-402`; `src/CcDirector.Gateway/Api/SessionRuleEndpoints.cs`; `src/CcDirector.Gateway/GatewayHost.cs:2554-2574`; `docs/missions/session-rules-2026-09-02/phase-2-report.md:25`.

**Why it matters:** The guard's scope is narrower than the production feature surface, so it does not prove the stated architectural boundary.

**What would make this ready:** Derive the guarded feature inventory from explicit production composition or mark every feature type with a shared boundary marker, including endpoints and host wiring. Mutation-prove the guard by adding a send call independently in each included feature piece and observing a failure.

### 11. HIGH - The test runner still passes partially collected evidence

**What is wrong:** The runner rejects only a global collected total of zero. It prints every zero-test project as PASS when at least one other project collects tests, and it does not compare project totals or filtered inventory against a recorded expectation despite saying totals must be at or above baseline.

**Where:** `scripts/test-local.ps1:203-204,263-285,336-377`.

**Observed proof:** On the inspected landing, the composite filter `FullyQualifiedName~RuleReasonGroundingTests|FullyQualifiedName~DefinitelyNoSuchTest_dnkeyz` collected eight tests from the first term, collected nothing from the second term, and exited **0**. The output also labeled the other zero-test projects PASS.

**Why it matters:** Removing or renaming a required test, misspelling one side of a composite filter, or skipping a whole project can still produce green evidence. The instrument proves that something ran, not that the named inventory ran.

**What would make this ready:** Declare the expected test inventory or count per evidence command and project, fail when any required item is absent, and make a partial-collection probe red. Keep the existing all-zero probe as a separate guard.

### 12. MEDIUM - The public record projection hides the new accountability fields

**What is wrong:** The stored rule includes `PromotedBy`, and the stored firing includes `Grounding`, but the only Session Rules API projection omits both fields.

**Where:** `src/CcDirector.Gateway/Rules/SessionRule.cs:31-70`; `src/CcDirector.Gateway/Api/SessionRuleEndpoints.cs:112-140`.

**Why it matters:** The account cannot retrieve who promoted a rule or whether the stated reason matched the screen, even though those fields were added specifically to make the action accountable. The record exists in storage but is not delivered through the feature's public read surface.

**What would make this ready:** Include `promotedBy` and `grounding` in the projections, define their wire contracts, and add endpoint tests that create, promote, fire, read, and assert every accountability field.

## Inspection A disposition

| A finding | Disposition at landing B | Evidence |
|---|---|---|
| 1. Dry-run promotion has no human boundary | **OPEN** | A public string-based grant remains forgeable; see finding 2. |
| 2. The validator is not a structural write gate | **OPEN** | Bulk updates and direct firing writes bypass it; tracked writes enforce only part of the record; see finding 5. |
| 3. Two red-first commits contain no named tests | **CLOSED** | `1eeaca050` now fails 18/18 validator tests and `c6bdef6c8` fails 21/21 store tests under their named filters. |
| 4. Stored agent input is wider than the screen | **CLOSED** | The prompt now contains instruction plus bounded screen text, not hidden machine state; the repaired red probe at `2cb4b0131` fails 1/24 as claimed. |
| 5. Missing scope becomes every session | **PARTIAL** | The store create route refuses absent scope, but direct tracked writes can still store an all-empty scope; see finding 5. |
| 6. Type-nothing guard is narrow | **OPEN** | It covers more direct and transitive calls but still excludes endpoint and host feature pieces; see finding 10. |
| 7. Registry does not enforce exactly five primitives | **CLOSED** | The exact inventory guard is present, and the known-bad red at `1ee65c3eb` fails after `14a858d19` as reported. |
| 8. Null calls crash validation | **CLOSED** | Validator null cases now reject through the domain result, and the repaired red probe is real. |
| 9. Firing store accepts empty or invented records | **OPEN** | Store methods reject them, but the public firing DbSet still accepts direct invented rows; see finding 5. |

## Evidence checked

All commands below were run at the named commit with `scripts/test-local.ps1`; counts are TRX totals, not console-message absence.

| Commit | Filter | Observed result | Assessment |
|---|---|---|---|
| `62133c497` | `FullyQualifiedName~Rules` | 47 failed, 78 passed, total 125, exit 1 | Red is real; documented passed count is not. |
| `a7bf10b2f` | `FullyQualifiedName~RulesTypeNothingGuardTests` | 1 failed, 3 passed, total 4, exit 1 | Reproduces. |
| `1eeaca050` | `FullyQualifiedName~RuleCallValidatorTests` | 18 failed, 0 passed, total 18, exit 1 | Repaired red reproduces. |
| `c6bdef6c8` | `FullyQualifiedName~SessionRuleStoreTests` | 21 failed, 0 passed, total 21, exit 1 | Repaired red reproduces. |
| `78399eab2` | `FullyQualifiedName~Rules` | 31 failed, 142 passed, total 173, exit 1 | Reproduces. |
| `1ee65c3eb` | type-nothing plus primitive-registry filters | 0 failed, 12 passed, total 12, exit 0 | Known-bad fail-open state reproduces. |
| `14a858d19` | same two filters | 2 failed, 13 passed, total 15, exit 1 | Repaired red reproduces. |
| `2cb4b0131` | `FullyQualifiedName~RuleEvaluatorTests` | 1 failed, 23 passed, total 24, exit 1 | Reproduces. |
| `6467aa69a` | nonexistent filter | total 0 across all projects, exit 0 | Old fail-open reproduces. |
| `65e88f4f0` | same nonexistent filter | total 0, exit 3 | All-zero repair reproduces. |
| `ffe1a74eb` | `FullyQualifiedName~Rules` | 0 failed, 168 passed, total 168, exit 0 | Current targeted suite is green. |

No runtime path was found that parses, compiles, evaluates, or interprets program text from stored rules. Rule checks remain the five fixed primitives, and their arguments are values rather than executable programs. Tenant query filters, active-tenant reads, explicit store scope validation, dry-run evaluation, decline handling, null validation, and exact primitive inventory all have positive targeted coverage. Those strengths do not close the findings above.

No long or parked suite was run. No HTTP request was made.
