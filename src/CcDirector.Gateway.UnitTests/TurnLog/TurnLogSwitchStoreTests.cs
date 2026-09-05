using CcDirector.Core.Tenancy;
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

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        _h.Dispose();
    }

    private readonly List<TurnLogSwitchStore> _stores = new();

    private TurnLogSwitchStore NewStore()
    {
        var store = new TurnLogSwitchStore(Db, () => new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc));
        // The answer is served from memory so the turn-end path never waits on a database, which means it
        // has to be primed before it can say yes to anything.
        store.Start();
        _stores.Add(store);
        return store;
    }

    [Fact]
    public void TheSwitchIsReadableWithNoTenantInScope_WhichIsEVERYHOSTEDTURNEND()
    {
        // THE TEST THAT WOULD HAVE CAUGHT THE WORST BUG THIS FEATURE HAD, and the reason it did not exist
        // is worth keeping written down: every other test in this file runs against the shared harness,
        // which supplies an ambient Local tenant, so a tenant-SCOPED context worked perfectly here while
        // throwing on the one deployment that matters. On the hosted Gateway there is no ambient tenant at a
        // turn-end boundary and none on an administrator request either. The throw was then swallowed into
        // "capture is off" - because this read must never fail open - so the feature would have looked
        // switched on, recorded nothing, and said nothing about why.
        //
        // This table is global on purpose and must be read through an UNSCOPED context. Driving it with a
        // context that denies when no scope is entered is what pins that down.
        var hosted = new AsyncLocalTenantContext();
        using var db = _h.Open(hosted);
        using var store = new TurnLogSwitchStore(db, () => new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc));

        // No scope is entered anywhere in this test. Every call below would throw against a scoped context.
        store.Set("acct-a", TurnLogSwitchEntity.Any, enabled: true, actor: "soren", reason: "our own fleet");
        store.Start();

        Assert.True(store.IsEnabled("acct-a", "director-1"));
        Assert.Single(store.All());
    }

    [Fact]
    public void Set_TheSaveSucceedsButTheReREADFails_TheOFFStillTakesEffect()
    {
        // THE SECURITY REVIEW'S FINDING, and the worst one in this class. Set used to commit the row and
        // then call Refresh() to make it take effect. Refresh swallows a failed read and keeps whatever it
        // already had - so a committed OFF could be thrown away by a database blip while the endpoint
        // answered "recorded", and an administrator was told capture had stopped when it had not.
        var store = NewStore();
        store.Set("acct-a", TurnLogSwitchEntity.Any, enabled: true, actor: "soren", reason: "on first");
        Assert.True(store.IsEnabled("acct-a", "director-1"));

        // From here every re-read fails.
        store.ReaderForTest = () => throw new InvalidOperationException("the database is unreachable");

        store.Set("acct-a", TurnLogSwitchEntity.Any, enabled: false, actor: "soren", reason: "they withdrew");

        Assert.False(store.IsEnabled("acct-a", "director-1"));
    }

    [Fact]
    public void IsEnabled_TheDecisionsCannotBeReREADForTooLong_CaptureSTOPS()
    {
        // A cache trusted forever is a way for a withdrawal never to arrive: every instance would go on
        // capturing from the last answer it held for as long as the outage lasted. Losing records during an
        // outage is a gap in a corpus; capturing somebody who withdrew is a broken promise.
        var now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        using var store = new TurnLogSwitchStore(Db, () => now);
        store.Set("acct-a", TurnLogSwitchEntity.Any, enabled: true, actor: "soren", reason: "on");
        store.Start();
        Assert.True(store.IsEnabled("acct-a", "director-1"));

        // The clock moves past the window with no successful read in between.
        now = now.Add(TurnLogSwitchStore.MaxTrustedStaleness).AddSeconds(1);
        store.ReaderForTest = () => throw new InvalidOperationException("still unreachable");
        store.Refresh();

        Assert.False(store.IsEnabled("acct-a", "director-1"));
    }

    [Theory]
    [InlineData("DIRECTOR-2")]
    [InlineData("director-2")]
    [InlineData("Director-2")]
    public void IsEnabled_AnOffRowWrittenInANYCase_StillProtectsThatMachine(string offSpelling)
    {
        // The pushed roster keys Directors case-insensitively, so an ordinal comparison here let a
        // deliberate OFF sit in the table looking recorded while a wider ON went on capturing the machine
        // it was meant to protect. For a privacy switch the comparison has to be the looser one.
        var store = NewStore();
        store.Set("acct-a", TurnLogSwitchEntity.Any, enabled: true, actor: "soren", reason: "the account");
        store.Set("acct-a", offSpelling, enabled: false, actor: "soren", reason: "not this machine");

        Assert.False(store.IsEnabled("acct-a", "director-2"));
        Assert.False(store.IsEnabled("acct-a", "DIRECTOR-2"));
        Assert.True(store.IsEnabled("acct-a", "director-9"));
    }

    [Fact]
    public void Clean_StripsControlCharactersSoAnIdentifierCannotForgeALogLine()
    {
        // A session or machine identifier is caller-supplied text that reaches a line-oriented log, so an
        // embedded newline lets a caller forge an entry that reads exactly like one of ours.
        var forged = "sid-1" + (char)10 + "2026-09-05 00:00:00 [TurnLogSwitchStore] capture ON for everything";

        var cleaned = TurnLogSwitchStore.Clean(forged);

        Assert.DoesNotContain(((char)10).ToString(), cleaned);
        Assert.DoesNotContain(((char)13).ToString(), cleaned);
        Assert.True(cleaned.Length <= 80);
    }

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
    public void IsEnabled_OneMachineOffInsideAnAccountThatIsOn_TheOffDecisionWins()
    {
        // The reason this matters: a noisy machine can be left out without switching the account off.
        var store = NewStore();
        store.Set("acct-a", TurnLogSwitchEntity.Any, enabled: true, actor: "soren", reason: "our own fleet");
        store.Set("acct-a", "director-2", enabled: false, actor: "soren", reason: "this one is too chatty");

        Assert.True(store.IsEnabled("acct-a", "director-1"));
        Assert.False(store.IsEnabled("acct-a", "director-2"));
    }

    [Fact]
    public void IsEnabled_AMachineSwitchedOffAcrossEveryAccount_BeatsAnAccountThatIsOn()
    {
        // Ranking scopes against each other - is a rule about one machine narrower than a rule about one
        // account? - has no obvious answer and got this wrong once already. The rule is simply that OFF
        // wins, so a person who switches something off never has to work out what silently outranks them.
        var store = NewStore();
        store.Set("acct-a", TurnLogSwitchEntity.Any, enabled: true, actor: "soren", reason: "our own fleet");
        store.Set(TurnLogSwitchEntity.Any, "director-7", enabled: false, actor: "soren", reason: "this computer is never recorded");

        Assert.False(store.IsEnabled("acct-a", "director-7"));
        Assert.True(store.IsEnabled("acct-a", "director-1"));
    }

    [Fact]
    public void IsEnabled_BeforeItHasBeenPrimed_IsOff()
    {
        // A Gateway that has not managed its first read captures nothing. Late is a few missing records;
        // wrong the other way is recording an account that never agreed.
        using var store = new TurnLogSwitchStore(Db, () => new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc));
        var primed = NewStore();
        primed.Set(TurnLogSwitchEntity.Any, TurnLogSwitchEntity.Any, enabled: true, actor: "soren", reason: "the whole fleet");

        Assert.False(store.IsEnabled("acct-a", "director-1"));
        store.Start();
        Assert.True(store.IsEnabled("acct-a", "director-1"));
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
