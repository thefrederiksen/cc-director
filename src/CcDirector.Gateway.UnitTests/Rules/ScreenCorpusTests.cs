using System.Text;
using CcDirector.Gateway.Rules;
using CcDirector.Rules.ScreenHarness;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE CORPUS TESTS (Session Rules mission, phase 0). They read the REAL corpus beside the screen harness -
/// found by walking up from the test output to the directory that holds <c>cc-director.sln</c> - and they
/// keep it honest: enough cases, enough negatives, the three named negative kinds present, every case
/// explained and sourced, and - the part that makes a negative a negative - every case one the free checks
/// would actually put in front of the model.
///
/// THEY ARE RED ON AN EMPTY OR PARTIAL CORPUS, ON PURPOSE. A corpus test that passed on no corpus would
/// certify a run that never happened. So every per-case check first asserts there is at least one case, and
/// the count check demands twenty.
///
/// The per-case checks are written as functions that answer WHY a case fails, and each one is run against a
/// case built to fail it - under a temporary directory, never under the Manager's corpus - so a check that
/// had quietly stopped checking would be caught here rather than certifying the corpus.
/// </summary>
public sealed class ScreenCorpusTests
{
    private static readonly Lazy<IReadOnlyList<SessionRule>> Rules = new(
        () => ScreenCorpus.ReadRules(RepositoryRoot.DefaultCorpus()), isThreadSafe: true);

    private static readonly Lazy<IReadOnlyList<ScreenCase>> Cases = new(
        () => ScreenCorpus.ReadCases(RepositoryRoot.DefaultCorpus()), isThreadSafe: true);

    private static IReadOnlyList<ScreenCase> TheCases()
    {
        var cases = Cases.Value;
        Assert.True(cases.Count > 0, "the corpus at " + RepositoryRoot.DefaultCorpus() + " has no cases, and an empty corpus proves nothing.");
        return cases;
    }

    // ---- the checks, each answering why a case fails or null when it passes ------------------------------

    /// <summary>The case's written-down side is complete and consistent with its kind.</summary>
    internal static string? WhyTheRecordIsIncomplete(ScreenCase screenCase)
    {
        var record = screenCase.Record;
        var directoryName = Path.GetFileName(screenCase.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.Equals(record.Id, directoryName, StringComparison.Ordinal))
            return "its id '" + record.Id + "' is not its directory name '" + directoryName + "'";
        if (!CaseKinds.All.Contains(record.Kind, StringComparer.Ordinal))
            return "its kind '" + record.Kind + "' is not one of: " + string.Join(", ", CaseKinds.All);
        if (record.Expected != CaseExpectations.Act && record.Expected != CaseExpectations.Decline)
            return "its expected answer '" + record.Expected + "' is neither act nor decline";
        if (CaseKinds.ExpectationOf(record.Kind) != record.Expected)
            return "its kind '" + record.Kind + "' expects " + CaseKinds.ExpectationOf(record.Kind) + " but it says " + record.Expected;
        if (string.IsNullOrWhiteSpace(record.Reason))
            return "it has no reason - a corpus whose answers are unexplained is a corpus nobody can argue with";
        if (string.IsNullOrWhiteSpace(record.Source.Method))
            return "it has no source.method";
        if (string.IsNullOrWhiteSpace(record.Source.SessionId))
            return "it has no source.sessionId";
        if (!screenCase.ScreenRows.Any(r => !string.IsNullOrWhiteSpace(r)))
            return "its screen.txt has no non-blank line";
        return null;
    }

    /// <summary>An act case names a rule that exists.</summary>
    internal static string? WhyTheExpectedRuleIsMissing(ScreenCase screenCase, IReadOnlyList<SessionRule> rules)
    {
        if (!string.Equals(screenCase.Record.Expected, CaseExpectations.Act, StringComparison.Ordinal)) return null;
        if (!Guid.TryParse(screenCase.Record.ExpectedRuleId, out var id))
            return "it expects act but names no guid expectedRuleId (got '" + screenCase.Record.ExpectedRuleId + "')";
        if (!rules.Any(r => r.Id == id))
            return "it expects act from rule " + id + ", which is not in rules.json";
        return null;
    }

