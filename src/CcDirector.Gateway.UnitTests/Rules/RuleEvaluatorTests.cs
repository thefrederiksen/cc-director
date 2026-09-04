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

        /// <summary>The activity states the session reports, in the order they are asked for; the last one
        /// repeats once the list runs out. Empty means <see cref="ActivityState"/> every time. This is how a
        /// test makes the session START WORKING during the agent call - the gap the evaluator's decision is
        /// made across - while leaving the visible rows exactly as they were.</summary>
        public List<string> ActivityStatesInOrder { get; } = new();
        private int _factReads;
        public string? AgentReply { get; set; }

        public int ScreenReads => _screenReads;
        public int AgentCalls { get; private set; }
        public string? LastPrompt { get; private set; }

        /// <summary>How many times the SECOND question was asked - whose state the cited line reports. It
        /// is counted apart from <see cref="AgentCalls"/> so a test can say what an act costs and what a
        /// decline costs, and so the tests written before the second question existed keep their meaning:
        /// <see cref="AgentCalls"/> is still the number of JUDGEMENT calls.</summary>
        public int OwnStateCalls { get; private set; }

        /// <summary>The second question as it was asked, or null when it never was.</summary>
        public string? LastOwnStatePrompt { get; private set; }

        /// <summary>What the model answers the SECOND question with. It answers "own" by default, which is
        /// the answer that lets a correct act through, so every test about the rest of the act path keeps
        /// testing the rest of the act path. The tests below set it to say otherwise.</summary>
        public string? OwnStateReply { get; set; } =
            $$"""{ "{{RuleOwnStateContract.Field}}": "own", "reason": "The agent printed this line about itself." }""";

        /// <summary>THE SEND SEAM. Every keystroke this feature can produce goes through here.</summary>
        public List<string> Typed { get; } = new();
        public RuleSendResult SendResult { get; set; } = RuleSendResult.Confirmed();

        public DateTime NowUtc { get; set; } = Now;

        public IReadOnlyList<SessionRule> Rules(TenantId tenant) => StoredRules;

        /// <summary>Run with the firings ALREADY READ and about to be handed back, which is INSIDE the free
        /// checks and therefore before the evaluator has recorded that it has looked at this screen. That is
        /// the exact window an overlapping pass arrives in - one pass holding a snapshot of the record that
        /// another pass is about to write to - so it is where a test has to hold a pass to make the overlap
        /// deterministic rather than hoping for a race. Holding it BEFORE the read instead would let the
        /// held pass see the other pass's act when it resumed, and the cooldown would hide the defect.</summary>
        public Action? WhileHoldingTheFiringsSnapshot { get; set; }

        /// <summary>Whether a firing this environment is told about becomes visible to the free checks, the
        /// way a real store's does. Off by default so the tests written before it keep their exact shape;
        /// on, it is what makes the cooldown and the daily cap real in a test rather than assumed.</summary>
        public bool FiringsAreVisibleToTheFreeChecks { get; set; }

        public IReadOnlyList<SessionRuleFiring> FiringsFor(TenantId tenant, Guid ruleId)
        {
            List<SessionRuleFiring> snapshot;
            lock (StoredFirings) snapshot = StoredFirings.Where(f => f.RuleId == ruleId).ToList();
            WhileHoldingTheFiringsSnapshot?.Invoke();
            return snapshot;
        }

        /// <summary>The repository the session is working in. Settable so a test can give it a string that
        /// could not appear by accident and then look for it where it must not be.</summary>
        public string RepositoryPath { get; set; } = @"D:\ReposFred\scratch";

        /// <summary>How many times the session's facts were asked for. A re-read immediately before the
        /// keystroke is a PRESENCE, and this is what counts it.</summary>
        public int FactReads => _factReads;

        /// <summary>Set to make the session vanish from the roster after this many fact reads.</summary>
        public int? SessionVanishesAfterFactRead { get; set; }

        public RuleSessionFacts? ReadSessionFacts(TenantId tenant, string sessionId)
        {
            var index = _factReads;
            _factReads++;
            if (SessionVanishesAfterFactRead is { } vanishAfter && index >= vanishAfter) return null;
            var state = ActivityStatesInOrder.Count == 0
                ? ActivityState
                : ActivityStatesInOrder[Math.Min(index, ActivityStatesInOrder.Count - 1)];
            return new(sessionId, "RawCli", RepositoryPath, "SOREN_NORTH", "Session Rules", state);
        }

        public Task<IReadOnlyList<string>?> ReadScreenRowsAsync(
            TenantId tenant, string directorId, string sessionId, CancellationToken ct)
        {
            var index = Math.Min(_screenReads, Screens.Count - 1);
            _screenReads++;
            return Task.FromResult(Screens.Count == 0 ? null : Screens[index]);
        }

        public Task<string?> AskAgentAsync(TenantId tenant, string prompt, CancellationToken ct)
        {
            // WHICH QUESTION IS THIS? The two run-time questions go through the one seam, so the fake tells
            // them apart by the field only the second one asks for - the same constant the prompt is built
            // from, so a renamed field cannot leave this fake silently answering the wrong question.
            if (prompt is not null && prompt.Contains(RuleOwnStateContract.Field, StringComparison.Ordinal))
            {
                OwnStateCalls++;
                LastOwnStatePrompt = prompt;
                return Task.FromResult(OwnStateReply);
            }

            AgentCalls++;
            LastPrompt = prompt;
            return Task.FromResult(AgentReply);
        }

        /// <summary>Read at the moment the send seam is reached, so a test can assert that the record
        /// already existed BEFORE the keystroke rather than inferring it from the order of a list.</summary>
        public Func<int>? RecordedWhenTyping { get; set; }

        /// <summary>How many firings had been written when the send seam was reached.</summary>
        public int RecordsWhenTheSendHappened { get; private set; } = -1;

        public Task<RuleSendResult> TypeIntoSessionAsync(
            TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct)
        {
            if (RecordedWhenTyping is not null) RecordsWhenTheSendHappened = RecordedWhenTyping();
            lock (Typed) Typed.Add(text);
            return Task.FromResult(SendResult);
        }

        /// <summary>Set to make the store refuse to write a firing with this decision - what a real store
        /// does for a rule deleted mid-pass, a record it will not accept, or a database that is down.</summary>
        public string? RefuseToRecordDecision { get; set; }

        /// <summary>What each written firing was completed with afterwards, by id.</summary>
        public Dictionary<Guid, (string TypedText, string Outcome)> Completed { get; } = new();

        public Guid RecordFiring(TenantId tenant, RuleFiringDraft draft)
        {
            if (string.Equals(RefuseToRecordDecision, draft.Decision, StringComparison.Ordinal))
                throw new RuleRejectedException(
                    "this record was refused by the store, which is what a rule deleted during the model " +
                    "call looks like from here.");

            var id = Guid.NewGuid();
            lock (Recorded)
            {
                _rowOf[id] = Recorded.Count;
                Recorded.Add(draft);
            }
            lock (WrittenIds) WrittenIds.Add(id);
            if (!FiringsAreVisibleToTheFreeChecks) return id;
            lock (StoredFirings)
                StoredFirings.Add(new SessionRuleFiring(
                    id, draft.RuleId, draft.SessionId, NowUtc, draft.ScreenText,
                    draft.Understanding, draft.Decision, draft.Reason, draft.Runs, draft.TypedText,
                    draft.Outcome, draft.Grounding));
            return id;
        }

        /// <summary>The ids handed out, in order.</summary>
        public List<Guid> WrittenIds { get; } = new();

        /// <summary>Where each written firing sits in <see cref="Recorded"/>, so completing one UPDATES the
        /// row rather than adding a second - which is what a real store does, and what makes
        /// <see cref="Recorded"/> the record a reader would actually find.</summary>
        private readonly Dictionary<Guid, int> _rowOf = new();

        public void CompleteFiring(TenantId tenant, Guid firingId, string typedText, string outcome)
        {
            lock (Completed) Completed[firingId] = (typedText, outcome);
            lock (Recorded)
            {
                if (!_rowOf.TryGetValue(firingId, out var row)) return;
                Recorded[row] = Recorded[row] with { TypedText = typedText, Outcome = outcome };
            }
        }
    }

    /// <summary>The text every test rule types when it acts, stored with the rule (phase 1).</summary>
    private const string TheStoredText = "/model opus";

    private static SessionRule Rule(
        RuleState state = RuleState.DryRun,
        IReadOnlyList<string>? triggerWords = null,
        Guid? id = null,
        string textToType = TheStoredText,
        IReadOnlyList<RulePrimitiveCall>? calls = null) => new(
            id ?? Guid.NewGuid(),
            TheSentence,
            "A session stopped on a provider allowance notice.",
            textToType,
            triggerWords ?? new[] { "reached your", "limit" },
            calls ?? Array.Empty<RulePrimitiveCall>(),
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

    /// <summary>An ACT reply in its phase 1 shape: the decision, ONE line copied from the screen, and why.
    /// It carries no text to type and names no checks - both are on the rule. The quote is the notice
    /// itself, so the grounding check finds it on the screen; the tests about a missing or invented
    /// quote build their own replies.</summary>
    private static string ActReply(Guid ruleId) => $$"""
        {
          "rule_id": "{{ruleId}}",
          "decision": "act",
          "quote": "{{TheNotice}}",
          "reason": "The session itself is blocked on its model allowance, which is what the instruction is about."
        }
        """;

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

    // ---- dry run types nothing ----------------------------------------------------------------------

    [Fact]
    public async Task A_dry_run_rule_types_nothing_and_records_what_it_would_have_typed()
    {
        var rule = Rule(state: RuleState.DryRun);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);

        var pass = await Run(env);

        // The instrumented send seam, counted at zero...
        Assert.Empty(env.Typed);
        // ...and the PRESENCE that says the evaluator really did get all the way there.
        Assert.Equal(RulePassOutcomes.DryRun, pass.What);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Act, firing.Decision);
        Assert.Equal("", firing.TypedText);
        Assert.Contains("dry run", firing.Outcome);
        Assert.Contains(TheStoredText, firing.Outcome);
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
    /// A SEND NOBODY ANSWERED FOR IS NOT A SEND THAT LANDED, AND IT IS NOT ONE THAT DID NOT.
    ///
    /// Two defects meet on this line and pull in opposite directions, so the record has to refuse both. The
    /// prompt route answers "never started a turn ... parked in the composer unsubmitted" for a session
    /// whose turn is over in milliseconds - a shell - while the keystroke has in fact landed; reading that
    /// as "the send did not land" put a sentence into a real firing record that the session's own screen
    /// disproved. But the same answer also comes back when the Director refused the command outright, when
    /// the tunnel dropped, and when nothing answered at all - and in those the text did NOT land. Writing
    /// "typed into the session" for all of them is the first lie wearing the other coat.
    ///
    /// So an unanswered send records: what was SENT, in the outcome, in full; and NO typed text, because
    /// typed text is this product's word for "this reached the session" and nothing here confirmed that.
    /// The session's screen is named as the only evidence either way.
    /// </summary>
    [Fact]
    public async Task A_send_nobody_answered_for_names_the_text_it_sent_and_does_not_claim_it_landed()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.SendResult = RuleSendResult.Unknown(
            "never started a turn within 8 beats: the agent produced under 2048 bytes.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.SendUnconfirmed, pass.What);
        Assert.Equal(TheStoredText, Assert.Single(env.Typed));
        var firing = Assert.Single(env.Recorded);

        // The claim that must not be made: typed text is the product's word for "it reached the session".
        Assert.Equal("", firing.TypedText);

        // And the presence that must be there, so nothing is lost by refusing the claim: the record still
        // says exactly what was put on the wire, and says the screen is the evidence.
        Assert.Contains(TheStoredText, firing.Outcome);
        Assert.Contains("nothing confirmed", firing.Outcome);
        Assert.Contains("screen", firing.Outcome);
        Assert.DoesNotContain("did not land", firing.Outcome);
        Assert.DoesNotContain("never reached", firing.Outcome);
        Assert.DoesNotContain("typed into the session", firing.Outcome);
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

        // THE ONE SESSION FACT THAT IS ALLOWED IN, and why it is not a breach of A11: the agent does not
        // decide whether the instruction applies - the scope filter already removed every other agent's
        // session before this question was asked - it decides how the screen is READ. The same trouble
        // prints different words on different agents. This was added on 3 September 2026 when the owner
        // ruled rules are agent-specific by default, and it stopped at the agent because this test said
        // the machine stays out.
        Assert.Contains("running the agent", prompt, StringComparison.Ordinal);
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

    // ---- an act must cite the screen ----------------------------------------------------------------

    [Fact]
    public async Task A_decline_that_cites_nothing_is_still_recorded_and_the_record_says_it_cited_nothing()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = DeclineReply(rule.Id, "the screen is only talking about a limit, not reporting one.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Declined, pass.What);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Decline, firing.Decision);
        Assert.Contains("cite", firing.Grounding, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the record exists before the keystroke ------------------------------------------------------

    /// <summary>
    /// AN ACTION THE PRODUCT CANNOT ACCOUNT FOR IS AN ACTION NOBODY CAN SUPERVISE.
    ///
    /// The evaluator typed first and recorded afterwards. Everything that can make the record fail - a rule
    /// deleted during the model call, a record the store will not accept, a database that is down - then
    /// happened AFTER something had already been done to somebody's session, and the only trace was a log
    /// line. The record is the product; it has to exist before the side effect and be reconciled after it.
    ///
    /// So the store's refusal is a REASON NOT TO ACT, not a detail discovered too late. Here the store
    /// refuses the act's record, and nothing may be typed.
    /// </summary>
    [Fact]
    public async Task An_act_whose_record_the_store_refuses_is_not_carried_out()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.RefuseToRecordDecision = RuleDecisions.Act;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.NotRecorded, pass.What);
        Assert.Empty(env.Typed);
    }

    [Fact]
    public async Task The_record_of_an_act_is_written_before_the_keystroke_and_completed_after_it()
    {
        // THE PRESENCE, and it is what makes the test above mean something: a pass that simply refused to
        // act would satisfy "nothing was typed" while proving nothing about the ordering. Here the act goes
        // through, and the record must have been WRITTEN before the send and COMPLETED afterwards - two
        // observable events on one row, in that order.
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.RecordedWhenTyping = () => env.WrittenIds.Count;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Acted, pass.What);
        Assert.Equal(TheStoredText, Assert.Single(env.Typed));

        // Written first: by the time the send seam was reached, the record already existed.
        Assert.Equal(1, env.RecordsWhenTheSendHappened);

        // Completed after: the same row, told what became of the keystroke.
        var id = Assert.Single(env.WrittenIds);
        Assert.True(env.Completed.ContainsKey(id),
            "the firing was written before the keystroke and never completed afterwards, so the record " +
            "still says only that a keystroke was about to go.");
        Assert.Contains(TheStoredText, env.Completed[id].Outcome);
        Assert.Equal(TheStoredText, env.Completed[id].TypedText);
    }

    // ---- still idle, immediately before the keystroke -----------------------------------------------

    /// <summary>
    /// THE SESSION MUST STILL BE IDLE WHEN THE KEYSTROKE GOES, NOT MERELY WHEN THE DECISION WAS MADE.
    ///
    /// "Idle sessions only" is a primary bound, and it was read ONCE, before the agent call. Between that
    /// read and the keystroke there is a model call - the longest gap in the whole pass - and the only thing
    /// re-read across it was the terminal screen. A new owner turn makes the session Working before any of
    /// its output appears, so the visible rows are briefly identical and the stale decision types anyway,
    /// straight into a turn somebody else just started.
    ///
    /// Screen equality is not proof that a session is still idle. This holds the screen still and changes
    /// only the thing that actually says whether the session is working.
    /// </summary>
    [Fact]
    public async Task A_session_that_started_working_during_the_agent_call_is_abandoned_though_the_screen_is_unchanged()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        // Idle when the pass starts; working by the time the keystroke would go. The screen never changes.
        env.ActivityStatesInOrder.Add("WaitingForInput");
        env.ActivityStatesInOrder.Add(RuleCandidateFilter.WorkingState);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Abandoned, pass.What);
        Assert.Contains("working", pass.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(pass.Recorded);
        Assert.Equal(RuleDecisions.Abandoned, firing.Decision);
        Assert.True(env.FactReads >= 2,
            "the session's facts were read " + env.FactReads + " time(s). A bound that is checked before a " +
            "model call and never again is a bound about a moment that has passed.");
    }

    [Fact]
    public async Task A_session_that_left_the_roster_during_the_agent_call_is_abandoned()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.SessionVanishesAfterFactRead = 1;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Abandoned, pass.What);
        Assert.Empty(env.Typed);
        Assert.Single(pass.Recorded);
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
        env.WhileHoldingTheFiringsSnapshot = () =>
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
        Assert.Equal(TheStoredText, Assert.Single(env.Typed));
        Assert.Single(env.Recorded, r => r.Decision == RuleDecisions.Act);
        Assert.Empty(second.Recorded);
    }

    private static string ScreenTextOf(RuleFiringDraft draft) =>
        draft.ScreenText.Split('\n').Select(l => l.TrimEnd()).Last(l => l.Contains("reached your"));

    // ---- phase 1: the text typed is the STORED text, the question is yes/no plus one copied line -----

    /// <summary>A phase 1 act reply: a decision and ONE line copied from the screen. It also carries a
    /// "type" of its own, which the evaluator must ignore - nothing the model says is ever typed.</summary>
    private static string ActWithQuote(Guid ruleId, string quote, string typeItWouldLike = "/usage-credits") => $$"""
        {
          "rule_id": "{{ruleId}}",
          "decision": "act",
          "quote": "{{quote}}",
          "reason": "The session itself is blocked on its model allowance, which is what the instruction is about.",
          "type": "{{typeItWouldLike}}"
        }
        """;

    /// <summary>
    /// THE ACCEPTANCE ROW: the text typed is the authored text, verbatim, and nothing is composed at run
    /// time. The reply carries a text of its own and it is not typed; the stored text is, byte for byte.
    /// </summary>
    [Fact]
    public async Task A_live_rule_types_the_stored_text_byte_for_byte_and_never_what_the_reply_carries()
    {
        var rule = Rule(state: RuleState.Live, textToType: "/model opus");
        var env = EnvironmentWith(rule);
        env.AgentReply = ActWithQuote(rule.Id, TheNotice, typeItWouldLike: "/usage-credits");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Acted, pass.What);
        Assert.Equal("/model opus", Assert.Single(env.Typed));
        Assert.Equal("/model opus", Assert.Single(env.Recorded).TypedText);
        Assert.DoesNotContain("/usage-credits", Assert.Single(env.Recorded).Outcome, StringComparison.Ordinal);
    }

    /// <summary>
    /// A RULE STORED BEFORE RULES CARRIED THEIR TEXT HAS NOTHING TO TYPE. It must not silently stop
    /// firing - a rule that silently stopped is a trust failure - and it must not fall back to composing
    /// text at run time. So it is refused OUT LOUD: a recorded firing naming the rule as needing to be
    /// re-authored, no model call, no keystroke.
    /// </summary>
    [Fact]
    public async Task A_rule_stored_before_rules_carried_their_text_is_refused_out_loud_and_the_model_is_never_asked()
    {
        var rule = Rule(state: RuleState.Live, textToType: "");
        var env = EnvironmentWith(rule);
        env.AgentReply = ActWithQuote(rule.Id, TheNotice);

        var pass = await Run(env);

        Assert.Equal(0, env.AgentCalls);
        Assert.Empty(env.Typed);
        Assert.Equal(RulePassOutcomes.NeedsReauthoring, pass.What);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(rule.Id, firing.RuleId);
        Assert.Equal(RuleDecisions.Refused, firing.Decision);
        Assert.Contains("re-author", firing.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", firing.TypedText);
    }

    /// <summary>Two rules in play, one of them unauthored: the unauthored one is refused on its own record
    /// and the other is still asked about. One rule's defect never silences another.</summary>
    [Fact]
    public async Task An_unauthored_rule_is_refused_on_its_own_record_while_the_other_rules_are_still_asked_about()
    {
        var old = Rule(state: RuleState.Live, textToType: "");
        var current = Rule(state: RuleState.Live, textToType: "/model opus");
        var env = EnvironmentWith(old);
        env.StoredRules.Add(current);
        env.AgentReply = ActWithQuote(current.Id, TheNotice);

        var pass = await Run(env);

        Assert.Equal(1, env.AgentCalls);
        Assert.DoesNotContain(old.Id.ToString(), env.LastPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RulePassOutcomes.Acted, pass.What);
        Assert.Equal("/model opus", Assert.Single(env.Typed));
        Assert.Equal(2, env.Recorded.Count);
        Assert.Contains(env.Recorded, f => f.RuleId == old.Id && f.Decision == RuleDecisions.Refused);
        Assert.Contains(env.Recorded, f => f.RuleId == current.Id && f.Decision == RuleDecisions.Act);
    }

    /// <summary>The checks a rule was STORED with are the checks that run - the question names none, so
    /// there is nothing for a model to invent an argument for.</summary>
    [Fact]
    public async Task The_checks_the_rule_was_stored_with_are_run_and_their_answers_are_on_the_record()
    {
        var stored = RulePrimitiveCall.To(
            "matches_any",
            RuleArgument.FromInput("text", RuleInput.ScreenText),
            RuleArgument.LiteralList("terms", new[] { "reached your" }));
        var rule = Rule(state: RuleState.Live, calls: new[] { stored });
        var env = EnvironmentWith(rule);
        env.AgentReply = ActWithQuote(rule.Id, TheNotice);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Acted, pass.What);
        var run = Assert.Single(Assert.Single(env.Recorded).Runs);
        Assert.Equal("matches_any", run.Name);
        Assert.Contains("true", run.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_stored_check_that_answers_no_abandons_the_act_and_types_nothing()
    {
        var stored = RulePrimitiveCall.To(
            "matches_any",
            RuleArgument.FromInput("text", RuleInput.ScreenText),
            RuleArgument.LiteralList("terms", new[] { "zz-not-on-this-screen" }));
        var rule = Rule(state: RuleState.Live, calls: new[] { stored });
        var env = EnvironmentWith(rule);
        env.AgentReply = ActWithQuote(rule.Id, TheNotice);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Abandoned, pass.What);
        Assert.Empty(env.Typed);
        Assert.Equal(RuleDecisions.Abandoned, Assert.Single(env.Recorded).Decision);
    }

    /// <summary>
    /// RULING A12, ASKED FOR AS A FIELD (phase 1, ruling P1-A). An act must cite something a person can go
    /// back and check. The citation is the "quote" field - one line copied from the screen - and it is
    /// checked against the very excerpt the model was shown. A quote that is not there refuses the act.
    /// </summary>
    [Fact]
    public async Task An_act_whose_quote_is_not_on_the_screen_is_refused_and_types_nothing()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActWithQuote(rule.Id, "YOUR SUBSCRIPTION HAS BEEN CANCELLED");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Ungrounded, pass.What);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Refused, firing.Decision);
        Assert.Contains("YOUR SUBSCRIPTION HAS BEEN CANCELLED", firing.Grounding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_act_with_no_quote_is_refused_because_nothing_could_be_checked()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = $$"""
        {
          "rule_id": "{{rule.Id}}",
          "decision": "act",
          "reason": "the screen says 'reached your Fable 5 limit', which is the session's own state."
        }
        """;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Ungrounded, pass.What);
        Assert.Empty(env.Typed);
        Assert.Contains("nothing", Assert.Single(env.Recorded).Grounding, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The PRESENCE half: a quote that IS a line of the screen lets the act through, and the
    /// reason needs no quotation marks of its own for that.</summary>
    [Fact]
    public async Task An_act_whose_quote_is_a_line_of_the_screen_acts_and_the_record_names_the_line()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActWithQuote(rule.Id, TheNotice);

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Acted, pass.What);
        Assert.Equal(TheStoredText, Assert.Single(env.Typed));
        Assert.Contains(TheNotice, Assert.Single(env.Recorded).Grounding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_decline_whose_quote_is_not_on_the_screen_is_recorded_with_the_mismatch_noted()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = $$"""
        {
          "rule_id": "{{rule.Id}}",
          "decision": "decline",
          "quote": "YOUR SUBSCRIPTION HAS BEEN CANCELLED",
          "reason": "the notice is being discussed, not reported."
        }
        """;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Declined, pass.What);
        Assert.Empty(env.Typed);
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Decline, firing.Decision);
        Assert.Contains("does not contain", firing.Grounding, StringComparison.Ordinal);
    }

    /// <summary>The question is yes/no plus one copied line. It offers no checks and asks for no text to
    /// type, because the rule holds both already.</summary>
    [Fact]
    public async Task The_question_asks_only_whether_this_is_the_situation_and_for_one_copied_line()
    {
        var rule = Rule();
        var env = EnvironmentWith(rule);
        env.AgentReply = DeclineReply(rule.Id, "not this situation.");

        await Run(env);

        var prompt = env.LastPrompt!;
        Assert.Contains("\"quote\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"checks\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("matches_any", prompt, StringComparison.Ordinal);
        // And the excerpt it carries is the one the quote is checked against - one function, one text.
        Assert.Contains(RuleScreenExcerpt.Of(string.Join("\n", BlockedScreen)), prompt, StringComparison.Ordinal);
    }

    // ---- the second question: whose state does the cited line report? --------------------------------
    //
    // The phase 1 measurement found the fast model's five wrong negatives were all one confusion - a screen
    // that TALKS ABOUT a limit read as a screen where this session has stopped on one - and the citation
    // cannot catch it, because in every one of those cases the cited line really is on the screen. So an
    // act pays for one short second question, and a decline pays nothing.

    private static string OwnStateReply(string verdict, string reason) => $$"""
        {
          "{{RuleOwnStateContract.Field}}": "{{verdict}}",
          "reason": "{{reason}}"
        }
        """;

    [Fact]
    public async Task An_act_is_refused_when_the_line_it_cited_is_not_this_sessions_own_state()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.OwnStateReply = OwnStateReply(
            RuleOwnState.Elsewhere, "It is a report about another session that hit its limit.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.NotItsOwnState, pass.What);
        Assert.Empty(env.Typed);
        Assert.Equal(1, env.AgentCalls);
        Assert.Equal(1, env.OwnStateCalls);

        // The refusal is a RECORD, with the second answer's reason on it, so a reader can see which of the
        // two questions stopped it. A pass that stopped here must not look like a pass that never ran.
        var firing = Assert.Single(env.Recorded);
        Assert.Equal(RuleDecisions.Refused, firing.Decision);
        Assert.Contains("report about another session", firing.Reason, StringComparison.Ordinal);
        Assert.Contains("its own state", firing.Outcome, StringComparison.Ordinal);
        Assert.Equal("", firing.TypedText);
    }

    [Fact]
    public async Task An_own_answer_lets_the_act_through_and_the_rules_own_text_is_typed()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.OwnStateReply = OwnStateReply(RuleOwnState.Own, "This session's own agent printed it about itself.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Acted, pass.What);
        Assert.Equal(TheStoredText, Assert.Single(env.Typed));
        Assert.Equal(1, env.OwnStateCalls);
    }

    /// <summary>A DECLINE PAYS NOTHING. The second question is on the expensive side only, which is the
    /// whole reason it is affordable - proved by the count, not by reading the code.</summary>
    [Fact]
    public async Task A_decline_never_pays_for_the_second_question()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = DeclineReply(rule.Id, "the screen is only talking about a limit.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Declined, pass.What);
        Assert.Equal(1, env.AgentCalls);
        Assert.Equal(0, env.OwnStateCalls);
        Assert.Null(env.LastOwnStatePrompt);
    }

    /// <summary>An act whose citation is not on the screen is already refused, so the second question is
    /// never bought for it either. The cheap check runs first.</summary>
    [Fact]
    public async Task An_act_whose_citation_is_not_on_the_screen_never_pays_for_the_second_question()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActWithQuote(rule.Id, "a sentence that was never anywhere on this screen");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.Ungrounded, pass.What);
        Assert.Equal(0, env.OwnStateCalls);
        Assert.Empty(env.Typed);
    }

    /// <summary>THE PASS CONDITION IS A PRESENCE. A second question that was asked and not answered leaves
    /// the act refused - never through on the absence of an objection.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I would rather not say.")]
    [InlineData("{ \"whose_state\": \"perhaps\", \"reason\": \"neither\" }")]
    public async Task A_second_question_that_was_not_answered_refuses_the_act(string? unreadable)
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.OwnStateReply = unreadable;

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.NotItsOwnState, pass.What);
        Assert.Empty(env.Typed);
        Assert.Equal(RuleDecisions.Refused, Assert.Single(env.Recorded).Decision);
    }

    /// <summary>The second question is about the LINE THE FIRST ANSWER CITED, on the SAME excerpt, and it
    /// does not carry the account's instruction - it is not a second opinion on the first question.</summary>
    [Fact]
    public async Task The_second_question_carries_the_cited_line_and_the_same_screen_but_not_the_instruction()
    {
        var rule = Rule(state: RuleState.Live);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);

        await Run(env);

        var asked = env.LastOwnStatePrompt;
        Assert.NotNull(asked);
        Assert.Contains(TheNotice, asked!, StringComparison.Ordinal);
        Assert.Contains(RuleScreenExcerpt.Of(string.Join("\n", BlockedScreen)), asked, StringComparison.Ordinal);
        Assert.DoesNotContain(TheSentence, asked, StringComparison.Ordinal);
        Assert.DoesNotContain(rule.Id.ToString(), asked, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A dry run pays for the second question too, and is refused by it in the same way - the
    /// point of a dry run is to show what the rule WOULD do, and a rule that would have been stopped must
    /// not be reported as a rule that would have typed.</summary>
    [Fact]
    public async Task A_dry_run_is_refused_by_the_second_question_rather_than_reported_as_a_keystroke()
    {
        var rule = Rule(state: RuleState.DryRun);
        var env = EnvironmentWith(rule);
        env.AgentReply = ActReply(rule.Id);
        env.OwnStateReply = OwnStateReply(RuleOwnState.Elsewhere, "It is a banner, not a stop.");

        var pass = await Run(env);

        Assert.Equal(RulePassOutcomes.NotItsOwnState, pass.What);
        Assert.Empty(env.Typed);
        Assert.DoesNotContain(env.Recorded, f => f.Decision == RuleDecisions.Act);
    }
}
