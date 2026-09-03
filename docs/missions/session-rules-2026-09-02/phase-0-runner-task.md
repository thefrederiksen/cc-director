# Phase 0 - Worker task: the runner and the corpus tests

You are a Worker on the DevThrottle Session Rules mission, phase 0. Your Manager curates the corpus of
real captured screens; you build the thing that puts every case through the real engine and the tests
that keep the corpus honest. Read `phase-0-brief.md` in this folder first - it is short and it is the
acceptance. Then build exactly what is below. Do not build a screen store, a migration, or a capture
service. Do not compose screens. Do not open a pull request; commit and push on your branch and report
to the Manager in ONE single line (fleet messages truncate at the first newline).

## Where things go

| Thing | Path |
| --- | --- |
| The runner (a console project, in the solution) | `src/CcDirector.Rules.ScreenHarness/` |
| The corpus the Manager is writing | `src/CcDirector.Rules.ScreenHarness/corpus/` |
| The corpus tests and the guard test (pure, no model, no network) | `src/CcDirector.Gateway.UnitTests/Rules/ScreenCorpusTests.cs` and `src/CcDirector.Gateway.UnitTests/Rules/ScreenHarnessGuardTests.cs` |

The corpus may be empty or partial while you work. Build against the FORMAT below and test with two or
three cases of your own under a temporary directory in your tests - never under `corpus/`, which is the
Manager's. The corpus tests must read the real `corpus/` directory by walking up from
`AppContext.BaseDirectory` to the directory that holds `cc-director.sln` (precedent:
`src/CcDirector.Core.Tests/AgentPluginArchitectureGuardTests.cs`). Add the console project to
`cc-director.sln`. Do NOT add it to `scripts/test-local.ps1` - it calls a live model and is run by hand.

## The corpus format

`corpus/rules.json` - the standing instructions every case is judged against, as one array:

```json
[
  {
    "id": "a guid",
    "instruction": "what the account said, in its own words",
    "screenDescription": "what the rule is watching for",
    "triggerWords": ["cheap", "words"],
    "calls": [],
    "cooldownSeconds": 60,
    "dailyCap": 5
  }
]
```

Every rule has scope "all sessions" (`RuleScope` with every part null), state `RuleState.DryRun`, an
empty `PromotedBy`, and created/updated stamps you choose. `calls` is a list of
`{ "name": "...", "arguments": { "parameter": "value" } }` and may be empty.

`corpus/cases/<case-id>/screen.txt` - the captured screen, BYTES AS CAPTURED. It may contain non-ASCII
characters and `\r\n` line endings; read it as UTF-8 and split on line breaks into the rows the engine
reads. Do not trim, tidy, or re-encode it.

`corpus/cases/<case-id>/case.json`:

```json
{
  "id": "the directory name",
  "expected": "act",
  "expectedRuleId": "the guid of the rule that should act (only when expected is act)",
  "kind": "positive",
  "reason": "why that is the right answer, in plain English",
  "facts": {
    "agent": "ClaudeCode",
    "repositoryPath": "",
    "machine": "SOREN_NORTH",
    "mission": "",
    "activityState": "WaitingForInput"
  },
  "factsNote": "how the facts were established",
  "source": {
    "method": "turn-package screen tail",
    "sessionId": "the session it came from",
    "capturedUtc": "2026-06-11T16:51:00Z",
    "detail": "free text"
  },
  "nonAscii": true,
  "secretsChecked": true
}
```

`expected` is `act` or `decline`. `kind` is one of: `positive`, `negative-documentation`,
`negative-code`, `negative-report`, `negative-own-state-different-situation`, `negative-substring`.
Every `negative-*` kind expects `decline`; `positive` expects `act`.

## The runner - it goes through the real engine, not a copy

