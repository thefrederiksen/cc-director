using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.TurnLog;
using Xunit;

namespace CcDirector.Gateway.Tests.TurnLog;

/// <summary>
/// The switch: who is being recorded, and the ordering that lets one machine be treated differently from
/// the account it belongs to.
///
/// The first test is the one that matters most. Capture is off until somebody decides otherwise, and an
/// account nobody has considered must never be recorded by accident.
/// </summary>
public sealed class TurnLogSwitchStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();

    public void Dispose() => _h.Dispose();

    private TurnLogSwitchStore NewStore() => new(Db, () => new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void IsEnabled_NobodyHasDecidedAnything_IsOff()
    {
        var store = NewStore();
        Assert.False(store.IsEnabled("acct-a", "director-1"));
    }

    [Fact]
    public void IsEnabled_TheWholeAccountIsOn_CoversEveryMachineOnIt()
    {
        var store = NewStore();
        store.Set("acct-a", TurnLogSwitchEntity.Any, enabled: true, actor: "soren", reason: "our own fleet");

        Assert.True(store.IsEnabled("acct-a", "director-1"));
        Assert.True(store.IsEnabled("acct-a", "director-2"));
        Assert.False(store.IsEnabled("acct-b", "director-1"));
    }

    [Fact]
    public void IsEnabled_OneMachineOffInsideAnAccountThatIsOn_TheMachineWins()
    {
        // The reason the ordering exists: a noisy machine can be left out without switching the account off.
        var store = NewStore();
        store.Set("acct-a", TurnLogSwitchEntity.Any, enabled: true, actor: "soren", reason: "our own fleet");
        store.Set("acct-a", "director-2", enabled: false, actor: "soren", reason: "this one is too chatty");

        Assert.True(store.IsEnabled("acct-a", "director-1"));
        Assert.False(store.IsEnabled("acct-a", "director-2"));
    }

    [Fact]
    public void IsEnabled_OneMachineOnInsideAnAccountNobodyDecidedAbout_IsOnForThatMachineOnly()
    {
        var store = NewStore();
        store.Set("acct-b", "director-9", enabled: true, actor: "soren", reason: "they agreed on a call");

        Assert.True(store.IsEnabled("acct-b", "director-9"));
        Assert.False(store.IsEnabled("acct-b", "director-8"));
    }

    [Fact]
    public void IsEnabled_TheWholeFleetIsOn_CoversAnAccountNobodyNamed()
    {
        var store = NewStore();
        store.Set(TurnLogSwitchEntity.Any, TurnLogSwitchEntity.Any, enabled: true, actor: "soren", reason: "the whole fleet");

        Assert.True(store.IsEnabled("acct-anything", "director-anything"));
    }

    [Fact]
    public void Set_SwitchingOffLeavesARowSayingSo_RatherThanForgettingTheDecision()
    {
        // "We decided not to record this" and "nobody has ever considered it" are different facts, and only
        // one of them can be defended later.
        var store = NewStore();
        store.Set("acct-b", TurnLogSwitchEntity.Any, enabled: true, actor: "soren", reason: "they agreed");
        store.Set("acct-b", TurnLogSwitchEntity.Any, enabled: false, actor: "soren", reason: "they withdrew permission");

        var row = Assert.Single(store.All(), r => r.Account == "acct-b");
        Assert.False(row.Enabled);
        Assert.Equal("they withdrew permission", row.Reason);
        Assert.Equal("soren", row.Actor);
    }

    [Fact]
    public void Set_RecordsWhoDecidedAndWhy()
    {
        var store = NewStore();
        store.Set("acct-b", "director-9", enabled: true, actor: "soren", reason: "written permission, 4 September");

        var row = Assert.Single(store.All());
        Assert.Equal("soren", row.Actor);
        Assert.Equal("written permission, 4 September", row.Reason);
        Assert.Equal(new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc), row.RecordedUtc);
    }

    [Theory]
    [InlineData("", "reason")]
    [InlineData("actor", "")]
    [InlineData("  ", "reason")]
    public void Set_WithoutAnActorOrAReason_IsRefused(string actor, string reason)
    {
        // For an account that is not ours, the reason is where the permission is written down. A blank one
        // answers no question anybody will actually ask.
        var store = NewStore();
        Assert.Throws<ArgumentException>(() =>
            store.Set("acct-b", "director-9", enabled: true, actor: actor, reason: reason));
    }
}
