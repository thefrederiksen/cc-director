using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hostile two-tenant coverage for the TWO plain-endpoint consumers this branch owns (issue #2017): the
/// snooze default read at hold creation and the display time zone read in the stats page. Both consumers
/// call the resolver with the tenant the route resolved. These tests prove, at the resolver seam the
/// consumers use, that one tenant's override is NEVER served to another - the other tenant gets the operator
/// global default, not a cross-tenant value. (The MTR mission owns the equivalent proof for the deep runtime
/// consumers - wingman models, text-to-speech, narration, car mode.)
/// </summary>
public sealed class TenantSettingsConsumerIsolationTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SnoozeDefault_consumer_reads_only_the_callers_tenant()
    {
        using var h = new GatewayDbTestHarness();
        var r = new TenantSettingsResolver(new TenantSettingsStore(h.Open()));

        // Tenant A sets a distinctive default; tenant B sets nothing.
        r.SetSnoozePresets(TenantA, new[] { 7, 60 }, 7, Now);

        // The hold-creation consumer calls SnoozeDefaultMinutes(tenant). A sees 7; B must see the operator
        // global default, NOT tenant A's 7.
        Assert.Equal(7, r.SnoozeDefaultMinutes(TenantA));
        Assert.Equal(Core.Configuration.SnoozeDefaultConfig.Get(), r.SnoozeDefaultMinutes(TenantB));
        Assert.NotEqual(7, r.SnoozeDefaultMinutes(TenantB));
    }

    [Fact]
    public void TimeZone_consumer_reads_only_the_callers_tenant()
    {
        using var h = new GatewayDbTestHarness();
        var r = new TenantSettingsResolver(new TenantSettingsStore(h.Open()));

        // Tenant A overrides the display zone; tenant B sets nothing.
        r.SetTimeZone(TenantA, "Asia/Tokyo", Now);

        // The stats consumer calls TimeZone(tenant). A sees Tokyo; B must see the operator global default,
        // NOT tenant A's zone.
        Assert.Equal("Asia/Tokyo", r.TimeZone(TenantA));
        Assert.Equal(Core.Configuration.TimeZoneConfig.Get(), r.TimeZone(TenantB));
        Assert.NotEqual("Asia/Tokyo", r.TimeZone(TenantB));
    }

    [Fact]
    public void One_tenant_setting_a_default_never_moves_anothers()
    {
        using var h = new GatewayDbTestHarness();
        var r = new TenantSettingsResolver(new TenantSettingsStore(h.Open()));

        r.SetSnoozePresets(TenantA, new[] { 7, 60 }, 7, Now);
        r.SetSnoozePresets(TenantB, new[] { 480, 60 }, 480, Now);

        Assert.Equal(7, r.SnoozeDefaultMinutes(TenantA));
        Assert.Equal(480, r.SnoozeDefaultMinutes(TenantB));
    }
}