This is the acceptance row most likely to be quietly broken, so it is a structural rule: **the runner
never builds a prompt and never reads a reply.** It constructs the real `RuleEvaluator`
(`CcDirector.Gateway.Rules`) with an `IRuleEnvironment` whose reads come from the case and whose model
call is the real `HostedInferenceBrain` (`CcDirector.Gateway.Wingman`), and it calls
`RuleEvaluator.EvaluateAsync` once per case per model. The evaluator then runs the same free checks
(`RuleCandidateFilter.Choose`), the same `RuleAgentContract.BuildPrompt` and `RuleAgentContract.Read`,
the same `RuleCheckRunner.Run` and the same `RuleReasonGrounding.Check` that production runs, because
it IS the production class. Read `RuleEvaluator.cs` and `GatewayRuleEnvironment.cs` (the production
wiring) before you write the environment; mirror the production wiring, not a simplification of it.

The environment for one case:

- `Rules(tenant)` returns the corpus rules, every one in `RuleState.DryRun`.
- `FiringsFor` returns an empty list (no cooldown, no daily cap in play).
- `ReadSessionFacts` returns the case's `facts` as `RuleSessionFacts` with the case id as the session id.
- `ReadScreenRowsAsync` returns the case's screen rows, the same rows on every call (so the evaluator's
  re-read before the keystroke passes, exactly as it would on an unchanged live screen).
- `AskAgentAsync` builds `new HostedInferenceBrain(TranscriptionEndpointResolver.DevThrottleBaseUrl, key,
  model, log: ...)` where `key` is `new KeyVault().Get(TranscriptionEndpointResolver.DevThrottleKeyName)`
  (the same vault the Gateway reads) and `model` is the `IncludedModelId` under test, calls `AskAsync`,
  measures the elapsed time of that one call with a `Stopwatch`, and returns the text. Mirror
  `GatewayRuleEnvironment.AskAgentAsync`: a thrown exception is caught, logged as the reason, and returned
  as null - the evaluator then records a refusal, which the report counts as "no answer" and names the
  exception (a `TimeoutException` is a TIMEOUT and must be counted as one). Use the brain's default
  call timeout; do not lengthen it.
- `TypeIntoSessionAsync` THROWS `InvalidOperationException("the harness was asked to type; a dry-run rule
  must never reach the send")`. Every corpus rule is a dry run, so the evaluator never calls it; if it
  ever does, the run must fail loudly, not record a phantom keystroke.
- `RecordFiring` keeps the draft in a list on the environment and returns a new guid; `CompleteFiring`
  updates that list. The report is read from these drafts and from the returned `RulePass`.
- `NowUtc` is `DateTime.UtcNow`.

Use a fresh `RuleEvaluator` per model run and the case id as the session id, so the evaluator's
"screen unchanged" memory never carries from one case to the next.

Command line: `dotnet run --project src/CcDirector.Rules.ScreenHarness -- [--models wingman,wingman-fast]
[--corpus <dir>] [--out <dir>] [--case <id>]`. The models are named by their `IncludedModelId`:
`wingman` is `IncludedModelId.Wingman` (the thinking model production uses today) and `wingman-fast` is
`IncludedModelId.WingmanFast`. Default: both models, the corpus beside the project, output to
`--out` (default `harness-out/` under the project, git-ignored). Cases run SEQUENTIALLY within a model
so each timing is one call under no self-inflicted load; the two models may run concurrently with each
other.

## What the report must say

Write `report.md` and `results.json` to the output directory. Per model, per case, one row: the case id,
its kind, the expected answer, the answer given, right or wrong, the model call time in seconds to one
decimal, and the evaluator's outcome (`RulePass.What`) with its detail shortened. The "answer given" is
read off the recorded firing: `act` when the pass is `dry-run`; `decline` when it is `declined`;
`act (ungrounded)` when the pass is `ungrounded` (the model said act and the engine refused it for citing
text the screen does not contain - the MODEL was still wrong); `abandoned` when a staked check failed;
`no answer` when the pass is `refused` and the environment logged an exception; `refused` for any other
refusal; `not asked` when the pass is `no-candidates` or `stopped-before-any-rule` (a corpus defect - the
free checks never let the model see it).

Right or wrong: expected `act` is right only when the answer is `act` AND the recorded firing's rule id
equals `expectedRuleId`. Expected `decline` is right only when the answer is `decline`. Everything else
is wrong.

Then, ABOVE the per-case table and in its own heading, per model:

