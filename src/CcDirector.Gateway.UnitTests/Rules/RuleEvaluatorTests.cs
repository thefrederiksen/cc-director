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

        /// <summary>Run just before the firings are read, which is INSIDE the free checks and therefore
        /// before the evaluator has recorded that it has looked at this screen. That is the exact window an
        /// overlapping pass arrives in, so it is where a test has to hold one pass to make the overlap
        /// deterministic rather than hoping for a race.</summary>
        public Action? BeforeReadingFirings { get; set; }

        /// <summary>Whether a firing this environment is told about becomes visible to the free checks, the
        /// way a real store's does. Off by default so the tests written before it keep their exact shape;
        /// on, it is what makes the cooldown and the daily cap real in a test rather than assumed.</summary>
        public bool FiringsAreVisibleToTheFreeChecks { get; set; }

        public IReadOnlyList<SessionRuleFiring> FiringsFor(TenantId tenant, Guid ruleId)
        {
            BeforeReadingFirings?.Invoke();
            return StoredFirings.Where(f => f.RuleId == ruleId).ToList();
        }

        /// <summary>The repository the session is working in. Settable so a test can give it a string that
        /// could not appear by accident and then look for it where it must not be.</summary>
        public string RepositoryPath { get; set; } = @"D:\ReposFred\scratch";

        public RuleSessionFacts? ReadSessionFacts(TenantId tenant, string sessionId) =>
            new(sessionId, "RawCli", RepositoryPath, "SOREN_NORTH", "Session Rules", ActivityState);

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
            lock (Typed) Typed.Add(text);
            return Task.FromResult(SendResult);
        }

        public void RecordFiring(TenantId tenant, RuleFiringDraft draft)
        {
            lock (Recorded) Recorded.Add(draft);
            if (!FiringsAreVisibleToTheFreeChecks) return;
            lock (StoredFirings)
                StoredFirings.Add(new SessionRuleFiring(
                    Guid.NewGuid(), draft.RuleId, draft.SessionId, NowUtc, draft.ScreenText,
                    draft.Understanding, draft.Decision, draft.Reason, draft.Runs, draft.TypedText,
                    draft.Outcome, draft.Grounding));
        }
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
            state == RuleState.Live ? "device-9f2c" : "",
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

    // ---- ruling A11: what decides WHETHER a rule applies is the screen ------------------------------

    [Fact]
    public async Task The_question_the_agent_is_asked_carries_the_screen_and_the_instruction_and_not_the_machine_state()
    {
        // RULING A11, MADE TESTABLE. The owner ruled that the terminal screen is the only input, and
        // ruling 15 ships checks that take a clock and the session's repository root as ARGUMENTS - so the
        // two are not in conflict, but nothing separated them except a convention, and a convention is not
        // a bound. This is the separation, enforced: the values that would let the agent decide whether an
        // instruction applies from something other than the screen are NOT in the question it is asked.
        //
        // The check the agent may ask for can still be handed those values when it RUNS - that is what
        // RuleRuntime is for, and it happens after the decision. What cannot happen is the decision being
        // made from them.
        var rule = Rule();
        var env = EnvironmentWith(rule);
        env.RepositoryPath = @"D:\ReposFred\zz-distinctive-repository-name";
        env.NowUtc = new DateTime(2031, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        env.AgentReply = DeclineReply(rule.Id, "the screen is not what the instruction is about.");

        await Run(env);

        var prompt = env.LastPrompt;
        Assert.NotNull(prompt);

        // A PRESENCE first, so the assertions below cannot pass over an empty or truncated question: the
        // screen and the account's own sentence must both really be in it.
        Assert.Contains("reached your", prompt!, StringComparison.Ordinal);
        Assert.Contains(TheSentence, prompt, StringComparison.Ordinal);

        // And then what must NOT be: the machine state the owner deferred.
        Assert.DoesNotContain("zz-distinctive-repository-name", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2031", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("SOREN_NORTH", prompt, StringComparison.Ordinal);
    }

    // ---- ruling A12: an act's reason has to be grounded in the screen it was given ------------------

    /// <summary>An ACT reply whose reason quotes something that is not on the screen.</summary>
    private static string UngroundedActReply(Guid ruleId) => $$"""
        {
          "rule_id": "{{ruleId}}",
          "understanding": "The session is blocked on its model allowance.",
          "decision": "act",
          "reason": "the screen says 'YOUR SUBSCRIPTION HAS BEEN CANCELLED', so the allowance is gone.",
          "checks": [ ],
          "type": "/usage-credits"
        }
        """;

    [Fact]
    public async Task An_act_whose_reason_quotes_text_the_screen_does_not_contain_is_refused_and_types_nothing()
    {
        // THE BOUND RULING A12 ASKS FOR. A rule that acts on evidence that was not there is the same
        // unfaithfulness that produced a decline quoting a screen from twelve minutes earlier, pointed in
        // the direction that does something.
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = UngroundedActReply(rule.Id);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Ungrounded, pass.What);
        Assert.Empty(env.Typed);

        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Refused, firing.Decision);
        Assert.Equal("", firing.TypedText);
        Assert.Contains("YOUR SUBSCRIPTION HAS BEEN CANCELLED", firing.Grounding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_decline_whose_reason_quotes_text_the_screen_does_not_contain_is_recorded_with_the_mismatch_noted()
    {
        // Declining is the direction that does nothing, so the decline stands - but the record has to show
        // the unfaithfulness rather than smooth it over.
        var rule = Rule();
        var env = EnvironmentWith(rule);
        env.AgentReply = DeclineReply(rule.Id,
            "the echo output explicitly says 'THE SCREEN HAS MOVED ON WHILE THE RULE WAS THINKING'.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Declined, pass.What);
        Assert.Empty(env.Typed);

        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Decline, firing.Decision);
        Assert.Contains("THE SCREEN HAS MOVED ON", firing.Grounding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_firing_says_what_the_grounding_check_found_even_when_there_was_nothing_to_check()
    {
        // THE PRESENCE. A run in which the grounding check never executed must not look identical to one
        // in which it ran and found nothing wrong, so the statement is never blank on any firing.
        var rule = Rule();
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.DryRun, pass.What);
        var firing = Assert.Single(env.Recorded);
        Assert.NotEqual("", firing.Grounding);
        Assert.Contains("grounding:", firing.Grounding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_says_the_grounding_check_did_not_apply_rather_than_saying_nothing()
    {
        // The reason on a refusal is the Gateway's own words, not the agent's, so there is nothing of the
        // agent's to check. Saying that is not the same as leaving it blank.
        var env = EnvironmentWith(Rule());
        env.AgentReply = "I am not going to answer in JSON.";

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Refused, pass.What);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleReasonGrounding.NotTheAgentsReason, firing.Grounding);
    }

    [Fact]
    public async Task An_act_whose_reason_quotes_the_screen_faithfully_still_acts()
    {
        // The PRESENCE half of the bound. A grounding check that refused everything would pass the tests
        // above while making the feature impossible, and they could not tell the difference.
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = $$"""
        {
          "rule_id": "{{rule.Id}}",
          "understanding": "The session is blocked on its model allowance.",
          "decision": "act",
          "reason": "the screen says 'reached your Fable 5 limit', which is the session's own state.",
          "checks": [ ],
          "type": "/usage-credits"
        }
        """;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Acted, pass.What);
        Assert.Equal("/usage-credits", Assert.Single(env.Typed));
    }

    // ---- two passes on one session ------------------------------------------------------------------

    /// <summary>
    /// TWO OVERLAPPING PASSES ON ONE SESSION MUST NOT BOTH ACT, and this is the shape that let them.
    ///
    /// Every turn-end signal starts its own pass, and the free checks read a rule's prior firings BEFORE the
    /// agent call and before anything is typed. Two passes that overlap in that window both see no act yet,
    /// and both go on to act - past the cooldown and past the daily cap, because both were counted against a
    /// state that neither of them had written to yet. The independent inspection of landing B proved exactly
    /// that with a synchronised probe: two evaluations, two sends, two firing records.
    ///
    /// A CEILING A RACE CAN WALK THROUGH IS NOT A CEILING, and an agent in a loop is the worst tail risk
    /// this feature has. So the test holds the first pass open in the window the inspection used - inside the
    /// firings read - runs a second pass to completion while it is held, and requires that exactly one send
    /// and exactly one act reach the world.
    /// </summary>
    [Fact]
    public async Task Two_passes_that_overlap_on_one_session_act_once_and_the_second_says_why()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.FiringsAreVisibleToTheFreeChecks = true;
        env.AgentReply = ActReply(rule.Id);

        // One evaluator, because production holds one: the Gateway arms a single evaluator and every
        // turn-end signal calls it. Two evaluator objects would be a different question than the one asked.
        var evaluator = new RuleEvaluator(env);

        var firstPassIsInTheWindow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letTheFirstPassGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = 0;
        env.BeforeReadingFirings = () =>
        {
            // ONLY the first reader is held. A gate that held every reader would deadlock the fixed code as
            // surely as it would hold the broken code, and a test that cannot finish proves nothing.
            if (Interlocked.Increment(ref readers) != 1) return;
            firstPassIsInTheWindow.SetResult();
            letTheFirstPassGo.Task.Wait(TimeSpan.FromSeconds(30));
        };

        var first = Task.Run(() => evaluator.EvaluateAsync(Tenant, DirectorId, SessionId, CancellationToken.None));
        await firstPassIsInTheWindow.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await evaluator.EvaluateAsync(Tenant, DirectorId, SessionId, CancellationToken.None);

        letTheFirstPassGo.SetResult();
        var firstPass = await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RulePassOutcomes.Acted, firstPass.What);
        Assert.Equal(RulePassOutcomes.AlreadyEvaluating, second.What);
        Assert.Equal("/usage-credits", Assert.Single(env.Typed));
        Assert.Single(env.Recorded, r => r.Decision == RuleDecisions.Act);
        Assert.Empty(second.Recorded);
    }

    private static string ScreenTextOf(RuleFiringDraft draft) =>
        draft.ScreenText.Split('\n').Select(l => l.TrimEnd()).Last(l => l.Contains("reached your"));
}