    /// <summary>The free checks, run exactly as the evaluator runs them, choose at least one rule.</summary>
    internal static string? WhyTheFreeChecksSkipIt(ScreenCase screenCase, IReadOnlyList<SessionRule> rules)
    {
        var candidates = ChooseAsTheEvaluatorDoes(screenCase, rules);
        if (candidates.StoppedBecause is not null)
            return "the free checks stopped before any rule: " + candidates.StoppedBecause;
        if (candidates.Chosen.Count == 0)
            return "the free checks chose no rule, so the model would never see it: " +
                   string.Join(" ", candidates.Skipped.Select(s => s.Reason));
        return null;
    }

    /// <summary>The trigger words of a chosen rule are inside the tail the question actually carries.
    /// The engine shows the model only the last <see cref="RuleAgentContract.ScreenTailLines"/> non-blank
    /// lines, so trigger words above that window are words the model never sees - the free checks would
    /// ask, and the model would be judged on a screen that does not show what the case is about.</summary>
    internal static string? WhyTheTriggerWordsAreAboveTheTail(ScreenCase screenCase, IReadOnlyList<SessionRule> rules)
    {
        var candidates = ChooseAsTheEvaluatorDoes(screenCase, rules);
        if (candidates.Chosen.Count == 0) return null; // reported by the free-checks test, not this one
        var tail = ScreenCorpus.TailAsTheContractDoes(screenCase.ScreenRows, RuleAgentContract.ScreenTailLines);
        var tailText = string.Join("\n", tail);
        if (candidates.Chosen.Any(r => RulePrimitives.MatchesAny(tailText, r.TriggerWords)))
            return null;
        return "no chosen rule's trigger words appear in the last " + RuleAgentContract.ScreenTailLines +
               " non-blank lines, which is all the model is shown; the words sit above that window";
    }

    /// <summary>The nonAscii flag says what the bytes say.</summary>
    internal static string? WhyTheAsciiFlagIsWrong(ScreenCase screenCase)
    {
        var pure = ScreenCorpus.IsPureAscii(screenCase.ScreenBytes);
        if (screenCase.Record.NonAscii && pure)
            return "it says nonAscii: true but every byte of its screen.txt is ASCII";
        if (!screenCase.Record.NonAscii && !pure)
            return "it says nonAscii: false but its screen.txt contains a non-ASCII byte";
        return null;
    }

    private static RuleCandidates ChooseAsTheEvaluatorDoes(ScreenCase screenCase, IReadOnlyList<SessionRule> rules) =>
        RuleCandidateFilter.Choose(
            rules,
            screenCase.SessionFacts(),
            ScreenCorpus.JoinAsTheEvaluatorDoes(screenCase.ScreenRows),
            previousScreenText: null,
            firingsFor: _ => Array.Empty<SessionRuleFiring>(),
            nowUtc: DateTime.UtcNow);

    private static void AssertNone(IEnumerable<(string Id, string? Why)> problems, string what)
    {
        var failing = problems.Where(p => p.Why is not null).Select(p => "  " + p.Id + ": " + p.Why).ToList();
        Assert.True(failing.Count == 0, failing.Count + " case(s) " + what + ":\n" + string.Join("\n", failing));
    }

    // ---- the corpus -------------------------------------------------------------------------------------

    [Fact]
    public void The_corpus_has_at_least_20_cases_and_at_least_half_of_them_decline()
    {
        var cases = TheCases();
        var declines = cases.Count(c => c.ExpectsDecline);
        Assert.True(cases.Count >= 20, "the corpus has " + cases.Count + " case(s); it needs at least 20.");
        Assert.True(declines * 2 >= cases.Count,
            "the corpus has " + declines + " decline case(s) out of " + cases.Count + "; at least half must decline.");
    }

