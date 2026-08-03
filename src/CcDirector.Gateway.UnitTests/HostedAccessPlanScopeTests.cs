using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The SECOND door into hosted capacity, and the one enrolment cannot close (issue #2147).
///
/// Enrolment is a one-time act. A tenant that already exists keeps asking this gate on EVERY hosted request,
/// so an account that MOVES from a hosted plan to a self-host one - a downgrade, which is exactly what a
/// self-host plan is sold as - would go on being served hosted service forever if only the front door checked
/// the plan. Its entitlement row is still active and still paying, so the entitlement read legitimately says
/// Entitled: it is the PLAN that refuses, not the payment, and nothing else in the request path is looking.
///
/// Both this gate and the enrolment gate read the SAME table (<see cref="EntitlementScopes"/>), so the two
/// can never disagree about what a plan includes.
///
/// HOW THESE FAIL ON PURPOSE: delete the plan branch from <see cref="HostedAccessLeaseService"/> and the
/// self-host test goes RED (it starts allowing). Give pro_selfhost the hosted_gateway scope in the table and
/// it reddens the same way. The hosted control stays green in both cases, which is what proves the refusal is
/// aimed at the plan and is not a gate that denies everyone.
/// </summary>
public sealed class HostedAccessPlanScopeTests : IDisposable
{
    private const string Subject = "sub-plan-scope";

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    /// <summary>Records revocations instead of performing them, so a test can assert that a plan refusal does
    /// NOT tombstone a paying customer's device credentials.</summary>
    private sealed class RecordingRevoker : ITenantAccessRevoker
    {
        public List<string> Revoked { get; } = new();

        public Task RevokeAsync(TenantId tenant, string reason, CancellationToken ct = default)
        {
            Revoked.Add(tenant.Value);
            return Task.CompletedTask;
        }
    }

    /// <summary>The payment-side table this Gateway only reads, with one active, live-money row on the given
    /// plan - the best row a subscriber can have, so any refusal below is about the PLAN and nothing else.</summary>
    private GatewayDatabase OpenWithPlan(string? tier)
    {
        var db = _harness.Open();
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS entitlements (" +
            "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
            "livemode INTEGER NULL, tier TEXT NULL)");
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
            Subject, EntitlementRegistry.StatusActive, DateTime.UtcNow.AddYears(1), true, tier);
        return db;
    }

    private static (HostedAccessLeaseService leases, TenantId tenant, RecordingRevoker revoker) Wire(GatewayDatabase db)
    {
        var tenants = new TenantRegistry(db);
        var tenant = tenants.MintOrLookupBySubject(Subject, "payer@example.com");
        var revoker = new RecordingRevoker();
        var leases = new HostedAccessLeaseService(
            new EntitlementRegistry(db, requireLivemode: true), tenants, revoker);
        return (leases, tenant, revoker);
    }

    [Fact]
    public async Task AuthorizeAsync_SelfHostPlanOnAnActiveRow_DeniesHostedAccess()
    {
        // The negative control on the ongoing path. Active, live money, in period - and still refused, because
        // the plan does not include hosted capacity.
        var (leases, tenant, _) = Wire(OpenWithPlan(EntitlementRegistry.TierProSelfHost));

        var decision = await leases.AuthorizeAsync(tenant);

        Assert.Equal(HostedAccessDecision.DenyNotEntitled, decision);
    }

    [Fact]
    public async Task AuthorizeAsync_HostedPlanOnAnActiveRow_AllowsHostedAccess()
    {
        // The positive control that stops the test above passing for the wrong reason. An identical row on the
        // hosted plan IS served - so the refusal above is caused by the plan specifically and not by the row
        // being rejected for some unrelated defect, and not by a gate that denies everyone.
        var (leases, tenant, _) = Wire(OpenWithPlan(EntitlementRegistry.TierHosted));

        var decision = await leases.AuthorizeAsync(tenant);

        Assert.Equal(HostedAccessDecision.Allow, decision);
    }

    [Fact]
    public async Task AuthorizeAsync_SelfHostPlan_DeniesWithoutRevokingDeviceCredentials()
    {
        // A plan refusal denies; it must NOT tombstone. The reason for this refusal is a string the payment
        // side writes, so a renamed or mistyped plan would otherwise destroy a paying customer's device
        // credentials - damage a later correction cannot undo. The deny is re-evaluated on every request and is
        // sufficient. (A CANCELLED subscription still revokes; that path is unchanged.)
        var (leases, tenant, revoker) = Wire(OpenWithPlan(EntitlementRegistry.TierProSelfHost));

        await leases.AuthorizeAsync(tenant);

        Assert.Empty(revoker.Revoked);
    }

    [Fact]
    public async Task AuthorizeAsync_UnknownPlan_DeniesHostedAccess()
    {
        // The default direction on the ongoing path, matching the front door: a plan this code has never been
        // taught grants nothing, so it is not served hosted capacity.
        var (leases, tenant, _) = Wire(OpenWithPlan("enterprise_platinum"));

        var decision = await leases.AuthorizeAsync(tenant);

        Assert.Equal(HostedAccessDecision.DenyNotEntitled, decision);
    }

    [Fact]
    public async Task AuthorizeAsync_RowPredatingTheTierColumn_AllowsHostedAccess()
    {
        // The established customers who predate the tier column. A null tier is an OLD HOSTED ROW, not an
        // unknown plan, and refusing it would lock out people who have paid for months over a column they were
        // written before. If a future edit folds null into the unknown-plan default, this reddens - which is
        // the point of writing it down.
        var (leases, tenant, _) = Wire(OpenWithPlan(tier: null));

        var decision = await leases.AuthorizeAsync(tenant);

        Assert.Equal(HostedAccessDecision.Allow, decision);
    }
}

