using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The scan-result store (devthrottle #2115): one row per tenant holding the latest scan (its time, the
/// screening outcome, and the approved suggestions with evidence), overwritten per scan, edited in place by
/// apply/dismiss, tenant-isolated. The badge and the page read this row - so its round-trip fidelity IS the
/// page's correctness.
/// </summary>
public sealed class DictionarySuggestionScanStoreTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Base = new(2026, 7, 24, 0, 5, 0, DateTimeKind.Utc);

    private static MistranscriptionSuggestion Suggestion(string term, params (string heard, int count)[] variants)
        => new(term, variants.Select(v => new MistranscriptionVariant(v.heard, v.count)).ToList(),
            variants.Sum(v => v.count), variants.Sum(v => v.count) + 10);

    [Fact]
    public void SaveThenGet_RoundTripsEverything()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionScanStore(h.Open());

        store.Save(TenantA, new DictionarySuggestionScanStore.ScanResult(
            Base, false, "model unreachable",
            new[] { Suggestion("mindzie", ("Mindsee", 20), ("Mindzee", 12)) }));

        var got = store.Get(TenantA);
        Assert.NotNull(got);
        Assert.Equal(Base, got!.ScannedAtUtc);
        Assert.Equal(DateTimeKind.Utc, got.ScannedAtUtc.Kind);
        Assert.False(got.ScreeningOk);
        Assert.Equal("model unreachable", got.ScreeningError);
        var s = Assert.Single(got.Suggestions);
        Assert.Equal("mindzie", s.Term);
        Assert.Equal(32, s.WrongCount);
        Assert.Equal(42, s.TotalCount);
        Assert.Equal(2, s.Variants.Count);
        Assert.Equal("Mindsee", s.Variants[0].Heard);
        Assert.Equal(20, s.Variants[0].Count);
    }

    [Fact]
    public void Get_NoScanEver_ReturnsNull()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionScanStore(h.Open());
        Assert.Null(store.Get(TenantA));
    }

    [Fact]
    public void Save_OverwritesThePreviousScan()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionScanStore(h.Open());

        store.Save(TenantA, new DictionarySuggestionScanStore.ScanResult(
            Base, true, "", new[] { Suggestion("mindzie", ("Mindsee", 20)) }));
        store.Save(TenantA, new DictionarySuggestionScanStore.ScanResult(
            Base.AddDays(1), true, "", new[] { Suggestion("ConPty", ("Con-TY", 9)) }));

        var got = store.Get(TenantA)!;
        Assert.Equal(Base.AddDays(1), got.ScannedAtUtc);
        Assert.Equal("ConPty", Assert.Single(got.Suggestions).Term);
    }

    [Fact]
    public void RemoveSuggestion_EditsTheStoredListInPlace()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionScanStore(h.Open());
        store.Save(TenantA, new DictionarySuggestionScanStore.ScanResult(
            Base, true, "",
            new[] { Suggestion("mindzie", ("Mindsee", 20)), Suggestion("ConPty", ("Con-TY", 9)) }));

        store.RemoveSuggestion(TenantA, "MIND-ZIE"); // normalized match
        Assert.Equal("ConPty", Assert.Single(store.Get(TenantA)!.Suggestions).Term);

        store.RemoveSuggestion(TenantA, "notthere"); // no-op, no throw
        Assert.Single(store.Get(TenantA)!.Suggestions);

        // The scan time is NOT touched by an in-place edit (it still answers "when did we last scan").
        Assert.Equal(Base, store.Get(TenantA)!.ScannedAtUtc);
    }

    [Fact]
    public void Scans_AreTenantIsolated()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionScanStore(h.Open());
        store.Save(TenantA, new DictionarySuggestionScanStore.ScanResult(
            Base, true, "", new[] { Suggestion("mindzie", ("Mindsee", 20)) }));

        Assert.Null(store.Get(TenantB));
        store.Save(TenantB, new DictionarySuggestionScanStore.ScanResult(
            Base.AddHours(1), true, "", Array.Empty<MistranscriptionSuggestion>()));
        Assert.Equal("mindzie", Assert.Single(store.Get(TenantA)!.Suggestions).Term);
        Assert.Empty(store.Get(TenantB)!.Suggestions);
    }
}
