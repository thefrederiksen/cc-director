using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE VALIDATOR IS THE WRITE GATE, AND THESE TESTS TAKE THE ROUTE THAT WENT ROUND IT. The store's own
/// source claimed that nothing reaches <c>session_rules</c> without passing the validator. The independent
/// inspection of landing A found that claim was false: the entity, its setters, the DbSet and the context
/// factory are all public, so a Gateway caller could construct a rule with an arbitrary call document, an
/// arbitrary tenant and <c>State = "live"</c>, add it and save it, meeting neither the validator nor dry
/// run. One bypass defeated three properties at once.
///
/// So the check now lives in <c>SaveChanges</c>, where the route cannot go round it, and every test here
/// writes THROUGH THE CONTEXT rather than through the store - the bypass itself, run as a test.
///
/// The firing tests are the same shape for the other half of finding 9: the record is the product, and a
/// record that can be blank or can name a check that never ran is a record nobody can use as evidence.
/// </summary>
public sealed class RulesWriteGateTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string DryRun = RuleWireNames.ToWireName(nameof(RuleState.DryRun));
    private static readonly string Live = RuleWireNames.ToWireName(nameof(RuleState.Live));

    private static List<RulePrimitiveCall> GoodCalls() => new()
    {
        RulePrimitiveCall.To(
            "matches_any",
            RuleArgument.FromInput("text", RuleInput.ScreenText),
            RuleArgument.LiteralList("terms", new[] { "usage limit" })),
    };

    /// <summary>A rule entity as a caller going round the store would build one.</summary>
    private static SessionRuleEntity Straight(string tenant, string state, List<RulePrimitiveCall> calls) => new()
    {
        TenantId = tenant,
        Instruction = "When I run out of allowance, switch me to Opus.",
        ScreenDescription = "A session stopped on a provider allowance notice.",
        TriggerWords = new List<string> { "limit" },
        Calls = calls,
        CooldownSeconds = 300,
        DailyCap = 5,
        State = state,
        CreatedUtc = Now,
        UpdatedUtc = Now,
    };

    // ---- finding 2: the route round the validator ---------------------------------------------------

    [Fact]
    public void A_rule_written_straight_through_the_data_context_still_meets_the_validator()
    {
        var db = _h.Open();
        using var ctx = db.CreateContext();

        // A call naming a check the product does not ship - exactly what the store refuses, arriving by the
        // route that used to miss the store entirely.
        var invented = new List<RulePrimitiveCall>
        {
            RulePrimitiveCall.To("run_python", RuleArgument.Literal("code", "import os")),
        };
        ctx.SessionRules.Add(Straight(ctx.ActiveTenant!, DryRun, invented));

        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
        Assert.Contains("run_python", ex.Reason, StringComparison.Ordinal);

        Assert.Empty(new SessionRuleStore(_h.Open()).All());
    }

    [Fact]
    public void A_rule_written_straight_through_the_data_context_cannot_start_live()
    {
        var db = _h.Open();
        using var ctx = db.CreateContext();
        ctx.SessionRules.Add(Straight(ctx.ActiveTenant!, Live, GoodCalls()));

        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
        Assert.Contains("dry run", ex.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(new SessionRuleStore(_h.Open()).All());
    }

    [Fact]
    public void A_rule_written_straight_through_the_data_context_cannot_belong_to_another_account()
    {
        var db = _h.Open(new FixedTenantContext(new TenantId("tenant-a")));
        using var ctx = db.CreateContext();
        ctx.SessionRules.Add(Straight("tenant-b", DryRun, GoodCalls()));

        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
        Assert.Contains("tenant-b", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_moved_to_live_straight_through_the_data_context_is_refused_without_a_grant()
    {
        var db = _h.Open();
        var store = new SessionRuleStore(db);
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" }, GoodCalls(), RuleScope.AllSessions, 300, 5, Now);

        using var ctx = db.CreateContext();
        var row = ctx.SessionRules.First(r => r.Id == rule.Id);
        row.State = Live;

        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
        Assert.Contains("person", ex.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RuleState.DryRun, new SessionRuleStore(_h.Open()).Get(rule.Id)!.State);
    }

    [Fact]
    public void The_gate_lets_a_rule_the_store_built_through_so_it_is_a_gate_and_not_a_wall()
    {
        // The PRESENCE half. A gate that refused everything would pass every test above while making the
        // feature impossible, and the tests above could not tell the difference.
        var store = new SessionRuleStore(_h.Open());
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" }, GoodCalls(), RuleScope.AllSessions, 300, 5, Now);

        Assert.Equal(RuleState.DryRun, rule.State);
        Assert.Single(new SessionRuleStore(_h.Open()).All());
    }

    // ---- finding 9: the record is the product -------------------------------------------------------

    private (SessionRuleStore Store, SessionRule Rule) StoreWithARule()
    {
        var store = new SessionRuleStore(_h.Open());
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" }, GoodCalls(), RuleScope.AllSessions, 300, 5, Now);
        return (store, rule);
    }

    [Theory]
    [InlineData(null, RuleDecisions.Decline, "not covered", "left alone")]
    [InlineData("", RuleDecisions.Decline, "not covered", "left alone")]
    [InlineData("session-101", null, "not covered", "left alone")]
    [InlineData("session-101", "", "not covered", "left alone")]
    [InlineData("session-101", RuleDecisions.Decline, null, "left alone")]
    [InlineData("session-101", RuleDecisions.Decline, "", "left alone")]
    [InlineData("session-101", RuleDecisions.Decline, "not covered", null)]
    [InlineData("session-101", RuleDecisions.Decline, "not covered", "")]
    public void A_firing_missing_any_part_of_the_record_is_refused_rather_than_blanked(
        string? sessionId, string? decision, string? reason, string? outcome)
    {
        var (store, rule) = StoreWithARule();

        var ex = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
            rule.Id, sessionId!, "a screen", "an understanding", decision!, reason!,
            Array.Empty<RulePrimitiveRun>(), "", outcome!, "grounding: nothing was quoted.", Now));

        Assert.NotEqual("", ex.Reason);
        Assert.Empty(store.FiringsFor(rule.Id));
    }

    [Fact]
    public void A_firing_whose_decision_is_not_one_of_the_four_is_refused()
    {
        var (store, rule) = StoreWithARule();

        var ex = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
            rule.Id, "session-101", "a screen", "an understanding", "would_have_acted", "dry run",
            Array.Empty<RulePrimitiveRun>(), "", "reported", "grounding: nothing was quoted.", Now));

        Assert.Contains("would_have_acted", ex.Reason, StringComparison.Ordinal);
        Assert.Empty(store.FiringsFor(rule.Id));
    }

    [Fact]
    public void A_firing_claiming_a_check_the_product_does_not_ship_is_refused()
    {
        var (store, rule) = StoreWithARule();

        var ex = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
            rule.Id, "session-101", "a screen", "an understanding", RuleDecisions.Decline, "not covered",
            new[] { new RulePrimitiveRun("run_python", "code=import os", "true") },
            "", "left alone", "grounding: nothing was quoted.", Now));

        Assert.Contains("run_python", ex.Reason, StringComparison.Ordinal);
        Assert.Empty(store.FiringsFor(rule.Id));
    }

    [Fact]
    public void A_firing_with_a_check_that_answered_nothing_is_refused_because_a_blank_answer_is_not_a_result()
    {
        var (store, rule) = StoreWithARule();

        var ex = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
            rule.Id, "session-101", "a screen", "an understanding", RuleDecisions.Decline, "not covered",
            new[] { new RulePrimitiveRun("matches_any", "text=<screen_text>, terms=usage limit", "") },
            "", "left alone", "grounding: nothing was quoted.", Now));

        Assert.NotEqual("", ex.Reason);
        Assert.Empty(store.FiringsFor(rule.Id));
    }

    [Fact]
    public void A_firing_that_never_had_its_reason_checked_against_the_screen_is_refused()
    {
        // THE PRESENCE THAT RULING A12 ASKS FOR. A firing that carries no grounding statement is a firing
        // whose grounding check may simply never have run, and that must not be indistinguishable from one
        // that ran and found nothing wrong.
        var (store, rule) = StoreWithARule();

        var ex = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
            rule.Id, "session-101", "a screen", "an understanding", RuleDecisions.Decline, "not covered",
            Array.Empty<RulePrimitiveRun>(), "", "left alone", grounding: "", Now));

        Assert.NotEqual("", ex.Reason);
        Assert.Empty(store.FiringsFor(rule.Id));
    }

    [Fact]
    public void A_complete_firing_is_written_whole_so_the_refusals_above_are_not_a_wall()
    {
        var (store, rule) = StoreWithARule();

        store.RecordFiring(
            rule.Id, "session-101", "Claude usage limit reached.", "stopped on an allowance notice",
            RuleDecisions.Act, "the screen is the notice the instruction is about",
            new[] { new RulePrimitiveRun("matches_any", "text=<screen_text>, terms=usage limit", "true") },
            "", "dry run: nothing was typed.",
            "grounding: 1 quoted passage checked against the screen, all found.", Now);

        var read = Assert.Single(store.FiringsFor(rule.Id));
        Assert.Equal(RuleDecisions.Act, read.Decision);
        Assert.Contains("grounding:", read.Grounding, StringComparison.Ordinal);
    }

    // ---- finding 5: a missing scope silently widened to every session -------------------------------

    [Fact]
    public void A_rule_with_no_scope_at_all_is_refused_because_every_session_has_to_be_said_out_loud()
    {
        var store = new SessionRuleStore(_h.Open());

        var ex = Assert.Throws<RuleRejectedException>(() => store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" }, GoodCalls(), scope: null!, 300, 5, Now));

        Assert.NotEqual("", ex.Reason);
        Assert.Empty(store.All());
    }

    [Fact]
    public void Every_session_is_still_a_scope_a_rule_can_have_when_it_is_chosen_on_purpose()
    {
        var store = new SessionRuleStore(_h.Open());
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" }, GoodCalls(), RuleScope.AllSessions, 300, 5, Now);

        Assert.Equal(RuleScope.AllSessions, store.Get(rule.Id)!.Scope);
    }
}
