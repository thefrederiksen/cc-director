using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// WHAT THE ACCOUNT ACTUALLY RECEIVES.
///
/// The rule surface's projections lived inside the route lambdas, where only a host-bound test could reach
/// them - and the host-bound suite is parked, so in practice nothing tested them at all. Two fields added
/// specifically to make an action accountable were simply absent from the only read surface there is:
/// WHO promoted a rule out of dry run, and WHAT checking the stated reason against the screen found. The
/// record existed in storage and was not delivered, which for the reader is the same as not existing.
///
/// And the write side read its checks leniently: a missing property, an object, a number all became "no
/// checks", and a non-object entry inside the list was silently dropped. The endpoint's own comment said
/// both paths used the same reader and preserved one meaning. They did not.
/// </summary>
public sealed class SessionRuleWireTests
{
    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static SessionRule ALiveRule(string promotedBy) => new(
        Guid.NewGuid(),
        "When I run out of allowance, switch me to Opus.",
        "A session stopped on a provider allowance notice.",
        "/model opus",
        new[] { "limit" },
        Array.Empty<RulePrimitiveCall>(),
        RuleScope.AllSessions,
        300,
        5,
        RuleState.Live,
        promotedBy,
        Now,
        Now);

    private static SessionRuleFiring AFiring(string grounding) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "sid-1",
        Now,
        "You've reached your Fable 5 limit.",
        "The session is blocked on its allowance.",
        RuleDecisions.Act,
        "The screen says 'reached your Fable 5 limit'.",
        Array.Empty<RulePrimitiveRun>(),
        "/usage-credits",
        "typed into the session: /usage-credits",
        grounding);

    /// <summary>The projection as JSON, which is what the account really gets - not the anonymous object.</summary>
    private static JsonElement AsWire(object projected) =>
        JsonDocument.Parse(JsonSerializer.Serialize(projected)).RootElement;

    [Fact]
    public void A_live_rule_says_who_promoted_it()
    {
        var wire = AsWire(SessionRuleWire.Project(ALiveRule("device-9f2c")));

        Assert.True(wire.TryGetProperty("promotedBy", out var who),
            "the rule the account reads does not say who moved it out of dry run, so the field that was " +
            "added to make a live rule accountable is not delivered to anybody.");
        Assert.Equal("device-9f2c", who.GetString());
    }

    [Fact]
    public void A_firing_says_what_checking_the_reason_against_the_screen_found()
    {
        var wire = AsWire(SessionRuleWire.Project(AFiring("grounding: 1 passage(s) cited, all found on it.")));

        Assert.True(wire.TryGetProperty("grounding", out var grounding),
            "the firing the account reads does not say what the grounding check found, so a run where " +
            "that check never happened cannot be told from one where it passed.");
        Assert.Contains("cited", grounding.GetString());
    }

    [Fact]
    public void A_rule_still_carries_everything_it_carried_before()
    {
        // THE PRESENCE. A projection that answered only the two new fields would satisfy the tests above
        // and break every reader there is.
        var rule = ALiveRule("device-9f2c");
        var wire = AsWire(SessionRuleWire.Project(rule));

        foreach (var expected in new[]
                 {
                     "id", "instruction", "screenDescription", "triggerWords", "checks", "scope",
                     "cooldownSeconds", "dailyCap", "state", "createdUtc", "updatedUtc",
                 })
            Assert.True(wire.TryGetProperty(expected, out _), "the rule projection lost '" + expected + "'.");

        Assert.Equal("live", wire.GetProperty("state").GetString());
    }

    // ---- fix round D, ruling D8: the Gateway stamps the finished labels; clients render them ----------

    /// <summary>
    /// THE CLIENT IS DUMB (repository rule 7). Both clients were composing "every session" and "10
    /// minutes" for themselves, from different code, so the two could disagree about the same rule. The
    /// served rule carries the finished scope label and wait label, and the clients render the strings.
    /// </summary>
    [Fact]
    public void A_served_rule_carries_the_finished_scope_label_and_wait_label()
    {
        var wire = AsWire(SessionRuleWire.Project(ALiveRule("device-9f2c")));

        Assert.True(wire.TryGetProperty("scopeLabel", out var scope),
            "the served rule carries no scope label, so every client has to compose one for itself.");
        Assert.Equal("every session", scope.GetString());
        Assert.True(wire.TryGetProperty("waitLabel", out var wait),
            "the served rule carries no wait label, so every client has to compose one for itself.");
        Assert.Equal("5 minutes", wait.GetString());
    }

    [Fact]
    public void A_narrow_scope_is_labelled_by_the_parts_that_are_set()
    {
        var rule = ALiveRule("device-9f2c") with { Scope = new RuleScope("Codex", null, "SOREN_NORTH", null) };

        var wire = AsWire(SessionRuleWire.Project(rule));

        Assert.Equal("agent Codex, machine SOREN_NORTH", wire.GetProperty("scopeLabel").GetString());
    }

    // ---- fix round D, ruling D7: a number that cannot be read is a refusal, never a 500 ---------------

    /// <summary>
    /// The write route's number reader called the 32-bit accessor on any JSON number, so a decimal or an
    /// out-of-range integer threw past the route's catch and became a server error with the reason lost.
    /// A write that cannot be read is refused with a sentence that names the field and the value.
    /// </summary>
    [Theory]
    [InlineData("600.5")]
    [InlineData("99999999999")]
    [InlineData("-1e3")]
    public void A_number_that_is_not_a_whole_number_in_range_is_refused_with_a_sentence(string written)
    {
        var body = JsonDocument.Parse("{ \"cooldownSeconds\": " + written + " }").RootElement;

        var ex = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.Number(body, "cooldownSeconds"));

        Assert.Contains("cooldownSeconds", ex.Reason, StringComparison.Ordinal);
        Assert.Contains(written, ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whole_number_in_range_is_read_as_itself()
    {
        var body = JsonDocument.Parse("{ \"cooldownSeconds\": 600 }").RootElement;

        Assert.Equal(600, SessionRuleWire.Number(body, "cooldownSeconds"));
    }

    [Fact]
    public void A_firing_still_carries_everything_it_carried_before()
    {
        var wire = AsWire(SessionRuleWire.Project(AFiring("grounding: nothing was cited.")));

        foreach (var expected in new[]
                 {
                     "id", "ruleId", "sessionId", "occurredUtc", "screenText", "understanding",
                     "decision", "reason", "checksRun", "typedText", "outcome",
                 })
            Assert.True(wire.TryGetProperty(expected, out _), "the firing projection lost '" + expected + "'.");
    }

    // ---- the write side reads its checks the same way the reply does --------------------------------

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("""{ "checks": { } }""")]
    [InlineData("""{ "checks": 7 }""")]
    [InlineData("""{ "checks": "matches_any" }""")]
    [InlineData("""{ "checks": null }""")]
    [InlineData("""{ }""")]
    public void A_write_whose_checks_are_not_a_list_of_checks_is_refused(string json)
    {
        var ex = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.Calls(Body(json)));
        Assert.Contains("checks", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_write_with_an_entry_that_is_not_a_check_is_refused_rather_than_having_it_dropped()
    {
        // The lenient reader dropped a non-object entry silently, so a caller could ask for two checks,
        // have one quietly removed, and get a rule that runs the other one alone.
        var json = """{ "checks": [ { "name": "matches_any" }, "not a check" ] }""";

        var ex = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.Calls(Body(json)));
        Assert.NotEqual("", ex.Reason);
    }

    [Fact]
    public void A_write_with_an_empty_list_of_checks_is_accepted_and_means_no_checks()
    {
        // THE PRESENCE. A reader that refused every shape would make a rule with no checks impossible to
        // write, which is a legitimate rule.
        Assert.Empty(SessionRuleWire.Calls(Body("""{ "checks": [] }""")));
    }

    // ---- fix round F, ruling F3: the label stamper never invents a scope ----------------------------

    /// <summary>
    /// AN ABSENT SCOPE IS NOT "EVERY SESSION" IN THE LABEL EITHER. The stamped labels are what a client
    /// renders verbatim (ruling D8), so a stamper that answered "every session" for a scope that was
    /// never said would put the widest possible sentence in front of a person - the same habit this round
    /// swept for. Every session is still a scope a rule can have; it just has to be a scope, said out
    /// loud, which is what the four-parts-blank control below stands for.
    /// </summary>
    [Fact]
    public void A_scope_that_is_not_there_at_all_is_a_fault_and_never_the_widest_label()
    {
        Assert.Throws<ArgumentNullException>(() => RuleLabels.Scope(null!));
    }

    [Fact]
    public void A_scope_that_says_every_session_out_loud_is_labelled_every_session()
    {
        Assert.Equal("every session", RuleLabels.Scope(RuleScope.AllSessions));
        Assert.Equal("agent ClaudeCode", RuleLabels.Scope(new RuleScope("ClaudeCode", null, null, null)));
    }

    [Fact]
    public void A_write_with_real_checks_reads_them()
    {
        var json = """
        { "checks": [ { "name": "matches_any", "arguments": { "text": "<screen_text>", "terms": ["limit"] } } ] }
        """;

        var call = Assert.Single(SessionRuleWire.Calls(Body(json)));
        Assert.Equal("matches_any", call.Name);
    }


    // ---- phase 1: the text the rule types is shown, because it is what a person confirms ---------------

    /// <summary>The most consequential thing a rule does is the keystroke, so the served rule says exactly
    /// what it will type. A rule delivered without it is a rule the account approved without seeing.</summary>
    [Fact]
    public void A_served_rule_carries_the_text_it_types()
    {
        var wire = AsWire(SessionRuleWire.Project(ALiveRule("device-9f2c")));

        Assert.True(wire.TryGetProperty("textToType", out var text),
            "the served rule does not say what it types, so nobody reading it can see the keystroke it " +
            "was approved to make.");
        Assert.Equal("/model opus", text.GetString());
    }

    /// <summary>The drafted rule's write body carries the text, so posting the proposal back unchanged
    /// stores exactly the text the person read - and the write route has something to store.</summary>
    [Fact]
    public void A_drafted_rules_write_body_carries_the_text_it_types()
    {
        var proposal = new RuleProposal(
            "When I run out of allowance, switch me to Opus.", "sid-1", false, "the screen",
            "A session stopped on a provider allowance notice.", "/model opus",
            new[] { "limit" }, Array.Empty<RulePrimitiveCall>(), RuleScope.AllSessions, 300, 5,
            "I will switch the session to Opus.");

        var wire = AsWire(SessionRuleWire.Project(proposal));

        Assert.Equal("/model opus", wire.GetProperty("rule").GetProperty("textToType").GetString());
    }
}
