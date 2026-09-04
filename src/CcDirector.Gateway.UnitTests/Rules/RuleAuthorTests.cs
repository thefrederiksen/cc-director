using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// MAKING A RULE BY TALKING, end to end short of the network: what somebody said goes in, the Gateway
/// reads the named session's screen, a rule to confirm comes out, and posting that rule back is a real
/// write into the real store - through the same grounding check the write route runs.
///
/// The round-trip tests are the ones that matter, and they are here rather than in the parked host-bound
/// suite on purpose. A drafted rule that looks right and that the writing route would then refuse is the
/// worst outcome this feature has: somebody would have read a rule, agreed to it, and been told no
/// afterwards. So the proposal is projected exactly as the route projects it, read back by exactly the
/// readers the writing route uses, re-grounded by exactly the method the writing route calls, and
/// written to a real migrated database.
///
/// Every model answer here is a canned string and every screen is a canned reading. That proves the PATH
/// carries a rule, not that a live model writes good ones - which is a separate claim and is not made
/// anywhere in this file.
/// </summary>
public sealed class RuleAuthorTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);

    private static readonly RuleSessionOrigin ClaudeOnNorth = new("ClaudeCode", "SOREN_NORTH");

    private const string TheSession = "sid-1";

    /// <summary>What a limit screen shows; every word the allowance reply watches for is on it.</summary>
    private const string TheLimitScreen = """
    > carry on with the refactor

    Claude usage limit reached. Your limit will reset at 11:50pm. out of credits.

    >
    """;

    /// <summary>What an outage screen shows; every word the outage reply watches for is on it.</summary>
    private const string TheOutageScreen = """
    > carry on

    API Error: 529 overloaded. connection error. internal server error.

    >
    """;

    /// <summary>A screen reader that answers this reading for every session and every tenant.</summary>
    private static RuleScreenReader Showing(string screen, RuleSessionOrigin? origin = null) =>
        (_, sid, _) => Task.FromResult(RuleScreenResult.Read(new RuleScreenReading(sid, origin ?? ClaudeOnNorth, screen)));

    /// <summary>An author whose model always says this, looking at this screen.</summary>
    private static RuleAuthor AuthorSaying(string? reply, string screen = TheLimitScreen, RuleSessionOrigin? origin = null) =>
        new((_, _, _) => Task.FromResult(reply), Showing(screen, origin));

    private static IReadOnlyList<RuleDraftTurn> Said(params string[] words) =>
        words.Select(w => new RuleDraftTurn(RuleDraftSpeakers.Person, w)).ToList();

    private static Task<RuleDraftReading> Draft(RuleAuthor author, IReadOnlyList<RuleDraftTurn> turns, bool allAgents = false) =>
        author.DraftAsync(TenantId.Local, turns, TheSession, allAgents, CancellationToken.None);

    private const string TheAllowanceSentence =
        "When a session runs out of its allowance, switch it to another model and carry on.";

    private const string AnAllowanceReply = """
    {
      "answer": "propose",
      "screen_description": "The session has stopped on a notice that the account is out of allowance.",
      "trigger_words": ["usage limit", "out of credits"],
      "checks": [ { "name": "matches_any", "arguments": { "text": "<screen_text>", "terms": ["usage limit"] } } ],
      "scope": "all-sessions",
      "cooldown_seconds": 600,
      "daily_cap": 4,
      "read_back": "When one of your sessions stops on an allowance notice I will switch it to another model and tell it to carry on."
    }
    """;

    // ---- what goes in ------------------------------------------------------------------------------

    /// <summary>A conversation in which the person said nothing is not a rule waiting to be written. It
    /// is refused, because the instruction is the sentence they said and there is not one.</summary>
    [Fact]
    public async Task Nothing_the_person_said_is_refused_rather_than_drafted()
    {
        var reading = await Draft(AuthorSaying(AnAllowanceReply),
            new[] { new RuleDraftTurn(RuleDraftSpeakers.DevThrottle, "Which sessions?") });

        Assert.Null(reading.Proposal);
        Assert.Contains("nothing to turn into one", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A MODEL THAT CANNOT BE ASKED PRODUCES A REFUSAL AND NEVER A RULE. The asking seam answers null when
    /// the model could not be reached, and there is no partial rule, no default rule and nothing assembled
    /// out of what could be read.
    /// </summary>
    [Fact]
    public async Task A_model_that_cannot_be_asked_produces_a_refusal_and_never_a_rule()
    {
        var reading = await Draft(AuthorSaying(null), Said(TheAllowanceSentence));

        Assert.Null(reading.Proposal);
        Assert.Null(reading.Question);
        Assert.Contains("no answer at all", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// RUNNING OUT OF TIME IS NOT ANSWERING NOTHING, and the person is told which. Measured against the
    /// hosted model on 3 September 2026: the same sentence asked five times ran out of the sixty-second
    /// limit three times and answered twice. Someone who waits a minute and is then told "the model gave
    /// no answer at all" is being told something true about the call and misleading about what to do
    /// next - the answer is to try again, and the refusal has to say so.
    /// </summary>
    [Fact]
    public async Task A_model_that_ran_out_of_time_says_so_and_says_to_try_again()
    {
        var author = new RuleAuthor(
            (_, _, _) => Task.FromException<string?>(new TimeoutException("The wingman model call did not answer within 60 seconds.")),
            Showing(TheLimitScreen));

        var reading = await Draft(author, Said(TheAllowanceSentence));

        Assert.Null(reading.Proposal);
        Assert.Contains("longer than the model is given", reading.Refusal!, StringComparison.Ordinal);
        Assert.Contains("worth trying again", reading.Refusal!, StringComparison.Ordinal);

        // And it is NOT the "answered nothing" sentence - the two events have to be told apart by the
        // person reading them, not only in the log.
        Assert.DoesNotContain("no answer at all", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>Any other failure to reach the model is stated too, carrying what went wrong.</summary>
    [Fact]
    public async Task A_model_that_could_not_be_reached_at_all_says_what_went_wrong()
    {
        var author = new RuleAuthor(
            (_, _, _) => Task.FromException<string?>(new HttpRequestException("no such host is known")),
            Showing(TheLimitScreen));

        var reading = await Draft(author, Said(TheAllowanceSentence));

        Assert.Null(reading.Proposal);
        Assert.Contains("could not be asked", reading.Refusal!, StringComparison.Ordinal);
        Assert.Contains("no such host is known", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>Everything the person said becomes the instruction, in their own words and in order - a
    /// conversation is still one instruction.</summary>
    [Fact]
    public async Task The_instruction_is_everything_the_person_said_in_their_own_words()
    {
        var reading = await Draft(AuthorSaying(AnAllowanceReply), new[]
        {
            new RuleDraftTurn(RuleDraftSpeakers.Person, TheAllowanceSentence),
            new RuleDraftTurn(RuleDraftSpeakers.DevThrottle, "Which sessions should this apply to?"),
            new RuleDraftTurn(RuleDraftSpeakers.Person, "All of them."),
        });

        Assert.Equal(TheAllowanceSentence + " All of them.", reading.Proposal!.Instruction);
    }

    // ---- the screen is the Gateway's own reading (fix round D, ruling D2) -------------------------------

    /// <summary>
    /// AUTHORING FROM MEMORY IS NOT A MODE. A request that names no session is refused - it does not fall
    /// back to a prompt with no screen, because that is the path on which the trigger words were a guess.
    /// The model is never even asked.
    /// </summary>
    [Fact]
    public async Task Naming_no_session_is_refused_and_the_model_is_never_asked()
    {
        var asked = 0;
        var author = new RuleAuthor(
            (_, _, _) => { asked++; return Task.FromResult<string?>(AnAllowanceReply); },
            Showing(TheLimitScreen));

        var reading = await author.DraftAsync(TenantId.Local, Said(TheAllowanceSentence), "", false, CancellationToken.None);

        Assert.Null(reading.Proposal);
        Assert.Contains("named no session", reading.Refusal!, StringComparison.Ordinal);
        Assert.Equal(0, asked);
    }

    /// <summary>The screen reader's own refusal - the session is not on the roster, its machine is not
    /// connected - is the refusal the person reads, and the model is never asked.</summary>
    [Fact]
    public async Task A_screen_that_cannot_be_read_is_the_refusal_and_the_model_is_never_asked()
    {
        var asked = 0;
        var author = new RuleAuthor(
            (_, _, _) => { asked++; return Task.FromResult<string?>(AnAllowanceReply); },
            (_, sid, _) => Task.FromResult(RuleScreenResult.Refused($"session {sid} is not on this account's roster.")));

        var reading = await Draft(author, Said(TheAllowanceSentence));

        Assert.Null(reading.Proposal);
        Assert.Contains("sid-1 is not on this account's roster", reading.Refusal!, StringComparison.Ordinal);
        Assert.Equal(0, asked);
    }

    /// <summary>An empty screen is not a capture. Refused, not sent to the model to guess from.</summary>
    [Fact]
    public async Task An_empty_screen_is_refused_rather_than_written_from()
    {
        var reading = await Draft(AuthorSaying(AnAllowanceReply, screen: "   \n\n  "), Said(TheAllowanceSentence));

        Assert.Null(reading.Proposal);
        Assert.Contains("empty screen", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>A session whose roster row names no agent gives the Gateway no fact to scope the rule to,
    /// and the model must never choose that (ruling D3) - so it is refused.</summary>
    [Fact]
    public async Task A_session_with_no_known_agent_is_refused_rather_than_letting_the_model_choose()
    {
        var reading = await Draft(AuthorSaying(AnAllowanceReply, origin: RuleSessionOrigin.None), Said(TheAllowanceSentence));

        Assert.Null(reading.Proposal);
        Assert.Contains("which agent", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>The proposal carries the session it was grounded in and the excerpt it was checked
    /// against, so the write route can run the same check again from the body alone.</summary>
    [Fact]
    public async Task The_proposal_names_the_session_it_was_grounded_in()
    {
        var reading = await Draft(AuthorSaying(AnAllowanceReply), Said(TheAllowanceSentence));

        Assert.Equal(TheSession, reading.Proposal!.SessionId);
        Assert.Equal(RuleScreenExcerpt.Of(TheLimitScreen), reading.Proposal!.ExampleScreen);
    }

    // ---- two accounts, two tenants (fix round D, ruling D9) -------------------------------------------

    /// <summary>
    /// TWO DISTINCT ACCOUNTS REACH THE MODEL AND THE ROSTER AS THEMSELVES. Every other test in this file
    /// asks as TenantId.Local, so an author that substituted a constant tenant at either seam would have
    /// stayed green - and on the hosted Gateway a tenant constant selects the wrong account's model
    /// configuration, or reads a session off the wrong account's roster. Both seams RECORD the tenant
    /// they were asked as, and two different accounts have to arrive as two different tenants at both.
    /// </summary>
    [Fact]
    public async Task Two_accounts_reach_the_model_and_the_roster_as_two_different_tenants_and_not_as_a_constant()
    {
        var askedAs = new List<TenantId>();
        var readAs = new List<TenantId>();
        var author = new RuleAuthor(
            (tenant, _, _) => { askedAs.Add(tenant); return Task.FromResult<string?>(AnAllowanceReply); },
            (tenant, sid, _) =>
            {
                readAs.Add(tenant);
                return Task.FromResult(RuleScreenResult.Read(new RuleScreenReading(sid, ClaudeOnNorth, TheLimitScreen)));
            });
        var accountA = new TenantId("tenant-a-fix-round-d");
        var accountB = new TenantId("tenant-b-fix-round-d");

        await author.DraftAsync(accountA, Said(TheAllowanceSentence), TheSession, false, CancellationToken.None);
        await author.DraftAsync(accountB, Said(TheAllowanceSentence), TheSession, false, CancellationToken.None);

        Assert.Equal(new[] { accountA, accountB }, askedAs);
        Assert.Equal(new[] { accountA, accountB }, readAs);
        Assert.NotEqual(accountA, accountB);
    }

    // ---- the gate, run before anybody is asked to agree ---------------------------------------------

    /// <summary>
    /// A rule the writing route would refuse is never offered, and the refusal is the STORE'S OWN WORDS.
    /// This is the one implementation of what a rule is being asked the question early, so nobody can
    /// confirm a rule that then turns out not to exist.
    /// </summary>
    [Fact]
    public async Task A_rule_the_store_would_refuse_is_not_offered_and_says_the_stores_reason()
    {
        var noTriggerWords = AnAllowanceReply.Replace(
            "\"trigger_words\": [\"usage limit\", \"out of credits\"],", "\"trigger_words\": [],",
            StringComparison.Ordinal);

        var reading = await Draft(AuthorSaying(noTriggerWords), Said(TheAllowanceSentence));

        Assert.Null(reading.Proposal);
        Assert.Contains("at least one word to watch for", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>The same for a ceiling that is not a ceiling. A rule with no cooldown is a rule that can
    /// act on one session without limit, and the store refuses it - so it is refused here first.</summary>
    [Fact]
    public async Task A_rule_with_no_cooldown_is_not_offered()
    {
        var noCooldown = AnAllowanceReply.Replace(
            "\"cooldown_seconds\": 600,", "\"cooldown_seconds\": 0,", StringComparison.Ordinal);

        var reading = await Draft(AuthorSaying(noCooldown), Said(TheAllowanceSentence));

        Assert.Null(reading.Proposal);
        Assert.Contains("how long to wait before acting on the same session again",
            reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>And a ceiling outside the Architect's bounds (ruling D6) is refused before it is offered,
    /// naming the value and the bound.</summary>
    [Fact]
    public async Task A_rule_whose_ceiling_is_outside_the_bounds_is_not_offered()
    {
        var oneSecond = AnAllowanceReply.Replace(
            "\"cooldown_seconds\": 600,", "\"cooldown_seconds\": 1,", StringComparison.Ordinal);

        var reading = await Draft(AuthorSaying(oneSecond), Said(TheAllowanceSentence));

        Assert.Null(reading.Proposal);
        Assert.Contains("1 seconds is outside the bounds", reading.Refusal!, StringComparison.Ordinal);
        Assert.Contains("at least 60 seconds", reading.Refusal!, StringComparison.Ordinal);
    }

    // ---- the round trip: a drafted rule is a rule the writing route takes ---------------------------

    /// <summary>
    /// The drafted rule projected exactly as the draft route projects it, read back by exactly the readers
    /// the writing route uses, RE-GROUNDED by exactly the method the writing route calls, and written to
    /// the real store. This is the join between the two halves, and it is the join where a scope or a
    /// check could silently become something else.
    /// </summary>
    private async Task<SessionRule> StoreAsync(RuleAuthor author, RuleProposal proposal)
    {
        var projected = JsonSerializer.SerializeToElement(SessionRuleWire.Project(proposal));
        var body = projected.GetProperty("rule");

        var words = SessionRuleWire.Strings(body, "triggerWords");
        var scope = SessionRuleWire.ReadScope(body);
        // THE GROUNDED ROUTE, exactly as the write route runs it: the author reads the screen again and
        // mints the evidence the store demands. This is the CONTROL for the store's invariant (fix round
        // E, ruling E1) - the refusal tests in the store's own file could all pass on a store that refused
        // everything, and this is what says it does not.
        var grounding = await author.GroundAsync(
            TenantId.Local,
            RuleCallJson.Text(body, "sessionId"),
            words,
            scope,
            SessionRuleWire.Flag(body, "allAgents"),
            CancellationToken.None);
        Assert.Null(grounding.Refusal);
        Assert.NotNull(grounding.Evidence);

        return new SessionRuleStore(_h.Open()).Create(
            RuleCallJson.Text(body, "instruction") ?? "",
            RuleCallJson.Text(body, "screenDescription") ?? "",
            words,
            SessionRuleWire.Calls(body),
            scope,
            SessionRuleWire.Number(body, "cooldownSeconds"),
            SessionRuleWire.Number(body, "dailyCap"),
            Now,
            grounding.Evidence);
    }

    [Fact]
    public async Task A_drafted_rule_is_stored_by_the_writing_route_with_every_part_intact()
    {
        var author = AuthorSaying(AnAllowanceReply);
        var reading = await Draft(author, Said(TheAllowanceSentence));

        var stored = await StoreAsync(author, reading.Proposal!);

        Assert.Equal(TheAllowanceSentence, stored.Instruction);
        Assert.Equal("The session has stopped on a notice that the account is out of allowance.", stored.ScreenDescription);
        Assert.Equal(new[] { "usage limit", "out of credits" }, stored.TriggerWords);
        // The model said every session; the rule is for the session's agent, pinned by the Gateway.
        Assert.Equal(new RuleScope("ClaudeCode", null, null, null), stored.Scope);
        Assert.Equal(600, stored.CooldownSeconds);
        Assert.Equal(4, stored.DailyCap);
        Assert.Equal("matches_any(text=<screen_text>, terms=usage limit)", Assert.Single(stored.Calls).Describe());
    }

    /// <summary>The star survives the round trip: the account said every agent, the proposal carries it,
    /// and the write route holds the scope to it rather than pinning the agent back on.</summary>
    [Fact]
    public async Task A_rule_for_every_agent_survives_the_round_trip_as_every_session()
    {
        var author = AuthorSaying(AnAllowanceReply);
        var reading = await Draft(author, Said(TheAllowanceSentence), allAgents: true);

        var stored = await StoreAsync(author, reading.Proposal!);

        Assert.Equal(RuleScope.AllSessions, stored.Scope);
    }

    /// <summary>A rule made by talking is in DRY RUN like every other rule. Talking to it is a way to
    /// write a rule, never a way to skip the person who makes one live.</summary>
    [Fact]
    public async Task A_rule_made_by_talking_is_in_dry_run_like_every_other_rule()
    {
        var author = AuthorSaying(AnAllowanceReply);
        var reading = await Draft(author, Said(TheAllowanceSentence));

        var stored = await StoreAsync(author, reading.Proposal!);

        Assert.Equal(RuleState.DryRun, stored.State);
        Assert.Equal("", stored.PromotedBy);
        Assert.Equal("", stored.Acknowledgement);
    }

    /// <summary>
    /// A NARROWER SCOPE SURVIVES THE ROUND TRIP, with the parts that were not set still meaning "any" -
    /// except the agent, which is the session's.
    /// </summary>
    [Fact]
    public async Task A_rule_scoped_to_one_repository_survives_the_round_trip()
    {
        var oneRepository = AnAllowanceReply.Replace(
            "\"scope\": \"all-sessions\",",
            "\"scope\": { \"repository\": \"D:\\\\ReposFred\\\\devthrottle\" },",
            StringComparison.Ordinal);
        var author = AuthorSaying(oneRepository);
        var reading = await Draft(author, Said(TheAllowanceSentence));

        var stored = await StoreAsync(author, reading.Proposal!);

        Assert.Equal(@"D:\ReposFred\devthrottle", stored.Scope.Repository);
        Assert.Equal("ClaudeCode", stored.Scope.Agent);
        Assert.Null(stored.Scope.Machine);
        Assert.Null(stored.Scope.Mission);
    }

    /// <summary>
    /// A SCOPE THAT SAYS NOTHING IS REFUSED EVEN WHEN IT SAYS IT WITH WORDS. The writing route already
    /// refuses an omitted scope and an empty object, on the stated grounds that the widest possible value
    /// is the one an omission must never quietly become. An object whose four parts are all null was
    /// getting through that: each null was read as an empty string, so the object was not equal to the
    /// empty one, it was accepted as a NARROW scope of four empty strings, and the store then blanked all
    /// four back to null - producing a rule that acts on every session the account has, from a request
    /// that chose nothing.
    ///
    /// It matters more now than it did, because the thing filling that object in is a model. This is the
    /// exact shape a model produces when it does not know the answer and fills the fields in anyway.
    /// </summary>
    [Fact]
    public async Task A_scope_whose_parts_are_all_null_is_refused_rather_than_read_as_every_session()
    {
        var nullScope = AnAllowanceReply.Replace(
            "\"scope\": \"all-sessions\",",
            "\"scope\": { \"agent\": null, \"repository\": null, \"machine\": null, \"mission\": null },",
            StringComparison.Ordinal);

        var reading = await Draft(AuthorSaying(nullScope), Said(TheAllowanceSentence));

        Assert.Null(reading.Proposal);
        Assert.Contains("which sessions", reading.Refusal!, StringComparison.Ordinal);
    }

    // ---- the write gate's half of grounding (fix round D, ruling D2, item 5) ---------------------------

    /// <summary>
    /// THE WRITE ROUTE RE-READS THE SCREEN AND RUNS THE SAME CHECK. A body whose trigger word is not on
    /// the session's screen NOW is refused at the gate, whatever the draft route said earlier - so a
    /// hand-edited proposal, or a caller that skipped the draft route entirely, cannot store an
    /// ungrounded word.
    /// </summary>
    [Fact]
    public async Task The_write_gate_refuses_a_trigger_word_that_is_not_on_the_sessions_screen_now()
    {
        var author = AuthorSaying(AnAllowanceReply);

        var refusal = await author.WhyNotGroundedAsync(
            TenantId.Local, TheSession, new[] { "usage limit", "ECONNREFUSED" },
            new RuleScope("ClaudeCode", null, null, null), false, CancellationToken.None);

        Assert.NotNull(refusal);
        Assert.Contains("ECONNREFUSED", refusal!, StringComparison.Ordinal);
        Assert.Contains("that session's screen right now", refusal!, StringComparison.Ordinal);
        Assert.Contains("Nothing was stored", refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A RULE THAT WATCHES FOR NOTHING IS NOT GROUNDED (fix round F, ruling F3). Grounding asks whether
    /// every trigger word is on the screen, and over an empty list that question has no work to do, so
    /// the check used to answer yes - a pass condition that is an absence, in the one function that
    /// defines what grounding means for this feature. Two later gates refused the rule anyway, which is
    /// exactly the backstop this mission has now twice watched fail.
    /// </summary>
    [Fact]
    public async Task The_write_gate_refuses_a_rule_that_watches_for_nothing()
    {
        var refusal = await AuthorSaying(AnAllowanceReply).WhyNotGroundedAsync(
            TenantId.Local, TheSession, Array.Empty<string>(), RuleScope.AllSessions, true,
            CancellationToken.None);

        Assert.NotNull(refusal);
        Assert.Contains("watches for nothing", refusal!, StringComparison.Ordinal);
        Assert.Contains("Nothing was stored", refusal!, StringComparison.Ordinal);
    }

    /// <summary>The write gate names no session: refused, with the same sentence the draft route uses.</summary>
    [Fact]
    public async Task The_write_gate_refuses_a_body_that_names_no_session()
    {
        var refusal = await AuthorSaying(AnAllowanceReply).WhyNotGroundedAsync(
            TenantId.Local, "", new[] { "usage limit" }, RuleScope.AllSessions, true, CancellationToken.None);

        Assert.Contains("named no session", refusal!, StringComparison.Ordinal);
    }

    /// <summary>The agent scope at the gate is the session's agent, or lifted by the star - never a hand
    /// written one. A body naming a different agent did not come from the draft unchanged.</summary>
    [Theory]
    [InlineData("Codex", false)]
    [InlineData("", false)]
    [InlineData("ClaudeCode", true)]
    public async Task The_write_gate_refuses_an_agent_scope_that_is_not_the_sessions_or_the_star(string agentWritten, bool allAgents)
    {
        var refusal = await AuthorSaying(AnAllowanceReply).WhyNotGroundedAsync(
            TenantId.Local, TheSession, new[] { "usage limit" },
            new RuleScope(agentWritten.Length == 0 ? null : agentWritten, null, null, null), allAgents,
            CancellationToken.None);

        Assert.NotNull(refusal);
        Assert.Contains("Nothing was stored", refusal!, StringComparison.Ordinal);
    }

    /// <summary>And the gate LETS a grounded body through - it is a gate, not a wall. Both shapes: the
    /// session's agent, and the star with the agent lifted.</summary>
    [Theory]
    [InlineData("ClaudeCode", false)]
    [InlineData("", true)]
    public async Task The_write_gate_lets_a_grounded_body_through(string agentWritten, bool allAgents)
    {
        var refusal = await AuthorSaying(AnAllowanceReply).WhyNotGroundedAsync(
            TenantId.Local, TheSession, new[] { " usage limit ", "OUT OF CREDITS" },
            new RuleScope(agentWritten.Length == 0 ? null : agentWritten, null, null, null), allAgents,
            CancellationToken.None);

        Assert.Null(refusal);
    }

    // ---- the language is not shaped around one kind of trouble --------------------------------------

    /// <summary>
    /// THE OWNER'S SECOND CASE, EXPRESSED IN THE SAME LANGUAGE: a provider that stops working, which wants
    /// a wait and a restart rather than a switch to another model. It is a different trigger and a
    /// different act, and it has to go through the same authoring path without anything being widened for
    /// it.
    ///
    /// The waiting is the COOLDOWN. A rule that should leave a session alone for a while and then try
    /// again is a rule with a long cooldown and a low daily cap, and both are already part of what a rule
    /// is - so "wait, then start it back up" needs no new machinery at all.
    ///
    /// WHAT THIS DOES NOT PROVE, and it is the part the owner should read: this is a rule that fires on
    /// WORDS AN OUTAGE PUTS ON THE SCREEN. An outage that leaves a session hung mid-turn, or that shows
    /// nothing at all, is not reachable by any rule, because a rule only ever looks at the screen of a
    /// session that has gone idle.
    /// </summary>
    [Fact]
    public async Task A_rule_about_a_provider_that_stopped_working_goes_through_the_same_path()
    {
        const string outageSentence =
            "When the provider stops working, wait a while and then start the session back up.";
        const string outageReply = """
        {
          "answer": "propose",
          "screen_description": "The session has stopped on an error from the provider's own interface rather than on any work of its own.",
          "trigger_words": ["API Error", "overloaded", "connection error", "internal server error"],
          "checks": [],
          "scope": "all-sessions",
          "cooldown_seconds": 900,
          "daily_cap": 6,
          "read_back": "When one of your sessions stops on a provider error I will wait fifteen minutes and then tell it to carry on, at most six times a day for any one session."
        }
        """;
        var author = AuthorSaying(outageReply, screen: TheOutageScreen);
        var reading = await Draft(author, Said(outageSentence));

        var stored = await StoreAsync(author, reading.Proposal!);

        Assert.Equal(outageSentence, stored.Instruction);
        Assert.Contains("API Error", stored.TriggerWords);
        Assert.Equal(900, stored.CooldownSeconds);
        Assert.Equal(6, stored.DailyCap);

        // A rule that stakes nothing on a check is a real rule: none is a statement, not an omission.
        Assert.Empty(stored.Calls);
    }
}
