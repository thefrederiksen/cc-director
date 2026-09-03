using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE AUTHORING CALL: the question that turns what somebody said into a rule, and the reading of the
/// answer. This is the half the Session Rules mission named as missing - trigger words and checks used to
/// be worked out by hand while the store refused a rule without them on the stated grounds that a model
/// works them out.
///
/// Two things are covered here and they are different. The QUESTION is covered because what it offers is
/// what may be named: it is built off the derived registry, so it can never advertise a check we do not
/// ship, and it must not quietly teach the model that rules are about one kind of trouble. The ANSWER is
/// covered because every part of it is validated before anything is put in front of a person - a check
/// that does not exist, a missing scope, a rule that cannot be read back, a trigger word that is not on
/// the screen the model was shown.
///
/// EVERY TEST HERE HAS A SCREEN, because since fix round D there is no reading without one: the screen is
/// a <see cref="RuleScreenReading"/> the Gateway made from a real session, and its excerpt is the one text
/// the prompt carries and the grounding check runs against.
/// </summary>
public sealed class RuleDraftContractTests
{
    private static readonly RulePrimitiveRegistry Registry = RulePrimitiveRegistry.Default;

    private static readonly string TheSentence =
        "When a session runs out of its allowance, switch it to another model and carry on.";

    private static IReadOnlyList<RuleDraftTurn> OneTurn(string said) =>
        new[] { new RuleDraftTurn(RuleDraftSpeakers.Person, said) };

    private static readonly RuleSessionOrigin ClaudeOnNorth = new("ClaudeCode", "SOREN_NORTH");

    /// <summary>The real thing a session shows when it stops on a limit - the case that cost the owner a
    /// night. The wording is a real Claude Code notice, not a paraphrase.</summary>
    private const string ACapturedLimitScreen = """
    > carry on with the refactor

    Claude usage limit reached. Your limit will reset at 11:50pm.

    >
    """;

    /// <summary>A screen that mentions no kind of trouble at all, for the tests about the question's
    /// framing - a limit screen would put the presumed words into the prompt through the screen.</summary>
    private const string ANeutralScreen = """
    > run the tests

    All 42 tests passed.

    >
    """;

    /// <summary>A screen as the Gateway reads one: this session, this origin, this text.</summary>
    private static RuleScreenReading Screen(string text, RuleSessionOrigin? origin = null) =>
        new("sid-1", origin ?? ClaudeOnNorth, text);

    // ---- the question ------------------------------------------------------------------------------

    /// <summary>Every check the product ships is offered, by the name a rule stores it under. The question
    /// is built off the registry, so a check added to the product turns up here without anybody editing a
    /// list.</summary>
    [Fact]
    public void The_question_offers_every_check_the_product_ships()
    {
        var prompt = RuleDraftContract.BuildDraftPrompt(OneTurn(TheSentence), Registry, Screen(ANeutralScreen));

        Assert.NotEmpty(Registry.Primitives);
        foreach (var primitive in Registry.Primitives)
            Assert.Contains(primitive.Name, prompt, StringComparison.Ordinal);
    }

    /// <summary>The runtime inputs are offered in the same angle-bracket notation the stored rule and the
    /// firing record use, so there is one notation in this feature rather than three.</summary>
    [Fact]
    public void The_question_offers_the_runtime_inputs_in_angle_brackets()
    {
        var prompt = RuleDraftContract.BuildDraftPrompt(OneTurn(TheSentence), Registry, Screen(ANeutralScreen));

        Assert.NotEmpty(RuleInputs.Names);
        foreach (var name in RuleInputs.Names)
            Assert.Contains("<" + name + ">", prompt, StringComparison.Ordinal);
    }