    [Fact]
    public void The_three_named_negative_kinds_are_each_present()
    {
        var kinds = TheCases().Select(c => c.Record.Kind).ToHashSet(StringComparer.Ordinal);
        var missing = CaseKinds.RequiredNegatives.Where(k => !kinds.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "the corpus has no case of kind: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_case_is_complete_and_its_expected_answer_matches_its_kind() =>
        AssertNone(TheCases().Select(c => (c.Id, WhyTheRecordIsIncomplete(c))), "are incomplete or inconsistent");

    [Fact]
    public void Every_act_case_names_a_rule_that_exists() =>
        AssertNone(TheCases().Select(c => (c.Id, WhyTheExpectedRuleIsMissing(c, Rules.Value))), "expect act from a rule that does not exist");

    [Fact]
    public void The_free_checks_put_every_case_in_front_of_the_model() =>
        AssertNone(TheCases().Select(c => (c.Id, WhyTheFreeChecksSkipIt(c, Rules.Value))),
            "would be skipped by the free checks, so they prove nothing about the model");

    [Fact]
    public void The_trigger_words_of_every_case_sit_inside_the_tail_the_model_is_shown() =>
        AssertNone(TheCases().Select(c => (c.Id, WhyTheTriggerWordsAreAboveTheTail(c, Rules.Value))),
            "have their trigger words above the " + RuleAgentContract.ScreenTailLines + "-line tail the model is shown");

    [Fact]
    public void The_non_ascii_flag_on_every_case_is_a_fact_about_its_bytes() =>
        AssertNone(TheCases().Select(c => (c.Id, WhyTheAsciiFlagIsWrong(c))), "have a nonAscii flag that is not true of their bytes");

    [Fact]
    public void Every_corpus_rule_is_one_the_write_time_validator_accepts()
    {
        var rules = Rules.Value;
        Assert.True(rules.Count > 0, "rules.json holds no rules, and a corpus judged against no rules proves nothing.");
        var refused = rules
            .Select(r => (r.Id, Validation: RuleCallValidator.ValidateAll(r.Calls, RulePrimitiveRegistry.Default)))
            .Where(r => !r.Validation.IsValid)
            .Select(r => "  " + r.Id + ": " + r.Validation.Reason)
            .ToList();
        Assert.True(refused.Count == 0, "rule(s) the validator refuses:\n" + string.Join("\n", refused));
        Assert.All(rules, r =>
        {
            Assert.Equal(RuleState.DryRun, r.State);
            Assert.Equal(RuleScope.AllSessions, r.Scope);
            Assert.Equal("", r.PromotedBy);
        });
    }

    /// <summary>A corpus rule with no text to type would be refused by the engine before any model is
    /// asked (phase 1), so every case it was chosen for would read as "not asked" and prove nothing about
    /// the model. Every corpus rule says what it types, as every stored rule must.</summary>
    [Fact]
    public void Every_corpus_rule_says_what_it_types()
    {
        var rules = Rules.Value;
        Assert.True(rules.Count > 0, "rules.json holds no rules, and a corpus judged against no rules proves nothing.");
        var silent = rules.Where(r => string.IsNullOrWhiteSpace(r.TextToType)).Select(r => "  " + r.Id).ToList();
        Assert.True(silent.Count == 0,
            "rule(s) with no text to type, which the engine would refuse before asking any model: " + string.Join(", ", silent));
    }

    // ---- the checks, proven on known-bad cases under a temporary directory --------------------------------

    private static readonly Guid TheRule = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly IReadOnlyList<SessionRule> TemporaryRules = new[]
    {
        new SessionRule(TheRule, "when a session hits its usage limit, switch model", "a usage limit notice", "/model opus",
            new[] { "reached your", "usage limit" }, Array.Empty<RulePrimitiveCall>(), RuleScope.AllSessions,
            60, 5, RuleState.DryRun, "", ScreenCorpus.RuleStampUtc, ScreenCorpus.RuleStampUtc),
    };

    /// <summary>A throwaway corpus with one case, written the way the Manager writes them, then read back
    /// through the real reader. Deleted when the test is done.</summary>
    private sealed class TemporaryCorpus : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "screen-corpus-" + Guid.NewGuid().ToString("N"));

        public ScreenCase Write(string id, string expected, string kind, byte[] screen, bool nonAscii = false,
            string reason = "because the words are the session's own state", string activityState = "WaitingForInput",
            string? expectedRuleId = null)
        {
            var caseDirectory = Path.Combine(Directory, ScreenCorpus.CasesDirectoryName, id);
            System.IO.Directory.CreateDirectory(caseDirectory);
            var ruleLine = expectedRuleId is null ? "" : "  \"expectedRuleId\": \"" + expectedRuleId + "\",\n";
            File.WriteAllText(Path.Combine(caseDirectory, ScreenCorpus.CaseFileName),
                "{\n  \"id\": \"" + id + "\",\n  \"expected\": \"" + expected + "\",\n" + ruleLine +
                "  \"kind\": \"" + kind + "\",\n  \"reason\": \"" + reason + "\",\n" +
                "  \"facts\": { \"agent\": \"Shell\", \"repositoryPath\": \"\", \"machine\": \"M\", \"mission\": \"\", \"activityState\": \"" + activityState + "\" },\n" +
                "  \"factsNote\": \"made up for the test\",\n" +
                "  \"source\": { \"method\": \"test\", \"sessionId\": \"test-session\", \"capturedUtc\": \"2026-09-03T00:00:00Z\", \"detail\": \"\" },\n" +
                "  \"nonAscii\": " + (nonAscii ? "true" : "false") + ",\n  \"secretsChecked\": true\n}\n");
            File.WriteAllBytes(Path.Combine(caseDirectory, ScreenCorpus.ScreenFileName), screen);
            return ScreenCorpus.ReadCase(caseDirectory);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    [Fact]
    public void A_screen_is_read_as_captured_with_its_line_endings_and_its_non_ascii_bytes_kept()
    {
        using var corpus = new TemporaryCorpus();
        var screen = Encoding.UTF8.GetBytes("first row  \r\nYou've reached your usage limit \u2500 box\r\n\r\nlast row\r\n");
        var read = corpus.Write("kept-as-captured", CaseExpectations.Act, CaseKinds.Positive, screen, nonAscii: true,
            expectedRuleId: TheRule.ToString());

        Assert.Equal(screen, read.ScreenBytes);
        Assert.Equal(new[] { "first row  ", "You've reached your usage limit \u2500 box", "", "last row", "" }, read.ScreenRows);
        Assert.Null(WhyTheRecordIsIncomplete(read));
        Assert.Null(WhyTheExpectedRuleIsMissing(read, TemporaryRules));
        Assert.Null(WhyTheFreeChecksSkipIt(read, TemporaryRules));
        Assert.Null(WhyTheTriggerWordsAreAboveTheTail(read, TemporaryRules));
        Assert.Null(WhyTheAsciiFlagIsWrong(read));
    }

    [Fact]
    public void The_free_checks_test_fails_a_case_whose_trigger_words_are_not_on_the_screen()
    {
        using var corpus = new TemporaryCorpus();
        var read = corpus.Write("no-words", CaseExpectations.Decline, CaseKinds.NegativeCode,
            Ascii("a screen about something else entirely\n"));

        var why = WhyTheFreeChecksSkipIt(read, TemporaryRules);
        Assert.NotNull(why);
        Assert.Contains("chose no rule", why);
    }

    [Fact]
    public void The_free_checks_test_fails_a_case_whose_session_is_working()
    {
        using var corpus = new TemporaryCorpus();
        var read = corpus.Write("working", CaseExpectations.Decline, CaseKinds.NegativeReport,
            Ascii("You've reached your usage limit\n"), activityState: RuleCandidateFilter.WorkingState);

        var why = WhyTheFreeChecksSkipIt(read, TemporaryRules);
        Assert.NotNull(why);
        Assert.Contains(RuleCandidateFilter.SessionIsWorking, why);
    }

    [Fact]
    public void The_tail_test_fails_a_case_whose_trigger_words_sit_above_the_forty_line_window()
    {
        using var corpus = new TemporaryCorpus();
        var sb = new StringBuilder("You've reached your usage limit\n");
        for (var i = 0; i < RuleAgentContract.ScreenTailLines; i++) sb.Append("filler line ").Append(i).Append('\n');
        var read = corpus.Write("words-above-tail", CaseExpectations.Decline, CaseKinds.NegativeDocumentation, Ascii(sb.ToString()));

        // The free checks DO choose a rule - the words are on the screen - which is exactly why this needs
        // its own test: the model would be asked, and shown a screen without the words.
        Assert.Null(WhyTheFreeChecksSkipIt(read, TemporaryRules));
        var why = WhyTheTriggerWordsAreAboveTheTail(read, TemporaryRules);
        Assert.NotNull(why);
        Assert.Contains("above that window", why);
    }

    [Fact]
    public void The_ascii_test_fails_a_flag_in_either_wrong_direction()
    {
        using var corpus = new TemporaryCorpus();
        var claimsNonAsciiButIsNot = corpus.Write("claims-non-ascii", CaseExpectations.Decline, CaseKinds.NegativeSubstring,
            Ascii("reached your limit\n"), nonAscii: true);
        var claimsAsciiButIsNot = corpus.Write("claims-ascii", CaseExpectations.Decline, CaseKinds.NegativeSubstring,
            Encoding.UTF8.GetBytes("reached your limit \u2500\n"), nonAscii: false);

        Assert.Contains("every byte", WhyTheAsciiFlagIsWrong(claimsNonAsciiButIsNot));
        Assert.Contains("non-ASCII byte", WhyTheAsciiFlagIsWrong(claimsAsciiButIsNot));
    }

    [Fact]
    public void The_record_test_fails_a_case_whose_kind_and_expected_answer_disagree_or_that_lacks_a_reason()
    {
        using var corpus = new TemporaryCorpus();
        var disagree = corpus.Write("disagree", CaseExpectations.Act, CaseKinds.NegativeCode, Ascii("reached your limit\n"));
        var noReason = corpus.Write("no-reason", CaseExpectations.Decline, CaseKinds.NegativeCode, Ascii("reached your limit\n"), reason: "");
        var unknownKind = corpus.Write("unknown-kind", CaseExpectations.Decline, "negative-made-up", Ascii("reached your limit\n"));

        Assert.Contains("expects decline but it says act", WhyTheRecordIsIncomplete(disagree));
        Assert.Contains("no reason", WhyTheRecordIsIncomplete(noReason));
        Assert.Contains("not one of", WhyTheRecordIsIncomplete(unknownKind));
    }

    [Fact]
    public void The_rule_test_fails_an_act_case_that_names_a_rule_which_does_not_exist()
    {
        using var corpus = new TemporaryCorpus();
        var unknownRule = corpus.Write("unknown-rule", CaseExpectations.Act, CaseKinds.Positive,
            Ascii("reached your limit\n"), expectedRuleId: Guid.NewGuid().ToString());
        var noRule = corpus.Write("no-rule", CaseExpectations.Act, CaseKinds.Positive, Ascii("reached your limit\n"));

        Assert.Contains("not in rules.json", WhyTheExpectedRuleIsMissing(unknownRule, TemporaryRules));
        Assert.Contains("names no guid", WhyTheExpectedRuleIsMissing(noRule, TemporaryRules));
    }

    [Fact]
    public void Rules_json_is_read_into_dry_run_rules_over_all_sessions_with_their_calls()
    {
        using var corpus = new TemporaryCorpus();
        Directory.CreateDirectory(corpus.Directory);
        File.WriteAllText(Path.Combine(corpus.Directory, ScreenCorpus.RulesFileName),
            "[\n  {\n    \"id\": \"" + TheRule + "\",\n    \"instruction\": \"switch model when the limit is hit\",\n" +
            "    \"screenDescription\": \"a usage limit notice\",\n    \"triggerWords\": [\"reached your\", \"limit\"],\n" +
            "    \"calls\": [ { \"name\": \"matches_any\", \"arguments\": { \"text\": \"<screen_text>\", \"terms\": [\"limit\"] } } ],\n" +
            "    \"cooldownSeconds\": 60,\n    \"dailyCap\": 5\n  }\n]\n");

        var rules = ScreenCorpus.ReadRules(corpus.Directory);

        var rule = Assert.Single(rules);
        Assert.Equal(TheRule, rule.Id);
        Assert.Equal(RuleState.DryRun, rule.State);
        Assert.Equal(RuleScope.AllSessions, rule.Scope);
        Assert.Equal("", rule.PromotedBy);
        Assert.Equal(new[] { "reached your", "limit" }, rule.TriggerWords);
        Assert.Equal(60, rule.CooldownSeconds);
        Assert.Equal(5, rule.DailyCap);
        var call = Assert.Single(rule.Calls);
        Assert.Equal("matches_any", call.Name);
        Assert.True(RuleCallValidator.ValidateAll(rule.Calls, RulePrimitiveRegistry.Default).IsValid);
    }
}
