using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Rules;
using CcDirector.Rules.ScreenHarness;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE REPORT'S VERDICT, PROVEN ON KNOWN INPUTS (Session Rules mission, phase 0). The harness reads the
/// answer off the evaluator's pass and the recorded firing, and decides right or wrong from the case's
/// written-down expectation. That decision is the deliverable the phase is judged on - "wrong answers on
/// negatives" - so it is pinned here, pure, with no model and no corpus: a pass of each outcome in, the
/// named answer out, and the summary counting what it says it counts.
/// </summary>
public sealed class ScreenHarnessJudgementTests
{
    private static readonly Guid TheRule = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid AnotherRule = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly IReadOnlyList<SessionRule> Rules = new[]
    {
        new SessionRule(TheRule, "switch model at the limit", "a limit notice", new[] { "limit" },
            Array.Empty<RulePrimitiveCall>(), RuleScope.AllSessions, 60, 5, RuleState.DryRun, "",
            ScreenCorpus.RuleStampUtc, ScreenCorpus.RuleStampUtc),
    };

    private static ScreenCase Case(string expected, string kind, Guid? expectedRuleId = null) => new(
        Directory: "not-on-disk",
        Record: new CaseRecord
        {
            Id = "case-" + kind,
            Expected = expected,
            ExpectedRuleId = expectedRuleId?.ToString(),
            Kind = kind,
            Reason = "for the test",
            Facts = new CaseFacts { Agent = "Shell", ActivityState = "WaitingForInput" },
            Source = new CaseSource { Method = "test", SessionId = "s" },
        },
        ScreenRows: new[] { "You've reached your limit" },
        ScreenBytes: Array.Empty<byte>());

    private static CaseRuleEnvironment Environment(ScreenCase screenCase) =>
        new(Rules, screenCase, IncludedModelId.Wingman, "a-key-that-is-never-used-because-nothing-here-asks", _ => { });

    private static RuleFiringDraft Draft(Guid ruleId, string decision) =>
        new(ruleId, "s", "screen", "understanding", decision, "because the screen says limit", Array.Empty<RulePrimitiveRun>(), "", "outcome", "grounded");

    private static RulePass Pass(string what, params RuleFiringDraft[] recorded) => new(what, "detail", recorded);

    [Theory]
    [InlineData(RulePassOutcomes.DryRun, CaseAnswers.Act)]
    [InlineData(RulePassOutcomes.Declined, CaseAnswers.Decline)]
    [InlineData(RulePassOutcomes.Ungrounded, CaseAnswers.ActUngrounded)]
    [InlineData(RulePassOutcomes.Abandoned, CaseAnswers.Abandoned)]
    [InlineData(RulePassOutcomes.Refused, CaseAnswers.Refused)]
    [InlineData(RulePassOutcomes.NoCandidates, CaseAnswers.NotAsked)]
    [InlineData(RulePassOutcomes.StoppedBeforeAnyRule, CaseAnswers.NotAsked)]
    public void Each_pass_outcome_reads_as_the_named_answer(string outcome, string answer)
    {
        var screenCase = Case(CaseExpectations.Decline, CaseKinds.NegativeCode);
        Assert.Equal(answer, CaseAnswers.For(Pass(outcome), Environment(screenCase)));
    }

    [Fact]
    public async Task A_refusal_after_the_model_threw_reads_as_no_answer_and_a_timeout_is_counted_as_one()
    {
        // The environment's model call is the real hosted brain; a blank base URL cannot be given, so the
        // failure is provoked the way production sees it - the brain throws and the environment catches.
        // Here the environment is asked with a cancelled token so no request leaves the machine.
        var screenCase = Case(CaseExpectations.Decline, CaseKinds.NegativeReport);
        var environment = Environment(screenCase);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var reply = await environment.AskAgentAsync(TenantId.Local, "a prompt", cancelled.Token);

        Assert.Null(reply);
        Assert.NotNull(environment.ModelFailure);
        Assert.NotNull(environment.ModelCallTime);
        Assert.Equal(CaseAnswers.NoAnswer, CaseAnswers.For(Pass(RulePassOutcomes.Refused), environment));

        var result = HarnessRun.Judge("wingman", screenCase, Pass(RulePassOutcomes.Refused, Draft(TheRule, RuleDecisions.Refused)), environment);
        Assert.False(result.Right);
        Assert.Equal(CaseAnswers.NoAnswer, result.Answer);
        Assert.NotNull(result.Failure);
        // A cancelled call is not a timeout; the flag is reserved for the brain's own deadline.
        Assert.False(result.TimedOut);
    }

    [Fact]
    public void An_act_case_is_right_only_when_the_act_came_from_the_expected_rule()
    {
        var screenCase = Case(CaseExpectations.Act, CaseKinds.Positive, TheRule);

        var rightRule = HarnessRun.Judge("wingman", screenCase, Pass(RulePassOutcomes.DryRun, Draft(TheRule, RuleDecisions.Act)), Environment(screenCase));
        var wrongRule = HarnessRun.Judge("wingman", screenCase, Pass(RulePassOutcomes.DryRun, Draft(AnotherRule, RuleDecisions.Act)), Environment(screenCase));
        var declined = HarnessRun.Judge("wingman", screenCase, Pass(RulePassOutcomes.Declined, Draft(TheRule, RuleDecisions.Decline)), Environment(screenCase));
        var ungrounded = HarnessRun.Judge("wingman", screenCase, Pass(RulePassOutcomes.Ungrounded, Draft(TheRule, RuleDecisions.Refused)), Environment(screenCase));

        Assert.True(rightRule.Right);
        Assert.False(wrongRule.Right);
        Assert.False(declined.Right);
        Assert.False(ungrounded.Right);
        Assert.Equal(TheRule.ToString(), rightRule.FiringRuleId);
    }