    /// <summary>Both sides of the conversation reach the question, and are told apart - an answer to a
    /// question the model cannot see is an answer to nothing.</summary>
    [Fact]
    public void The_question_carries_what_was_said_by_both_sides()
    {
        var prompt = RuleDraftContract.BuildDraftPrompt(new[]
        {
            new RuleDraftTurn(RuleDraftSpeakers.Person, TheSentence),
            new RuleDraftTurn(RuleDraftSpeakers.DevThrottle, "Which sessions should this apply to?"),
            new RuleDraftTurn(RuleDraftSpeakers.Person, "All of them."),
        }, Registry, Screen(ANeutralScreen));

        Assert.Contains("they said: " + TheSentence, prompt, StringComparison.Ordinal);
        Assert.Contains("you asked: Which sessions should this apply to?", prompt, StringComparison.Ordinal);
        Assert.Contains("they said: All of them.", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE QUESTION DOES NOT PRESUME WHAT A RULE IS ABOUT. The evaluator was demonstrated on a provider
    /// allowance notice, and the standing danger is that the authoring question gets written around that
    /// one case - at which point every rule an account writes comes back shaped like an allowance rule and
    /// nobody can tell whether the language was ever general.
    ///
    /// The owner's own second case is exactly this: a provider that stops answering is not a session
    /// calmly reporting that it is out of allowance, and the two want different acts. So the question
    /// itself must name no kind of trouble as the expected one. Its only mention of allowance is whatever
    /// the person said, which is why the sentence used here is deliberately an allowance one: the words
    /// must appear ONCE, in their turn, and nowhere in the framing. The screen is a neutral one, so the
    /// screen cannot carry them in either.
    /// </summary>
    [Fact]
    public void The_question_names_no_kind_of_trouble_as_the_expected_one()
    {
        var prompt = RuleDraftContract.BuildDraftPrompt(OneTurn(TheSentence), Registry, Screen(ANeutralScreen));

        var framing = prompt.Replace(TheSentence, "", StringComparison.Ordinal);

        foreach (var presumed in new[] { "allowance", "usage limit", "credit", "rate limit" })
            Assert.DoesNotContain(presumed, framing, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The framing says an instruction may be about anything the screen can show, which is the
    /// positive form of the test above - an absence alone would pass just as happily over an empty
    /// prompt.</summary>
    [Fact]
    public void The_question_says_an_instruction_may_be_about_anything_the_screen_shows()
    {
        var prompt = RuleDraftContract.BuildDraftPrompt(OneTurn(TheSentence), Registry, Screen(ANeutralScreen));

        Assert.Contains("can be about anything a session's screen can show", prompt, StringComparison.Ordinal);
        Assert.Contains("not assume you know which kind of trouble is meant",
            prompt, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the answer: a rule -------------------------------------------------------------------------

    /// <summary>A good answer to the limit screen: every trigger word is on it.</summary>
    private const string AGoodReply = """
    {
      "answer": "propose",
      "screen_description": "The session has stopped on a notice that the account is out of allowance.",
      "type": "/model opus",
      "trigger_words": ["usage limit", "11:50pm"],
      "checks": [ { "name": "matches_any", "arguments": { "text": "<screen_text>", "terms": ["usage limit", "out of credits"] } } ],
      "scope": "all-sessions",
      "cooldown_seconds": 600,
      "daily_cap": 4,
      "read_back": "When one of your sessions stops on an allowance notice, I will switch it to another model and tell it to carry on."
    }
    """;

    [Fact]
    public void A_proposed_rule_is_read_with_every_part_intact()
    {
        var reading = RuleDraftContract.Read(AGoodReply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Refusal);
        Assert.Null(reading.Question);
        var proposal = Assert.IsType<RuleProposal>(reading.Proposal);

        Assert.Equal("The session has stopped on a notice that the account is out of allowance.", proposal.ScreenDescription);
        Assert.Equal(new[] { "usage limit", "11:50pm" }, proposal.TriggerWords);
        // The model said every session; the agent part is the SESSION'S agent, pinned by the Gateway.
        Assert.Equal(new RuleScope("ClaudeCode", null, null, null), proposal.Scope);
        Assert.Equal(600, proposal.CooldownSeconds);
        Assert.Equal(4, proposal.DailyCap);
        Assert.Contains("switch it to another model", proposal.ReadBack, StringComparison.Ordinal);
        // And the proposal carries what the write route needs to run the same check again.
        Assert.Equal("sid-1", proposal.SessionId);
        Assert.False(proposal.AllAgents);
        Assert.Equal(RuleScreenExcerpt.Of(ACapturedLimitScreen), proposal.ExampleScreen);

        var call = Assert.Single(proposal.Calls);
        Assert.Equal("matches_any", call.Name);
        Assert.Equal("matches_any(text=<screen_text>, terms=usage limit,out of credits)", call.Describe());
    }

    /// <summary>
    /// THE INSTRUCTION IS THE PERSON'S OWN WORDS, and it does not come out of the reply at all. The store
    /// treats the instruction as the authority; a model asked to restate a sentence will eventually
    /// improve it, and an improved authority is a different authority from the one the account gave.
    /// </summary>
    [Fact]
    public void The_instruction_is_the_persons_words_and_not_the_models()
    {
        var replyThatRewritesIt = AGoodReply.Replace(
            "\"answer\": \"propose\"",
            "\"answer\": \"propose\", \"instruction\": \"Handle rate limits automatically.\"",
            StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(replyThatRewritesIt, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Equal(TheSentence, reading.Proposal!.Instruction);
    }

    [Fact]
    public void A_check_that_does_not_exist_is_refused_by_name()
    {
        var reply = AGoodReply.Replace("\"matches_any\"", "\"screen_contains_regex\"", StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.Contains("screen_contains_regex", reading.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_check_given_the_wrong_arguments_is_refused()
    {
        var reply = AGoodReply.Replace(
            "{ \"text\": \"<screen_text>\", \"terms\": [\"usage limit\", \"out of credits\"] }",
            "{ \"text\": \"<screen_text>\" }",
            StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.Contains("terms", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>A rule that does not say which sessions it may act on is refused rather than read as every
    /// session there is. The widest possible value is the one an omission must never become.</summary>
    [Fact]
    public void A_rule_that_does_not_say_which_sessions_is_refused()
    {
        var reply = AGoodReply.Replace("\"scope\": \"all-sessions\",", "", StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.Contains("which sessions", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>A rule with nothing to read back is refused: the read-back is what the person confirms,
    /// and without it they would be agreeing to a list of trigger words.</summary>
    [Fact]
    public void A_rule_that_cannot_be_read_back_is_refused()
    {
        var reply = AGoodReply.Replace(
            "\"read_back\": \"When one of your sessions stops on an allowance notice, I will switch it to another model and tell it to carry on.\"",
            "\"read_back\": \"   \"",
            StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.Contains("what it would actually do", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>A reply wrapped in prose or a fenced block is still read - chat models do this - but the
    /// reading is the JSON, never the prose.</summary>
    [Fact]
    public void A_reply_wrapped_in_prose_is_still_read()
    {
        var reading = RuleDraftContract.Read(
            "Here is the rule I would write:\n```json\n" + AGoodReply + "\n```\nLet me know.",
            TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.NotNull(reading.Proposal);
    }

    // ---- the screen: the words have to be ON it, and it is the same text the model was shown ----------

    /// <summary>
    /// THE QUESTION SAYS TO READ THE WORDS OFF THE SCREEN, and carries the screen's exact excerpt. Without
    /// a screen the model guesses what one says, and it guesses plausibly and wrongly - asked about a
    /// provider outage with no screen, a live model proposed ECONNREFUSED, ETIMEDOUT and 429, none of
    /// which a coding agent necessarily prints. Since fix round D there is no prompt without a screen.
    /// </summary>
    [Fact]
    public void The_question_carries_the_screens_exact_excerpt_with_the_instruction_to_use_it()
    {
        var screen = Screen(ACapturedLimitScreen);

        var prompt = RuleDraftContract.BuildDraftPrompt(OneTurn("when this happens, wait and then carry on"), Registry, screen);

        Assert.Contains(screen.Excerpt, prompt, StringComparison.Ordinal);
        Assert.Contains("TAKE THE TRIGGER WORDS FROM THIS SCREEN", prompt, StringComparison.Ordinal);
        Assert.Contains("do not invent likely-looking error strings", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A WORD THAT IS NOT ON THE SCREEN IS REFUSED, BY NAME. This is the whole value of reading one: the
    /// rule can be checked against reality at the moment it is written, instead of looking perfectly good
    /// and never firing.
    /// </summary>
    [Fact]
    public void Trigger_words_that_are_not_on_the_screen_are_refused_by_name()
    {
        var reply = AGoodReply.Replace(
            "\"trigger_words\": [\"usage limit\", \"11:50pm\"],",
            "\"trigger_words\": [\"usage limit\", \"ECONNREFUSED\"],",
            StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.Contains("ECONNREFUSED", reading.Refusal!, StringComparison.Ordinal);
        Assert.Contains("not on the screen you captured", reading.Refusal!, StringComparison.Ordinal);

        // And it does NOT complain about the word that really is there.
        Assert.DoesNotContain("\"usage limit\"", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>Words that ARE on the screen pass, and the proposal keeps the exact excerpt it was checked
    /// against - so the rule carries the example it was made from.</summary>
    [Fact]
    public void Trigger_words_that_are_on_the_screen_are_accepted_and_the_excerpt_is_kept()
    {
        var reply = AGoodReply.Replace(
            "\"trigger_words\": [\"usage limit\", \"11:50pm\"],",
            "\"trigger_words\": [\"usage limit reached\", \"11:50pm\"],",
            StringComparison.Ordinal);
        var screen = Screen(ACapturedLimitScreen);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, screen);

        Assert.Null(reading.Refusal);
        Assert.Equal(new[] { "usage limit reached", "11:50pm" }, reading.Proposal!.TriggerWords);
        Assert.Equal(screen.Excerpt, reading.Proposal!.ExampleScreen);
        Assert.Contains("Claude usage limit reached", reading.Proposal!.ExampleScreen, StringComparison.Ordinal);
    }

    /// <summary>The check ignores case, because a screen and a model disagree about capitals constantly and
    /// the matching that runs later ignores case too. A guard stricter than the thing it guards would
    /// refuse rules that would have worked perfectly.</summary>
    [Fact]
    public void The_check_ignores_case_because_the_matching_it_guards_does()
    {
        var reply = AGoodReply.Replace(
            "\"trigger_words\": [\"usage limit\", \"11:50pm\"],",
            "\"trigger_words\": [\"USAGE LIMIT REACHED\"],",
            StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Refusal);
        Assert.NotNull(reading.Proposal);
    }

    /// <summary>
    /// A 41-line screen whose only distinctive word is on line 1. The prompt shows the model the last 40
    /// non-empty lines, so line 1 is text the model NEVER SAW - and a word from it is a word the model
    /// invented as far as the prompt is concerned. Inspection D found the check searching the whole
    /// caller string while the prompt showed the tail, so this word passed. Now there is one excerpt.
    /// </summary>
    private static string AScreenWhoseFirstLineIsOutsideTheExcerpt()
    {
        var lines = new List<string> { "FIRSTLINEWORD only here" };
        for (var i = 0; i < 40; i++) lines.Add($"ordinary line {i}");
        return string.Join("\n", lines);
    }

    [Fact]
    public void A_trigger_word_outside_the_lines_the_model_was_shown_is_refused()
    {
        var reply = AGoodReply.Replace(
            "\"trigger_words\": [\"usage limit\", \"11:50pm\"],",
            "\"trigger_words\": [\"FIRSTLINEWORD\"],",
            StringComparison.Ordinal);
        var screen = Screen(AScreenWhoseFirstLineIsOutsideTheExcerpt());

        // The premise, stated: the prompt really does not carry line 1.
        var prompt = RuleDraftContract.BuildDraftPrompt(OneTurn(TheSentence), Registry, screen);
        Assert.DoesNotContain("FIRSTLINEWORD", prompt, StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, screen);

        Assert.Null(reading.Proposal);
        Assert.Contains("FIRSTLINEWORD", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// ONE NORMALISER FOR A TRIGGER WORD. The store trims a word before storing it; the check used to run
    /// on the untrimmed word. So " usage limit reached " was checked as the padded string and stored as
    /// the narrower one - the word that was checked and the word that was stored were not the same string.
    /// The proposal has to carry the word in the form the store will keep.
    /// </summary>
    [Fact]
    public void A_padded_trigger_word_is_offered_as_the_word_the_store_will_keep()
    {
        var reply = AGoodReply.Replace(
            "\"trigger_words\": [\"usage limit\", \"11:50pm\"],",
            "\"trigger_words\": [\"  usage limit reached  \"],",
            StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Refusal);
        Assert.Equal(new[] { "usage limit reached" }, reading.Proposal!.TriggerWords);
        Assert.Equal(RuleTriggerWords.Normalise("  usage limit reached  "), reading.Proposal!.TriggerWords[0]);
    }

    // ---- which agent's screen this is, and who decides the agent scope -------------------------------

    /// <summary>
    /// THE MODEL IS TOLD WHICH AGENT IT IS LOOKING AT. A usage-limit notice on Claude Code reads "Claude
    /// usage limit reached"; on Codex or Gemini it reads something else. Trigger words are agent-specific
    /// whether anyone says so or not, so a model that does not know which agent printed the screen cannot
    /// know which words are that agent's and which are universal.
    /// </summary>
    [Fact]
    public void The_question_says_which_agent_the_screen_came_from()
    {
        var prompt = RuleDraftContract.BuildDraftPrompt(
            OneTurn("wait until it resets and carry on"), Registry, Screen(ACapturedLimitScreen));

        Assert.Contains("running the agent ClaudeCode", prompt, StringComparison.Ordinal);
        Assert.Contains("SOREN_NORTH", prompt, StringComparison.Ordinal);
        Assert.Contains("for ClaudeCode sessions only", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_question_says_when_the_rule_is_for_every_agent()
    {
        var prompt = RuleDraftContract.BuildDraftPrompt(
            OneTurn("wait until it resets and carry on"), Registry, Screen(ACapturedLimitScreen), allAgents: true);

        Assert.Contains("for EVERY agent", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("sessions only", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE AGENT PART OF THE SCOPE IS OURS, NOT THE MODEL'S. A rule written against a session is for that
    /// session's agent by default (the owner's ruling) - whatever the model wrote. Here the model said
    /// "all-sessions" and the proposal still comes back scoped to ClaudeCode, because that is a fact we
    /// hold rather than something to trust a guess about.
    /// </summary>
    [Fact]
    public void A_rule_written_against_a_session_is_scoped_to_that_sessions_agent_whatever_the_model_said()
    {
        var reading = RuleDraftContract.Read(AGoodReply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Refusal);
        Assert.Equal("ClaudeCode", reading.Proposal!.Scope.Agent);
    }

    /// <summary>The star: the account said every agent, so the agent part is lifted even though the
    /// session it was written against was a Claude Code one - and the proposal says the star was chosen,
    /// so the write route can hold the scope to the same choice.</summary>
    [Fact]
    public void Saying_every_agent_lifts_the_agent_scope()
    {
        var reading = RuleDraftContract.Read(
            AGoodReply, TheSentence, Registry, Screen(ACapturedLimitScreen), allAgents: true);

        Assert.Null(reading.Refusal);
        Assert.Null(reading.Proposal!.Scope.Agent);
        Assert.True(reading.Proposal!.AllAgents);
    }

    /// <summary>A model that tried to scope the rule to a DIFFERENT agent than the one the screen came
    /// from is overruled: the fact wins.</summary>
    [Fact]
    public void A_model_naming_a_different_agent_is_overruled_by_the_sessions_real_agent()
    {
        var reply = AGoodReply.Replace(
            "\"scope\": \"all-sessions\",", "\"scope\": { \"agent\": \"Codex\" },", StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Equal("ClaudeCode", reading.Proposal!.Scope.Agent);
    }

    /// <summary>
    /// THE MODEL NEVER CHOOSES THE AGENT SCOPE (fix round D, ruling D3). This test replaces one that
    /// blessed the opposite: with no session known, the old reading let "all-sessions" stand because the
    /// model wrote it, which is every agent chosen by the answer and not by the account. When the origin
    /// is not known there is no fact to pin the scope to, and the only honest answer is a refusal.
    /// </summary>
    [Fact]
    public void An_answer_whose_session_origin_is_not_known_is_refused_rather_than_scoped_by_the_model()
    {
        var reading = RuleDraftContract.Read(
            AGoodReply, TheSentence, Registry, Screen(ACapturedLimitScreen, RuleSessionOrigin.None));

        Assert.Null(reading.Proposal);
        Assert.Contains("which agent", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// TWO DISTINCT ORIGINS, ASSERTED AT THE FAR SIDE (fix round D, ruling D9). Every origin test above
    /// uses one agent value, so a contract that pinned every rule to "ClaudeCode" regardless of the
    /// session would have stayed green. Two agents, two machines, and the proposal has to carry EACH.
    /// </summary>
    [Theory]
    [InlineData("ClaudeCode", "SOREN_NORTH")]
    [InlineData("Codex", "SOREN_SOUTH")]
    public void The_agent_scope_is_the_origin_that_was_given_and_not_a_constant(string agent, string machine)
    {
        var screen = Screen(ACapturedLimitScreen, new RuleSessionOrigin(agent, machine));

        var prompt = RuleDraftContract.BuildDraftPrompt(OneTurn(TheSentence), Registry, screen);
        var reading = RuleDraftContract.Read(AGoodReply, TheSentence, Registry, screen);

        Assert.Contains($"running the agent {agent}", prompt, StringComparison.Ordinal);
        Assert.Contains(machine, prompt, StringComparison.Ordinal);
        Assert.Equal(agent, reading.Proposal!.Scope.Agent);
    }

    // ---- fix round D: a number that cannot be read is a refusal, never an exception (ruling D7) ----------

    [Fact]
    public void A_decimal_ceiling_is_refused_with_a_sentence_and_not_thrown()
    {
        var reply = AGoodReply.Replace("\"cooldown_seconds\": 600,", "\"cooldown_seconds\": 600.5,", StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.Contains("cooldown_seconds", reading.Refusal!, StringComparison.Ordinal);
        Assert.Contains("600.5", reading.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_out_of_range_ceiling_is_refused_with_a_sentence_and_not_thrown()
    {
        var reply = AGoodReply.Replace("\"daily_cap\": 4,", "\"daily_cap\": 99999999999,", StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.Contains("daily_cap", reading.Refusal!, StringComparison.Ordinal);
        Assert.Contains("99999999999", reading.Refusal!, StringComparison.Ordinal);
    }

    // ---- fix round D: the ceilings have real bounds, and the question says what they are (ruling D6) ----

    [Fact]
    public void The_question_states_the_bounds_of_the_ceilings()
    {
        var prompt = RuleDraftContract.BuildDraftPrompt(OneTurn(TheSentence), Registry, Screen(ACapturedLimitScreen));

        Assert.Contains("at least 60 seconds", prompt, StringComparison.Ordinal);
        Assert.Contains("at most 24 hours", prompt, StringComparison.Ordinal);
        Assert.Contains("at most 100", prompt, StringComparison.Ordinal);
    }

    // ---- the answer: a question ---------------------------------------------------------------------

    /// <summary>A model that does not know something asks, and that is a first-class answer. The
    /// alternative is a model that picks the widest scope it can and hands back a rule nobody asked
    /// for.</summary>
    [Fact]
    public void A_question_comes_back_as_a_question()
    {
        var reading = RuleDraftContract.Read(
            """{ "answer": "ask", "question": "Should this apply to every session, or only the ones in one repository?" }""",
            TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.Null(reading.Refusal);
        Assert.Equal("Should this apply to every session, or only the ones in one repository?", reading.Question);
    }

    [Fact]
    public void Saying_it_needs_something_and_then_asking_nothing_is_refused()
    {
        var reading = RuleDraftContract.Read(
            """{ "answer": "ask", "question": "  " }""", TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Question);
        Assert.Contains("asked nothing", reading.Refusal!, StringComparison.Ordinal);
    }

    // ---- the answer: not an answer at all -----------------------------------------------------------

    [Fact]
    public void No_answer_at_all_is_refused_and_is_never_a_rule()
    {
        var reading = RuleDraftContract.Read(null, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.Null(reading.Question);
        Assert.Contains("no answer at all", reading.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void Prose_with_no_answer_in_it_is_refused_and_quoted_back()
    {
        var reading = RuleDraftContract.Read(
            "Sure, I will watch for rate limits and handle it.", TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.Contains("Sure, I will watch for rate limits", reading.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_outside_the_two_there_are_is_refused()
    {
        var reading = RuleDraftContract.Read("""{ "answer": "maybe" }""", TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.Contains("maybe", reading.Refusal!, StringComparison.Ordinal);
        Assert.Contains(RuleDraftAnswers.Propose, reading.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void Broken_json_is_refused_and_never_partly_read()
    {
        var reading = RuleDraftContract.Read(
            """{ "answer": "propose", "trigger_words": [ """, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.NotNull(reading.Refusal);
    }

    /// <summary>There is no reading without a screen. The parameter is not optional and null is an
    /// argument error, not a mode.</summary>
    [Fact]
    public void There_is_no_reading_without_a_screen()
    {
        Assert.Throws<ArgumentNullException>(() => RuleDraftContract.Read(AGoodReply, TheSentence, Registry, null!));
        Assert.Throws<ArgumentNullException>(() => RuleDraftContract.BuildDraftPrompt(OneTurn(TheSentence), Registry, null!));
    }


    // ---- phase 1: the text the rule will type is decided here, and shown -----------------------------

    /// <summary>The question asks for the exact text the rule will type, as its own field, because the
    /// run-time call no longer composes one: it is a yes/no question, and it types what was stored.</summary>
    [Fact]
    public void The_question_asks_for_the_exact_text_the_rule_will_type()
    {
        var prompt = RuleDraftContract.BuildDraftPrompt(OneTurn(TheSentence), Registry, Screen(ACapturedLimitScreen));

        Assert.Contains("\"type\":", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("composes text", prompt, StringComparison.Ordinal);
    }

    /// <summary>A proposal that does not say what it types is not a rule anybody can confirm - the
    /// keystroke is the consequential part - and nothing at run time will fill it in.</summary>
    [Fact]
    public void A_proposal_that_does_not_say_what_it_types_is_refused()
    {
        var reply = AGoodReply.Replace("\"type\": \"/model opus\",", "", StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\"", reply, StringComparison.Ordinal);

        var reading = RuleDraftContract.Read(reply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Null(reading.Proposal);
        Assert.Contains("type", reading.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_proposed_rule_carries_the_text_it_will_type()
    {
        var reading = RuleDraftContract.Read(AGoodReply, TheSentence, Registry, Screen(ACapturedLimitScreen));

        Assert.Equal("/model opus", reading.Proposal!.TextToType);
    }
}
