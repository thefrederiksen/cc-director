using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The verdict store (devthrottle #2115): one row per tenant per normalized term, upserted, tenant-isolated.
/// This persistence is what makes "a term is judged at most once, ever" true across restarts.
/// </summary>
public sealed class DictionarySuggestionVerdictStoreTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Base = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Record_ThenRead_RoundTripsByNormalizedTerm()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionVerdictStore(h.Open());

        store.Record(TenantA, new[]
        {
            new DictionarySuggestionVerdictStore.Verdict("mindzie", true, "brand"),
            new DictionarySuggestionVerdictStore.Verdict("that", false, "ordinary word"),
        }, "model-x", Base);

        var verdicts = store.VerdictsByNorm(TenantA);
        Assert.Equal(2, verdicts.Count);
        Assert.True(verdicts["mindzie"]);
        Assert.False(verdicts["that"]);
    }

    [Fact]
    public void Record_UpsertsByNormalizedTerm_NoDuplicateForCasing()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionVerdictStore(h.Open());

        store.Record(TenantA, new[] { new DictionarySuggestionVerdictStore.Verdict("ConPty", false, "first") }, "m", Base);
        // Re-judged under different casing/punctuation: same normalized key, so the row is REPLACED.
        store.Record(TenantA, new[] { new DictionarySuggestionVerdictStore.Verdict("Con-Pty", true, "second") }, "m", Base.AddDays(1));

        var verdicts = store.VerdictsByNorm(TenantA);
        Assert.True(Assert.Single(verdicts).Value);
        Assert.Equal("conpty", Assert.Single(verdicts).Key);
    }

    [Fact]
    public void Verdicts_AreTenantIsolated()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionVerdictStore(h.Open());

        store.Record(TenantA, new[] { new DictionarySuggestionVerdictStore.Verdict("mindzie", true, "") }, "m", Base);
        store.Record(TenantB, new[] { new DictionarySuggestionVerdictStore.Verdict("mindzie", false, "") }, "m", Base);

        Assert.True(store.VerdictsByNorm(TenantA)["mindzie"]);
        Assert.False(store.VerdictsByNorm(TenantB)["mindzie"]);
    }

    [Fact]
    public void Record_SkipsTermsThatNormalizeToEmpty()
    {
        using var h = new GatewayDbTestHarness();
        var store = new DictionarySuggestionVerdictStore(h.Open());
        store.Record(TenantA, new[] { new DictionarySuggestionVerdictStore.Verdict("--- ", true, "") }, "m", Base);
        Assert.Empty(store.VerdictsByNorm(TenantA));
    }
}
