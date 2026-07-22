using System;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// audit-a regression tests: the shared hosted Gateway's broadcast governance state is keyed by the
/// OWNING tenant. Without the fix the rate-limit window is keyed by the bare session id and a grant
/// carries no owner, so tenant A can deny tenant B's same-id broadcast and A's grant validates in B's
/// partition. These pin both leaks shut. Reverting the fix reddens every assertion here.
/// </summary>
public sealed class BroadcastGovernorTenantIsolationTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    private sealed class FakeClock
    {
        public DateTime Now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
    }

    [Fact]
    public void RateWindow_isPerTenant_soOneTenantCannotExhaustAnothersSameSessionId()
    {
        var gov = new BroadcastGovernor(maxPerWindow: 1);

        // Tenant A uses up the whole window for session id X.
        Assert.True(gov.TryRecordSend(TenantA, "session-X").Allowed);
        Assert.False(gov.TryRecordSend(TenantA, "session-X").Allowed);

        // Tenant B broadcasts as the SAME session id X. It has its own partition, so it is still allowed -
        // A's timestamps never touch B's window.
        Assert.True(gov.TryRecordSend(TenantB, "session-X").Allowed);
        // And B has now used its own single-broadcast budget for X.
        Assert.False(gov.TryRecordSend(TenantB, "session-X").Allowed);
    }

    [Fact]
    public void Grant_isValidOnlyInTheTenantThatMintedIt()
    {
        var clock = new FakeClock();
        var gov = new BroadcastGovernor(grantTtl: TimeSpan.FromMinutes(10), now: clock.Get);

        var grantA = gov.MintGrant(TenantA);

        // Valid in its own tenant.
        Assert.True(gov.IsGrantValid(TenantA, grantA));
        // NEVER valid in another tenant's partition, even though the id exists and has not expired.
        Assert.False(gov.IsGrantValid(TenantB, grantA));
    }
}