- **Wrong answers on negatives** - the count of negative cases whose answer was `act` or
  `act (ungrounded)`. This is the number the phase is judged on. State it as a count even when it is 0.
- Of those, how many reached `act` (would have typed) versus `act (ungrounded)` (the engine's grounding
  check stopped it).
- Timeouts (count) and other no-answers (count).
- Wrong answers on positives (count).
- Cases not asked (count) - a corpus defect, listed by id.
- Total right, total wrong, median and maximum model call time.

Exit code: 0 only when no case was `not asked` and no negative was answered `act` or `act (ungrounded)`
on ANY model; otherwise 1. Print the summary to the console as well as writing it.

`results.json` carries the same rows plus, for every case, the full recorded firing drafts (understanding,
decision, reason, grounding statement, the check runs) so a reader can argue with a verdict.

## The corpus tests (pure, in `CcDirector.Gateway.UnitTests`)

Reading the real `corpus/` directory, and passing on an empty corpus is NOT acceptable - a corpus with
fewer than 20 cases fails, so these tests are RED in your worktree until the Manager's cases are merged
onto the phase branch. That is intended. Commit them red, run the rest of the gate, and in your report
line name the corpus tests as the expected reds and nothing else. The Manager runs the whole gate green
after the merge.

- At least 20 cases; at least half have `expected: decline`.
- The three named negative kinds (`negative-documentation`, `negative-code`, `negative-report`) are each
  present at least once.
- Every case has a non-empty `reason`, a non-empty `source.method`, a non-empty `source.sessionId`, and a
  `screen.txt` with at least one non-blank line.
- Every case with `expected: act` names an `expectedRuleId` that exists in `rules.json`.
- For EVERY case, `RuleCandidateFilter.Choose` (the real one) with the corpus rules, the case's facts, the
  case's screen text (rows joined with `\n` and trimmed the way `RuleEvaluator.Join` does), a null
  previous screen, no firings and `DateTime.UtcNow` chooses AT LEAST ONE rule. This is what makes a
  negative a negative: the trigger words are on the screen and the model IS asked. A case that the free
  checks would skip proves nothing about the model and fails this test by name.
- Every `screen.txt` whose `case.json` says `nonAscii: false` is pure ASCII, and every one that says
  `nonAscii: true` really does contain a non-ASCII character. (So the flag is a fact, not a guess.)
- `rules.json` parses into `SessionRule` records that the write-time validator accepts:
  `RuleCallValidator.ValidateAll(rule.Calls, RulePrimitiveRegistry.Default)` is valid for every rule.

## The guard test (pure, in `CcDirector.Gateway.UnitTests`)

Precedent: `RulesTypeNothingGuardTests.cs` and `NoListenerDependencyGuardTests.cs` read built assemblies
with Mono.Cecil. Add a `ProjectReference` from the unit-test project to the harness project so its
assembly lands in the test output, then assert against `CcDirector.Rules.ScreenHarness.dll`:

- It calls `CcDirector.Gateway.Rules.RuleEvaluator::EvaluateAsync` (the evidence it goes through the
  engine).
- It contains NO call to `RuleAgentContract::BuildPrompt` or `RuleAgentContract::Read`, and no string
  literal containing `--- the session's screen ---` (the evidence it is not a second implementation of
  the question).
- It contains NO call to `System.Net.Http.HttpClient::SendAsync`, `::PostAsync`, `::PostAsJsonAsync`,
  `::GetAsync`, `::GetStringAsync` (the evidence its only model call is the real `HostedInferenceBrain`).

Watch each guard fail first: put the forbidden call in, see the test go red with the reported symptom,
take it out, quote both runs in your report line and in the commit message body.

## The gate

`.\scripts\test-local.ps1` green (the Postgres proof rig `cc-pg-test` on port 55432 must be up). ASCII
only in everything you write. No mention of any assistant, model, vendor or AI tool in a commit message,
a document or a comment - naming the two models under test by their `IncludedModelId` is required and is
not that.

## How to finish

Commit on your branch `mission/rules-p0-runner`, push it, and send the Manager (session `d7fefdb9`) ONE
line: what is built, the guard runs you watched fail, and the test-local result. Then stop.
