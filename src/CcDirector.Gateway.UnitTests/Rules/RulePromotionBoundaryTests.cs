using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// DRY RUN IS THE OWNER'S MOST IMPORTANT BOUND, AND THIS IS WHERE IT IS ENFORCED RATHER THAN DESCRIBED.
/// The independent inspection of landing A found that <c>Promote</c> took a rule id and a timestamp and
/// nothing else, so anything that could read rules could move one to live, and that the test which claimed
/// "only a person moves it" proved only that a direct call worked - the caller in it had no property that
/// made it a person.
///
/// So these tests are written the other way round, exactly as the ruling asks: they show a caller that is
/// NOT a person being REFUSED. A promotion with no grant is refused; a grant that names nobody cannot be
/// minted; a grant with no acknowledgement cannot be minted; and a grant obtained for one rule cannot be
/// turned on another.
/// </summary>
public sealed class RulePromotionBoundaryTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private SessionRuleStore NewStore() => new(_h.Open());

    private static IReadOnlyList<RulePrimitiveCall> GoodCalls() => new[]
    {
        RulePrimitiveCall.To(
            "matches_any",
            RuleArgument.FromInput("text", RuleInput.ScreenText),
            RuleArgument.LiteralList("terms", new[] { "usage limit" })),
    };

    private SessionRule CreateTheRule(SessionRuleStore store) => store.Create(
        "When I run out of allowance, switch me to Opus.",
        "A session stopped on a provider allowance notice.",
        new[] { "limit" },
        GoodCalls(),
        RuleScope.AllSessions,
        cooldownSeconds: 300,
        dailyCap: 5,
        Now);

    /// <summary>A grant as the endpoint mints one, for a caller the request pipeline named.</summary>
    private static RulePromotionGrant GrantFor(Guid ruleId) =>
        RulePromotionGrant.FromAuthenticatedRequest(
            ruleId, "device-9f2c", "I have read this rule's dry-run record and I am making it live.", Now);

    [Fact]
    public void A_promotion_carrying_no_grant_is_refused_because_a_person_has_to_have_asked()
    {
        var store = NewStore();
        var rule = CreateTheRule(store);

        var ex = Assert.Throws<RuleRejectedException>(() => store.Promote(rule.Id, null!, Now));

        Assert.Contains("person", ex.Reason, StringComparison.OrdinalIgnoreCase);
        // And the rule is still where it was. A refusal that half-promoted would be worse than none.
        Assert.Equal(RuleState.DryRun, store.Get(rule.Id)!.State);
    }

    [Fact]
    public void A_caller_the_request_pipeline_could_not_name_cannot_mint_a_grant_at_all()
    {
        // This is the automated caller: it has no inbound request, so there is nobody for the pipeline to
        // have authenticated, and there is therefore nothing it can hand Promote.
        var ex = Assert.Throws<RuleRejectedException>(() => RulePromotionGrant.FromAuthenticatedRequest(
            Guid.NewGuid(), callerIdentity: null, acknowledgement: "make it live", Now));
        Assert.NotEqual("", ex.Reason);

        var blank = Assert.Throws<RuleRejectedException>(() => RulePromotionGrant.FromAuthenticatedRequest(
            Guid.NewGuid(), callerIdentity: "   ", acknowledgement: "make it live", Now));
        Assert.NotEqual("", blank.Reason);
    }

    [Fact]
    public void A_promotion_with_nothing_said_cannot_mint_a_grant_so_an_empty_post_promotes_nothing()
    {
        var ex = Assert.Throws<RuleRejectedException>(() => RulePromotionGrant.FromAuthenticatedRequest(
            Guid.NewGuid(), "device-9f2c", acknowledgement: "", Now));
        Assert.NotEqual("", ex.Reason);
    }

    [Fact]
    public void A_grant_obtained_for_one_rule_cannot_promote_another()
    {
        var store = NewStore();
        var mine = CreateTheRule(store);
        var other = CreateTheRule(store);

        var ex = Assert.Throws<RuleRejectedException>(() => store.Promote(other.Id, GrantFor(mine.Id), Now));

        Assert.NotEqual("", ex.Reason);
        Assert.Equal(RuleState.DryRun, store.Get(other.Id)!.State);
    }

    [Fact]
    public void A_person_who_asked_does_promote_it_and_the_rule_records_who()
    {
        var store = NewStore();
        var rule = CreateTheRule(store);

        var promoted = store.Promote(rule.Id, GrantFor(rule.Id), Now.AddHours(1));

        Assert.Equal(RuleState.Live, promoted.State);
        Assert.Equal("device-9f2c", promoted.PromotedBy);
        // Read back through a SECOND store over the same database, so this is the stored row and not the
        // object that was just handed out.
        var read = new SessionRuleStore(_h.Open()).Get(rule.Id)!;
        Assert.Equal(RuleState.Live, read.State);
        Assert.Equal("device-9f2c", read.PromotedBy);
    }

    [Fact]
    public void The_seam_the_evaluator_is_given_has_no_way_to_promote_create_or_delete_a_rule()
    {
        // The evaluator's environment is handed IRuleReading, not the store. A method that could change a
        // rule's life cycle must not be on it - this is the interface's whole reason for existing, so it is
        // asserted rather than left to whoever edits it next.
        var members = typeof(IRuleReading).GetMembers().Select(m => m.Name).ToList();
        Assert.NotEmpty(members);
        Assert.DoesNotContain(members, n => n.Contains("Promote", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, n => n.Contains("Create", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, n => n.Contains("Delete", StringComparison.OrdinalIgnoreCase));

        // A PRESENCE too: the seam must actually carry what the evaluator needs, or "it cannot promote"
        // would be true of an empty interface that also cannot do anything else.
        Assert.Contains(members, n => n == "All");
        Assert.Contains(members, n => n == "FiringsFor");
        Assert.Contains(members, n => n == "RecordFiring");
    }
}