/// <summary>
/// The plan-to-scope table itself (issue #2147). The enforcement tests above prove the Gateway ACTS on this
/// table; these pin what the table SAYS, so a plan's contents cannot drift silently underneath them.
/// </summary>
public sealed class EntitlementScopeTableTests
{
    [Fact]
    public void ForTier_ProSelfHost_GrantsTheThreeArtificialIntelligenceScopesAndNothingElse()
    {
        var granted = EntitlementScopes.ForTier(EntitlementRegistry.TierProSelfHost);

        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal)
            {
                EntitlementScopes.Dictation, EntitlementScopes.Tts, EntitlementScopes.Wingman,
            },
            new SortedSet<string>(granted, StringComparer.Ordinal));
    }

    [Fact]
    public void GrantsHostedGateway_ProSelfHost_IsFalse()
    {
        Assert.False(EntitlementScopes.GrantsHostedGateway(EntitlementRegistry.TierProSelfHost));
    }

    [Theory]
    [InlineData("hosted")]
    [InlineData("pro")]
    [InlineData(null)]
    public void GrantsHostedGateway_TheTiersThatAlreadyGrantedIt_StillDo(string? tier)
    {
        // The no-regression line. These three already provisioned hosted capacity before this table existed
        // (the tier-agnostic enrolment gate served all of them), and adding the table must not quietly take it
        // away from anyone. Null is the pre-column row - see the class remarks on EntitlementScopes.
        Assert.True(EntitlementScopes.GrantsHostedGateway(tier));
    }

    [Theory]
    [InlineData("enterprise_platinum")]
    [InlineData("PRO_SELFHOST")]
    [InlineData("Hosted")]
    public void GrantsHostedGateway_AnUnrecognisedTier_IsFalse(string tier)
    {
        // Deny by default, and EXACT matching. A tier is a value the payment side writes, not prose to be
        // interpreted: accepting a different casing would mean accepting a string the payment side never wrote,
        // and the whole table's safety rests on the two sides using identical values.
        Assert.False(EntitlementScopes.GrantsHostedGateway(tier));
    }

    [Fact]
    public void ForTier_ProSelfHost_IsNeverTreatedAsThePlainProTier()
    {
        // THE STRING-MATCHING TRAP, and it is the one mistake that would silently undo this entire issue.
        // "pro" is a PREFIX of "pro_selfhost", and "pro" deliberately DOES carry hosted gateway (the fourteen-day
        // trial on main grants exactly that tier). So any comparison that is sloppy about where a tier string
        // ends - StartsWith, Contains, a prefix or substring match, a "does it begin with pro" shortcut - would
        // hand hosted gateway to the one plan this issue exists to deny, and it would look completely correct
        // while doing it.
        //
        // The table matches whole strings by ordinal equality, and these assertions pin that: the two tiers
        // resolve to DIFFERENT scope sets, and the difference is exactly the hosted gateway scope.
        var pro = EntitlementScopes.ForTier(EntitlementRegistry.TierPro);
        var selfHost = EntitlementScopes.ForTier(EntitlementRegistry.TierProSelfHost);

        Assert.StartsWith(EntitlementRegistry.TierPro, EntitlementRegistry.TierProSelfHost, StringComparison.Ordinal);
        Assert.NotEqual(EntitlementRegistry.TierPro, EntitlementRegistry.TierProSelfHost);

        Assert.True(pro.Contains(EntitlementScopes.HostedGateway));         // "pro" carries it, on purpose
        Assert.False(selfHost.Contains(EntitlementScopes.HostedGateway));   // "pro_selfhost" never does
        Assert.NotEqual(pro.Count, selfHost.Count);
    }

    [Fact]
    public void ForTier_AnUnrecognisedTier_GrantsNothingAtAll()
    {
        Assert.Empty(EntitlementScopes.ForTier("enterprise_platinum"));
    }

    [Theory]
    [InlineData("hosted")]
    [InlineData("pro")]
    [InlineData("pro_selfhost")]
    [InlineData(null)]
    public void ForTier_EveryPaidPlan_GrantsTheArtificialIntelligenceFeatures(string? tier)
    {
        // What every paid plan has in common. Self-host is a plan that pays for exactly these, so if a future
        // edit narrows it by removing one of them, that is a paying customer losing a feature they bought.
        var granted = EntitlementScopes.ForTier(tier);

        Assert.Contains(EntitlementScopes.Dictation, granted);
        Assert.Contains(EntitlementScopes.Tts, granted);
        Assert.Contains(EntitlementScopes.Wingman, granted);
    }

    [Fact]
    public void Grants_WithABlankScopeName_Throws()
    {
        Assert.Throws<ArgumentException>(() => EntitlementScopes.Grants(EntitlementRegistry.TierHosted, "  "));
    }
}
