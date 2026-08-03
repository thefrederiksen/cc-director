using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Emptying one tenant's stored dictation transcripts, and reading how much is there.
///
/// These are the operations any "close this account", "clear my history", or retention check needs, so they
/// are proved on their own terms rather than through whatever screen happens to call them first. Two tenants
/// over one database throughout, because the failure that would matter most - a delete or a count that reaches
/// across accounts - is exactly the shape a missing tenant scope produces, and it is invisible from inside a
/// single-tenant test.
/// </summary>
public sealed class TranscriptStoreDeleteAndCountTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Base = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    // ---- delete -----------------------------------------------------------------------------------------

    /// <summary>The delete empties this tenant's partition and reports the number that actually went.</summary>
    [Fact]
    public void DeleteAll_EmptiesTheTenantAndReportsTheCount()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());
        for (var i = 0; i < 7; i++)
            store.Append(TenantA, "dictation", $"utterance {i}", null, false, null, Base.AddSeconds(i));

        Assert.Equal(7, store.DeleteAll(TenantA));
        Assert.Equal(0, store.Count(TenantA));
    }

    /// <summary>
    /// ONE ACCOUNT CAN NEVER DELETE ANOTHER'S. Asserted directly rather than assumed to follow from the global
    /// query filter: this is the single most damaging way this method could fail, it would be silent, and the
    /// data it destroys is speech that cannot be recovered.
    /// </summary>
    [Fact]
    public void DeleteAll_NeverTouchesAnotherTenant()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());
        store.Append(TenantA, "dictation", "alpha", null, false, null, Base);
        store.Append(TenantB, "dictation", "bravo one", null, false, null, Base);
        store.Append(TenantB, "dictation", "bravo two", null, false, null, Base.AddSeconds(1));

        Assert.Equal(1, store.DeleteAll(TenantA));

        Assert.Equal(0, store.Count(TenantA));
        Assert.Equal(2, store.Count(TenantB));
    }

    /// <summary>Deleting nothing reports zero rather than throwing - a second press is not an error.</summary>
    [Fact]
    public void DeleteAll_WithNothingStored_ReportsZero()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        Assert.Equal(0, store.DeleteAll(TenantA));
    }

    /// <summary>A blank tenant must never reach a query as an ambient fallback.</summary>
    [Fact]
    public void DeleteAll_InvalidTenant_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        Assert.Throws<ArgumentException>(() => store.DeleteAll(default));
    }

    // ---- count and age ----------------------------------------------------------------------------------

    /// <summary>
    /// The count and the oldest timestamp come back together, for this tenant only. The rows are appended
    /// NEWEST FIRST on purpose: a version that returned the first row it saw rather than the minimum timestamp
    /// would pass against naturally-ordered data and fail here.
    /// </summary>
    [Fact]
    public void Stats_ReportsTheCountAndTheOldestForThisTenantOnly()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());
        store.Append(TenantA, "dictation", "newest", null, false, null, Base);
        store.Append(TenantA, "dictation", "oldest", null, false, null, Base.AddDays(-29));
        store.Append(TenantA, "dictation", "middle", null, false, null, Base.AddDays(-3));
        store.Append(TenantB, "dictation", "much older, another account", null, false, null, Base.AddDays(-100));

        var (count, oldest) = store.Stats(TenantA);

        Assert.Equal(3, count);
        Assert.Equal(Base.AddDays(-29), oldest);
    }

    /// <summary>With nothing stored there is no oldest. Null, not the epoch and not "today" - both of those
    /// would read to a caller as a fact about data that does not exist.</summary>
    [Fact]
    public void Stats_WithNothingStored_HasNoOldest()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        var (count, oldest) = store.Stats(TenantA);

        Assert.Equal(0, count);
        Assert.Null(oldest);
    }

    /// <summary>The count and the age agree after a delete, because they are read in one pass.</summary>
    [Fact]
    public void Stats_AfterADelete_ReportsAnEmptyStore()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());
        store.Append(TenantA, "dictation", "something", null, false, null, Base.AddDays(-5));
        store.DeleteAll(TenantA);

        var (count, oldest) = store.Stats(TenantA);

        Assert.Equal(0, count);
        Assert.Null(oldest);
    }

    [Fact]
    public void Stats_InvalidTenant_Throws()
    {
        using var h = new GatewayDbTestHarness();
        var store = new TranscriptStore(h.Open());

        Assert.Throws<ArgumentException>(() => store.Stats(default));
    }
}
