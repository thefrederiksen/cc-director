using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// MAKING A RULE BY TALKING, end to end short of the network: what somebody said goes in, a rule to
/// confirm comes out, and posting that rule back is a real write into the real store.
///
/// The round-trip tests are the ones that matter, and they are here rather than in the parked host-bound
/// suite on purpose. A drafted rule that looks right and that the writing route would then refuse is the
/// worst outcome this feature has: somebody would have read a rule, agreed to it, and been told no
/// afterwards. So the proposal is projected exactly as the route projects it, read back by exactly the
/// readers the writing route uses, and written to a real migrated database.
///
/// Every model answer here is a canned string. That proves the PATH carries a rule, not that a live model
/// writes good ones - which is a separate claim and is not made anywhere in this file.
/// </summary>
public sealed class RuleAuthorTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>An author whose model always says this.</summary>
    private static RuleAuthor AuthorSaying(string? reply) =>
        new((_, _, _) => Task.FromResult(reply));

    private static IReadOnlyList<RuleDraftTurn> Said(params string[] words) =>
        words.Select(w => new RuleDraftTurn(RuleDraftSpeakers.Person, w)).ToList();

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
        var reading = await AuthorSaying(AnAllowanceReply).DraftAsync(
            TenantId.Local,
            new[] { new RuleDraftTurn(RuleDraftSpeakers.DevThrottle, "Which sessions?") },
            CancellationToken.None);

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
        var reading = await AuthorSaying(null).DraftAsync(
            TenantId.Local, Said(TheAllowanceSentence), CancellationToken.None);

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
        var author = new RuleAuthor((_, _, _) =>
            Task.FromException<string?>(new TimeoutException("The wingman model call did not answer within 60 seconds.")));

        var reading = await author.DraftAsync(
            TenantId.Local, Said(TheAllowanceSentence), CancellationToken.None);

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
        var author = new RuleAuthor((_, _, _) =>
            Task.FromException<string?>(new HttpRequestException("no such host is known")));

        var reading = await author.DraftAsync(
            TenantId.Local, Said(TheAllowanceSentence), CancellationToken.None);

        Assert.Null(reading.Proposal);
        Assert.Contains("could not be asked", reading.Refusal!, StringComparison.Ordinal);
        Assert.Contains("no such host is known", reading.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>Everything the person said becomes the instruction, in their own words and in order - a
    /// conversation is still one instruction.</summary>
    [Fact]
    public async Task The_instruction_is_everything_the_person_said_in_their_own_words()
    {
        var reading = await AuthorSaying(AnAllowanceReply).DraftAsync(
            TenantId.Local,
            new[]
            {
                new RuleDraftTurn(RuleDraftSpeakers.Person, TheAllowanceSentence),
                new RuleDraftTurn(RuleDraftSpeakers.DevThrottle, "Which sessions should this apply to?"),
                new RuleDraftTurn(RuleDraftSpeakers.Person, "All of them."),
            },
            CancellationToken.None);

        Assert.Equal(TheAllowanceSentence + " All of them.", reading.Proposal!.Instruction);
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

        var reading = await AuthorSaying(noTriggerWords).DraftAsync(
            TenantId.Local, Said(TheAllowanceSentence), CancellationToken.None);

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

        var reading = await AuthorSaying(noCooldown).DraftAsync(
            TenantId.Local, Said(TheAllowanceSentence), CancellationToken.None);

        Assert.Null(reading.Proposal);
        Assert.Contains("how long to wait before acting on the same session again",
            reading.Refusal!, StringComparison.Ordinal);
    }

    // ---- the round trip: a drafted rule is a rule the writing route takes ---------------------------

    /// <summary>
    /// The drafted rule projected exactly as the draft route projects it, read back by exactly the readers
    /// the writing route uses, and written to the real store. This is the join between the two halves, and
    /// it is the join where a scope or a check could silently become something else.
    /// </summary>
    private SessionRule Store(RuleProposal proposal)
    {
        var projected = JsonSerializer.SerializeToElement(SessionRuleWire.Project(proposal));
        var body = projected.GetProperty("rule");

        return new SessionRuleStore(_h.Open()).Create(
            RuleCallJson.Text(body, "instruction") ?? "",
            RuleCallJson.Text(body, "screenDescription") ?? "",
            SessionRuleWire.Strings(body, "triggerWords"),
            SessionRuleWire.Calls(body),
            SessionRuleWire.ReadScope(body),
            SessionRuleWire.Number(body, "cooldownSeconds"),
            SessionRuleWire.Number(body, "dailyCap"),
            Now);
    }

    [Fact]
    public async Task A_drafted_rule_is_stored_by_the_writing_route_with_every_part_intact()
    {
        var reading = await AuthorSaying(AnAllowanceReply).DraftAsync(
            TenantId.Local, Said(TheAllowanceSentence), CancellationToken.None);

        var stored = Store(reading.Proposal!);

        Assert.Equal(TheAllowanceSentence, stored.Instruction);
        Assert.Equal("The session has stopped on a notice that the account is out of allowance.", stored.ScreenDescription);
        Assert.Equal(new[] { "usage limit", "out of credits" }, stored.TriggerWords);
        Assert.Equal(RuleScope.AllSessions, stored.Scope);
        Assert.Equal(600, stored.CooldownSeconds);
        Assert.Equal(4, stored.DailyCap);
        Assert.Equal("matches_any(text=<screen_text>, terms=usage limit)", Assert.Single(stored.Calls).Describe());
    }

    /// <summary>A rule made by talking is in DRY RUN like every other rule. Talking to it is a way to
    /// write a rule, never a way to skip the person who makes one live.</summary>
    [Fact]
    public async Task A_rule_made_by_talking_is_in_dry_run_like_every_other_rule()
    {
        var reading = await AuthorSaying(AnAllowanceReply).DraftAsync(
            TenantId.Local, Said(TheAllowanceSentence), CancellationToken.None);

        var stored = Store(reading.Proposal!);

        Assert.Equal(RuleState.DryRun, stored.State);
        Assert.Equal("", stored.PromotedBy);
    }

    /// <summary>
    /// A NARROWER SCOPE SURVIVES THE ROUND TRIP, with the parts that were not set still meaning "any".
    /// </summary>
    [Fact]
    public async Task A_rule_scoped_to_one_repository_survives_the_round_trip()
    {
        var oneRepository = AnAllowanceReply.Replace(
            "\"scope\": \"all-sessions\",",
            "\"scope\": { \"repository\": \"D:\\\\ReposFred\\\\devthrottle\" },",
            StringComparison.Ordinal);

        var reading = await AuthorSaying(oneRepository).DraftAsync(
            TenantId.Local, Said(TheAllowanceSentence), CancellationToken.None);

        var stored = Store(reading.Proposal!);

        Assert.Equal(@"D:\ReposFred\devthrottle", stored.Scope.Repository);
        Assert.Null(stored.Scope.Agent);
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

        var reading = await AuthorSaying(nullScope).DraftAsync(
            TenantId.Local, Said(TheAllowanceSentence), CancellationToken.None);

        Assert.Null(reading.Proposal);
        Assert.Contains("which sessions", reading.Refusal!, StringComparison.Ordinal);
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

        var reading = await AuthorSaying(outageReply).DraftAsync(
            TenantId.Local, Said(outageSentence), CancellationToken.None);

        var stored = Store(reading.Proposal!);

        Assert.Equal(outageSentence, stored.Instruction);
        Assert.Contains("API Error", stored.TriggerWords);
        Assert.Equal(900, stored.CooldownSeconds);
        Assert.Equal(6, stored.DailyCap);

        // A rule that stakes nothing on a check is a real rule: none is a statement, not an omission.
        Assert.Empty(stored.Calls);
    }
}
