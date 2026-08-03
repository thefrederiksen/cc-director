using System;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the Hub's broadcast governance state (issue #1229): the human-issued grant store and
/// the per-sender broadcast rate limiter. Uses an injected clock so time-based behaviour is
/// deterministic. All state is tenant-keyed (audit-a); these single-tenant tests pin the behaviour
/// within one tenant, and <see cref="BroadcastGovernorTenantIsolationTests"/> pins the cross-tenant
/// isolation.
/// </summary>
public sealed class BroadcastGovernorTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    private sealed class FakeClock
    {
        public DateTime Now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    // ===== Grants =====

    [Fact]
    public void MintedGrant_isValid_untilItExpires()
    {
        var clock = new FakeClock();
        var gov = new BroadcastGovernor(grantTtl: TimeSpan.FromMinutes(10), now: clock.Get);

        var grant = gov.MintGrant(Tenant);
        Assert.True(gov.IsGrantValid(Tenant, grant));

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(gov.IsGrantValid(Tenant, grant));

        clock.Advance(TimeSpan.FromMinutes(2)); // now 11 minutes in, past the 10-minute TTL
        Assert.False(gov.IsGrantValid(Tenant, grant));
    }

    [Fact]
    public void UnknownOrBlankGrant_isNeverValid()
    {
        var gov = new BroadcastGovernor();
        Assert.False(gov.IsGrantValid(Tenant, null));
        Assert.False(gov.IsGrantValid(Tenant, ""));
        Assert.False(gov.IsGrantValid(Tenant, "   "));
        Assert.False(gov.IsGrantValid(Tenant, "not-a-real-grant"));
    }

    [Fact]
    public void EachMintedGrant_isDistinct()
    {
        var gov = new BroadcastGovernor();
        Assert.NotEqual(gov.MintGrant(Tenant), gov.MintGrant(Tenant));
    }

    // ===== Rate limiting =====

    [Fact]
    public void SenderIsAllowed_upToTheLimit_thenDenied()
    {
        var clock = new FakeClock();
        var gov = new BroadcastGovernor(maxPerWindow: 3, window: TimeSpan.FromSeconds(60), now: clock.Get);

        Assert.True(gov.TryRecordSend(Tenant, "sender-1").Allowed);
        Assert.True(gov.TryRecordSend(Tenant, "sender-1").Allowed);
        Assert.True(gov.TryRecordSend(Tenant, "sender-1").Allowed);

        var fourth = gov.TryRecordSend(Tenant, "sender-1");
        Assert.False(fourth.Allowed);
        Assert.Equal(3, fourth.LimitPerWindow);
        Assert.Equal(60, fourth.WindowSeconds);
    }

    [Fact]
    public void RateLimit_isPerSender_notGlobal()
    {
        var gov = new BroadcastGovernor(maxPerWindow: 1);

        Assert.True(gov.TryRecordSend(Tenant, "sender-a").Allowed);
        Assert.False(gov.TryRecordSend(Tenant, "sender-a").Allowed);
        Assert.True(gov.TryRecordSend(Tenant, "sender-b").Allowed); // a different sender has its own budget
    }

    [Fact]
    public void RateLimit_windowSlidesForward_soOldSendsFallOff()
    {
        var clock = new FakeClock();
        var gov = new BroadcastGovernor(maxPerWindow: 2, window: TimeSpan.FromSeconds(60), now: clock.Get);

        Assert.True(gov.TryRecordSend(Tenant, "s").Allowed);
        Assert.True(gov.TryRecordSend(Tenant, "s").Allowed);
        Assert.False(gov.TryRecordSend(Tenant, "s").Allowed);

        clock.Advance(TimeSpan.FromSeconds(61)); // both prior sends are now outside the window
        Assert.True(gov.TryRecordSend(Tenant, "s").Allowed);
    }

    [Fact]
    public void BlankSender_isExempt_fromRateLimiting()
    {
        var gov = new BroadcastGovernor(maxPerWindow: 1);
        Assert.True(gov.TryRecordSend(Tenant, null).Allowed);
        Assert.True(gov.TryRecordSend(Tenant, "").Allowed);
        Assert.True(gov.TryRecordSend(Tenant, null).Allowed);
    }

    [Fact]
    public void Constructor_rejectsAZeroLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastGovernor(maxPerWindow: 0));
    }
}
