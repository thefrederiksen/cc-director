using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Throttle;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Stats;

/// <summary>
/// The window <c>GET /stats/data</c> answers over (mission "Clean up Your Throttle", rulings R4 and R9): the
/// default is the ledger's whole retention and says so; an explicit window is both ends or neither, ends
/// after it starts, and is never longer than the ledger keeps - a window the store cannot honestly answer is
/// refused with the reason, never served with silent zeroes at the front.
/// </summary>
public sealed class StatsPageWindowTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoWindowAsked_AnswersTheLedgersWholeRetention_AndSaysSo()
    {
        var (window, error) = StatsPageEndpoint.ResolveWindow(null, null, Now);

        Assert.Null(error);
        Assert.NotNull(window);
        Assert.True(window!.IsDefault);
        Assert.Equal(Now, window.ToUtc);
        Assert.Equal(Now.AddDays(-ThrottleDefinition.RetentionDays), window.FromUtc);
        Assert.Equal("Last 30 days", window.Label);
    }

    [Fact]
    public void AnExplicitWindow_IsEchoedInUtc_WithItsOwnLabel()
    {
        // The mentor report's 2026-W35, Monday to Monday in Toronto, as the link from the report will ask.
        var (window, error) = StatsPageEndpoint.ResolveWindow("2026-08-24T04:00:00Z", "2026-08-31T04:00:00Z", Now);

        Assert.Null(error);
        Assert.False(window!.IsDefault);
        Assert.Equal(new DateTime(2026, 8, 24, 4, 0, 0, DateTimeKind.Utc), window.FromUtc);
        Assert.Equal(DateTimeKind.Utc, window.FromUtc.Kind);
        Assert.Equal(new DateTime(2026, 8, 31, 4, 0, 0, DateTimeKind.Utc), window.ToUtc);
        Assert.Equal("2026-08-24 04:00 to 2026-08-31 04:00 UTC", window.Label);
    }

    [Fact]
    public void AnOffsetInstant_IsNormalisedToUtc()
    {
        var (window, _) = StatsPageEndpoint.ResolveWindow("2026-08-24T00:00:00-04:00", "2026-08-31T00:00:00-04:00", Now);
        Assert.Equal(new DateTime(2026, 8, 24, 4, 0, 0, DateTimeKind.Utc), window!.FromUtc);
    }

    [Theory]
    [InlineData("2026-08-24T04:00:00Z", null)]
    [InlineData(null, "2026-08-31T04:00:00Z")]
    [InlineData("", "2026-08-31T04:00:00Z")]
    public void HalfAWindow_IsRefused(string? from, string? to)
    {
        var (window, error) = StatsPageEndpoint.ResolveWindow(from, to, Now);
        Assert.Null(window);
        Assert.Contains("given together", error);
    }

    [Fact]
    public void AWindowThatEndsBeforeItStarts_IsRefused()
    {
        var (window, error) = StatsPageEndpoint.ResolveWindow("2026-08-31T04:00:00Z", "2026-08-24T04:00:00Z", Now);
        Assert.Null(window);
        Assert.Contains("later than", error);
    }

    [Fact]
    public void AWindowLongerThanTheLedgerKeeps_IsRefusedWithTheReason()
    {
        // Ninety days was the old tally's reach; the ledger keeps thirty, and the selector must never offer
        // a length the store cannot honestly answer (#2692).
        var (window, error) = StatsPageEndpoint.ResolveWindow("2026-06-01T00:00:00Z", "2026-09-01T00:00:00Z", Now);
        Assert.Null(window);
        Assert.Contains("30 days", error);
        Assert.Contains("submission ledger keeps", error);

        // Exactly the retention is fine.
        var (exact, exactError) = StatsPageEndpoint.ResolveWindow("2026-08-01T00:00:00Z", "2026-08-31T00:00:00Z", Now);
        Assert.Null(exactError);
        Assert.NotNull(exact);
    }

    [Fact]
    public void GarbageInstants_AreRefused()
    {
        var (window, error) = StatsPageEndpoint.ResolveWindow("last tuesday", "2026-08-31T04:00:00Z", Now);
        Assert.Null(window);
        Assert.Contains("'from'", error);
    }
}