    [Fact]
    public void A_decline_case_is_right_only_on_a_decline_and_an_act_in_either_form_is_wrong()
    {
        var screenCase = Case(CaseExpectations.Decline, CaseKinds.NegativeDocumentation);

        var declined = HarnessRun.Judge("wingman", screenCase, Pass(RulePassOutcomes.Declined, Draft(TheRule, RuleDecisions.Decline)), Environment(screenCase));
        var acted = HarnessRun.Judge("wingman", screenCase, Pass(RulePassOutcomes.DryRun, Draft(TheRule, RuleDecisions.Act)), Environment(screenCase));
        var ungrounded = HarnessRun.Judge("wingman", screenCase, Pass(RulePassOutcomes.Ungrounded, Draft(TheRule, RuleDecisions.Refused)), Environment(screenCase));
        var abandoned = HarnessRun.Judge("wingman", screenCase, Pass(RulePassOutcomes.Abandoned, Draft(TheRule, RuleDecisions.Abandoned)), Environment(screenCase));

        Assert.True(declined.Right);
        Assert.False(acted.Right);
        Assert.False(ungrounded.Right);
        Assert.False(abandoned.Right);
    }

    [Fact]
    public void The_summary_counts_wrong_negatives_in_both_forms_and_lists_the_cases_never_asked()
    {
        var negative = Case(CaseExpectations.Decline, CaseKinds.NegativeCode);
        var positive = Case(CaseExpectations.Act, CaseKinds.Positive, TheRule);

        var rows = new[]
        {
            HarnessRun.Judge("m", negative, Pass(RulePassOutcomes.DryRun, Draft(TheRule, RuleDecisions.Act)), Environment(negative)),
            HarnessRun.Judge("m", negative, Pass(RulePassOutcomes.Ungrounded, Draft(TheRule, RuleDecisions.Refused)), Environment(negative)),
            HarnessRun.Judge("m", negative, Pass(RulePassOutcomes.Declined, Draft(TheRule, RuleDecisions.Decline)), Environment(negative)),
            HarnessRun.Judge("m", negative, Pass(RulePassOutcomes.NoCandidates), Environment(negative)),
            HarnessRun.Judge("m", positive, Pass(RulePassOutcomes.Declined, Draft(TheRule, RuleDecisions.Decline)), Environment(positive)),
            HarnessRun.Judge("m", positive, Pass(RulePassOutcomes.DryRun, Draft(TheRule, RuleDecisions.Act)), Environment(positive)),
        };

        var summary = HarnessRun.Summarise("m", rows);

        Assert.Equal(6, summary.Cases);
        Assert.Equal(2, summary.WrongOnNegatives);
        Assert.Equal(1, summary.WrongOnNegativesThatReachedAct);
        Assert.Equal(1, summary.WrongOnNegativesStoppedByGrounding);
        Assert.Equal(1, summary.WrongOnPositives);
        Assert.Equal(new[] { "case-" + CaseKinds.NegativeCode }, summary.NotAsked);
        Assert.Equal(2, summary.Right);
        Assert.Equal(4, summary.Wrong);
        Assert.Null(summary.MedianSeconds);

        var report = HarnessRun.RenderReport(new[] { summary }, rows);
        Assert.Contains("WRONG ANSWERS ON NEGATIVES: 2", report);
        Assert.True(report.IndexOf("WRONG ANSWERS ON NEGATIVES", StringComparison.Ordinal) <
                    report.IndexOf("| case | kind |", StringComparison.Ordinal),
            "the negatives number must come above the per-case table");
    }

    [Fact]
    public async Task The_environment_refuses_to_type_and_records_what_the_evaluator_writes()
    {
        var screenCase = Case(CaseExpectations.Decline, CaseKinds.NegativeCode);
        var environment = Environment(screenCase);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            environment.TypeIntoSessionAsync(TenantId.Local, "d", "s", "text", CancellationToken.None));

        var id = environment.RecordFiring(TenantId.Local, Draft(TheRule, RuleDecisions.Act));
        environment.CompleteFiring(TenantId.Local, id, "typed", "done");

        var firing = Assert.Single(environment.Firings);
        Assert.Equal(id, firing.FiringId);
        Assert.Equal("typed", firing.Draft.TypedText);
        Assert.Equal("done", firing.Draft.Outcome);
        Assert.Empty(environment.FiringsFor(TenantId.Local, TheRule));
        Assert.Equal(screenCase.Id, environment.ReadSessionFacts(TenantId.Local, "ignored")!.SessionId);
    }

    [Fact]
    public void Only_the_two_included_models_are_accepted_by_name()
    {
        Assert.Equal(IncludedModelId.Wingman, HarnessRun.ModelNamed("wingman"));
        Assert.Equal(IncludedModelId.WingmanFast, HarnessRun.ModelNamed("wingman-fast"));
        Assert.Throws<ArgumentException>(() => HarnessRun.ModelNamed("some-catalog-model"));
    }
}
