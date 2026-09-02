using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE EVALUATOR (Session Rules mission, phase 2): the thin vertical slice from a session going idle to
/// something being typed into it, with every step recorded.
///
/// The acceptance rows proved here, each as a PRESENCE:
///
///   * a DECLINE is a RECORDED FIRING, not silence. A rule that did nothing because the evaluator threw
///     looks exactly like a rule that declined, so the decline is proved by the presence of its record
///     with its reason, never by the absence of a keystroke.
///   * DRY RUN TYPES NOTHING, proved by an INSTRUMENTED SEND SEAM counted at zero - the fake environment
///     counts every call to the one method that can type - together with the recorded firing that says
///     what it WOULD have typed. The count alone would pass just as happily if the evaluator had crashed
///     before reaching the send, which is why the record is asserted alongside it.
///   * THE SCREEN IS RE-READ IMMEDIATELY BEFORE ACTING, and a screen that changed between the decision and
///     the keystroke is abandoned with the abandonment recorded.
///   * a reply that names something it was not offered is RECORDED AS A REFUSAL, not swallowed.
///   * the free checks cost nothing: a working session never reaches the model, and the pass says why.
/// </summary>
public sealed class RuleEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId Tenant = TenantId.Local;
    private const string DirectorId = "director-1";
    private const string SessionId = "sid-1";

    private const string TheNotice =
        "You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.";

    private static readonly string[] BlockedScreen =
    {
        "C:\\scratch>echo " + TheNotice,
        TheNotice,
        "C:\\scratch>",
    };

    private static readonly string TheSentence =
        "If a session's screen says it has run out of its model allowance, type the command that shows me what is left.";

    // ---- the fake environment ----------------------------------------------------------------------

    /// <summary>
    /// Everything the evaluator touches, with the two things that matter INSTRUMENTED: how many times the
    /// model was asked, and how many times something was typed into a session. The send counter is the seam
    /// the "dry run types nothing" row is proved on - there is exactly one method here that can type, so a
    /// count of zero is a count of the real thing rather than a grep over a log.
    /// </summary>
    private sealed class FakeRuleEnvironment : IRuleEnvironment
    {
        public List<SessionRule> StoredRules { get; } = new();
        public List<SessionRuleFiring> StoredFirings { get; } = new();
        public List<RuleFiringDraft> Recorded { get; } = new();

        /// <summary>The screens the session shows, in the order they are read. The last one is repeated
        /// once the list runs out, so a test that wants the screen to CHANGE between the decision and the
        /// keystroke supplies two, and a test that wants it stable supplies one.</summary>
        public List<IReadOnlyList<string>?> Screens { get; } = new();
        private int _screenReads;

        public string ActivityState { get; set; } = "WaitingForInput";
        public string? AgentReply { get; set; }

        public int ScreenReads => _screenReads;
        public int AgentCalls { get; private set; }
        public string? LastPrompt { get; private set; }

        /// <summary>THE SEND SEAM. Every keystroke this feature can produce goes through here.</summary>
        public List<string> Typed { get; } = new();
        public RuleSendResult SendResult { get; set; } = RuleSendResult.Confirmed();

        public DateTime NowUtc { get; set; } = Now;

        public IReadOnlyList<SessionRule> Rules(TenantId tenant) => StoredRules;

        public IReadOnlyList<SessionRuleFiring> FiringsFor(TenantId tenant, Guid ruleId) =>
            StoredFirings.Where(f => f.RuleId == ruleId).ToList();

        public RuleSessionFacts? ReadSessionFacts(TenantId tenant, string sessionId) =>
            new(sessionId, "RawCli", @"D:\ReposFred\scratch", "SOREN_NORTH", "Session Rules", ActivityState);

        public Task<IReadOnlyList<string>?> ReadScreenRowsAsync(
            TenantId tenant, string directorId, string sessionId, CancellationToken ct)
        {
            var index = Math.Min(_screenReads, Screens.Count - 1);
            _screenReads++;
            return Task.FromResult(Screens.Count == 0 ? null : Screens[index]);
        }

        public Task<string?> AskAgentAsync(TenantId tenant, string prompt, CancellationToken ct)
        {
            AgentCalls++;
            LastPrompt = prompt;
            return Task.FromResult(AgentReply);
        }

        public Task<RuleSendResult> TypeIntoSessionAsync(
            TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct)
        {
            Typed.Add(text);
            return Task.FromResult(SendResult);
        }

        public void RecordFiring(TenantId tenant, RuleFiringDraft draft) => Recorded.Add(draft);
    }

    private static SessionRule Rule(
        RuleState state = RuleState.DryRun,
        IReadOnlyList<string>? triggerWords = null,
        Guid? id = null) => new(
            id ?? Guid.NewGuid(),
            TheSentence,
            "A session stopped on a provider allowance notice.",
            triggerWords ?? new[] { "reached your", "limit" },
            Array.Empty<RulePrimitiveCall>(),
            RuleScope.AllSessions,
            300,
            5,
            state,
            Now,
            Now);

    private static FakeRuleEnvironment EnvironmentWith(SessionRule rule, params IReadOnlyList<string>[] screens)
    {
        var env = new FakeRuleEnvironment();
        env.StoredRules.Add(rule);
        foreach (var screen in screens.Length == 0 ? new[] { BlockedScreen } : screens)
            env.Screens.Add(screen);
        return env;
    }

    /// <summary>The check the agent names by default: the words really are on this screen.</summary>
    private const string TheWordsAreThere =
        "{ \"name\": \"matches_any\", \"arguments\": { \"text\": \"<screen_text>\", \"terms\": [\"reached your\"] } }";

    private static string ActReply(Guid ruleId, string type = "/usage-credits", string? checks = null)
    {
        var theChecks = checks ?? TheWordsAreThere;
        return $$"""
        {
          "rule_id": "{{ruleId}}",
          "understanding": "The session itself is blocked on its model allowance and cannot run a turn.",
          "decision": "act",
          "reason": "The notice is the session's own state, not a discussion of one.",
          "checks": [ {{theChecks}} ],
          "type": "{{type}}"
        }
        """;
    }

    private static string DeclineReply(Guid ruleId, string reason) => $$"""
        {
          "rule_id": "{{ruleId}}",
          "understanding": "The screen shows the notice being talked about, not the session reporting its own state.",
          "decision": "decline",
          "reason": "{{reason}}"
        }
        """;

    private static Task<RulePass> Run(FakeRuleEnvironment env) =>
        new RuleEvaluator(env).EvaluateAsync(Tenant, DirectorId, SessionId, CancellationToken.None);

    // ---- the free checks cost nothing ---------------------------------------------------------------

    [Fact]
    public async Task A_working_session_never_reaches_the_model_and_the_pass_says_why()
    {
        var env = EnvironmentWith(Rule());
        env.ActivityState = "Working";

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.StoppedBeforeAnyRule, pass.What);
        Assert.Equal(RuleCandidateFilter.SessionIsWorking, pass.Detail);
        Assert.Equal(0, env.AgentCalls);
        Assert.Empty(env.Typed);
    }

    [Fact]
    public async Task A_screen_without_any_rules_words_never_reaches_the_model_and_every_rule_has_a_reason()
    {
        var env = EnvironmentWith(Rule(triggerWords: new[] { "no such words anywhere" }));

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.NoCandidates, pass.What);
        Assert.Contains("none of the words this rule watches for", pass.Detail);
        Assert.Equal(0, env.AgentCalls);
    }

    [Fact]
    public async Task An_unreadable_screen_is_not_evidence_and_stops_the_pass_with_a_reason()
    {
        var env = new FakeRuleEnvironment();
        env.StoredRules.Add(Rule());
        env.Screens.Add(null);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.ScreenUnreadable, pass.What);
        Assert.Equal(0, env.AgentCalls);
        Assert.Empty(env.Typed);
    }

    // ---- the decline is a RECORDED FIRING ------------------------------------------------------------

    [Fact]
    public async Task A_decline_is_written_down_with_its_reason_and_nothing_is_typed()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = DeclineReply(rule.Id,
            "This screen is a document quoting the notice, not a session reporting its own state.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Declined, pass.What);

        // The PRESENCE that proves it: a recorded firing saying it declined, and why.
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(rule.Id, firing.RuleId);
        Assert.Equal(RuleDecisions.Decline, firing.Decision);
        Assert.Contains("quoting the notice", firing.Reason);
        Assert.Equal("", firing.TypedText);
        Assert.Equal(TheNotice, ScreenTextOf(firing));
        Assert.Empty(env.Typed);
    }

    [Fact]
    public async Task A_declines_record_carries_what_the_agent_understood_as_well_as_what_it_decided()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = DeclineReply(rule.Id, "not this session's own state.");

        await Run(env);

        var firing = Assert.Single(env.Recorded);
        Assert.Contains("talked about", firing.Understanding);
    }

    // ---- dry run types nothing ----------------------------------------------------------------------

    [Fact]
    public async Task A_dry_run_rule_types_nothing_and_records_what_it_would_have_typed()
    {
        var rule = Rule(state: RuleState.DryRun);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id, "/usage-credits");

        var pass = await Run(env);

        // The instrumented send seam, counted at zero...
        Assert.Empty(env.Typed);
        // ...and the PRESENCE that says the evaluator really did get all the way there.
        Assert.Equal(RulePassOutcomes.DryRun, pass.What);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Act, firing.Decision);
        Assert.Equal("", firing.TypedText);
        Assert.Contains("dry run", firing.Outcome);
        Assert.Contains("/usage-credits", firing.Outcome);
    }

    // ---- a live rule types exactly the composed text -------------------------------------------------

    [Fact]
    public async Task A_live_rule_types_exactly_what_the_agent_composed_and_records_it()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id, "/usage-credits");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Acted, pass.What);
        Assert.Equal("/usage-credits", Assert.Single(env.Typed));
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Act, firing.Decision);
        Assert.Equal("/usage-credits", firing.TypedText);
        Assert.Contains("typed", firing.Outcome);
    }

    [Fact]
    public async Task The_checks_the_agent_named_are_run_and_their_answers_are_on_the_record()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);

        await Run(env);

        var firing = Assert.Single(env.Recorded);
        var run = Assert.Single(firing.Runs);
        Assert.Equal("matches_any", run.Name);
        Assert.Equal("text=<screen_text>, terms=reached your", run.Arguments);
        Assert.Equal("true", run.Answer);
    }

    [Fact]
    public async Task A_check_the_agent_staked_its_decision_on_that_answers_no_abandons_the_act()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id, "/usage-credits",
            checks: "{ \"name\": \"matches_any\", \"arguments\": { \"text\": \"<screen_text>\", \"terms\": [\"not on this screen at all\"] } }");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Abandoned, pass.What);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Abandoned, firing.Decision);
        Assert.Contains("matches_any", firing.Reason);
    }

    // ---- the screen is re-read immediately before acting ---------------------------------------------

    [Fact]
    public async Task The_screen_is_read_again_immediately_before_the_keystroke()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule, BlockedScreen, BlockedScreen);
        env.AgentReply = ActReply(rule.Id);

        await Run(env);

        Assert.Equal(2, env.ScreenReads);
        Assert.Single(env.Typed);
    }

    [Fact]
    public async Task A_screen_that_changed_between_the_decision_and_the_keystroke_is_abandoned_and_written_down()
    {
        var rule = Rule(state: RuleState.Live);
        var movedOn = new[] { "C:\\scratch>dir", "Volume in drive C has no label.", "C:\\scratch>" };
        var env = EnvironmentWith(rule, BlockedScreen, movedOn);
        env.AgentReply = ActReply(rule.Id);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Abandoned, pass.What);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Abandoned, firing.Decision);
        Assert.Contains("changed", firing.Reason);
        Assert.Equal("", firing.TypedText);
    }

    [Fact]
    public async Task A_screen_that_cannot_be_read_again_is_abandoned_rather_than_typed_into()
    {
        var rule = Rule(state: RuleState.Live);
        var env = new FakeRuleEnvironment();
        env.StoredRules.Add(rule);
        env.Screens.Add(BlockedScreen);
        env.Screens.Add(null);
        env.AgentReply = ActReply(rule.Id);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Abandoned, pass.What);
        Assert.Empty(env.Typed);
        Assert.Contains("could not be read", Assert.Single(env.Recorded).Reason);
    }

    // ---- a reply naming something it was not offered is RECORDED as a refusal -------------------------

    [Fact]
    public async Task A_reply_naming_a_rule_that_was_not_offered_is_recorded_as_a_refusal()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        var neverOffered = Guid.NewGuid();
        env.AgentReply = ActReply(neverOffered);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Refused, pass.What);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(rule.Id, firing.RuleId);     // recorded against the rule that WAS in play
        Assert.Equal(RuleDecisions.Refused, firing.Decision);
        Assert.Contains(neverOffered.ToString(), firing.Reason);
    }

    [Fact]
    public async Task A_reply_naming_a_check_the_product_does_not_ship_is_recorded_as_a_refusal()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id, "/usage-credits",
            checks: "{ \"name\": \"run_shell\", \"arguments\": { \"command\": \"whoami\" } }");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Refused, pass.What);
        Assert.Empty(env.Typed);
        Assert.Contains("run_shell", Assert.Single(env.Recorded).Reason);
    }

    [Fact]
    public async Task A_model_that_says_nothing_is_recorded_as_a_refusal_and_never_read_as_permission()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = null;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Refused, pass.What);
        Assert.Empty(env.Typed);
        Assert.Equal(RuleDecisions.Refused, Assert.Single(env.Recorded).Decision);
    }

    // ---- the record is the product ------------------------------------------------------------------

    [Fact]
    public async Task Every_firing_carries_the_screen_it_was_decided_on()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);

        await Run(env);

        Assert.Contains(TheNotice, Assert.Single(env.Recorded).ScreenText);
    }

    [Fact]
    public async Task A_send_that_never_left_the_gateway_is_recorded_as_nothing_typed()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.SendResult = RuleSendResult.NotSent("that machine is not connected.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.NotSent, pass.What);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal("", firing.TypedText);
        Assert.Contains("nothing was typed", firing.Outcome);
        Assert.Contains("not connected", firing.Outcome);
    }

    /// <summary>
    /// THE 502 TRAP, and it is a defect this feature already produced once on a real session. The
    /// prompt route answers "never started a turn ... parked in the composer unsubmitted" for a
    /// session whose turn is over in milliseconds - a shell - while the keystroke has in fact landed.
    /// Reading that as "the send did not land" put a sentence into the firing record that the
    /// session's own screen disproved. An unconfirmed send is therefore recorded as UNCONFIRMED,
    /// with the text kept as typed and the screen named as the evidence - never as a send that did
    /// not happen.
    /// </summary>
    [Fact]
    public async Task A_send_the_route_could_not_confirm_is_recorded_as_unconfirmed_not_as_nothing_typed()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.SendResult = RuleSendResult.NotConfirmed(
            "never started a turn within 8 beats: the agent produced under 2048 bytes.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.SendUnconfirmed, pass.What);
        Assert.Equal("/usage-credits", Assert.Single(env.Typed));
        var firing = Assert.Single(env.Recorded);
        Assert.Equal("/usage-credits", firing.TypedText);
        Assert.Contains("did not confirm", firing.Outcome);
        Assert.Contains("screen", firing.Outcome);
        Assert.DoesNotContain("did not land", firing.Outcome);
        Assert.DoesNotContain("never reached", firing.Outcome);
    }

    private static string ScreenTextOf(RuleFiringDraft draft) =>
        draft.ScreenText.Split('\n').Select(l => l.TrimEnd()).Last(l => l.Contains("reached your"));
}
