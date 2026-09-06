using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Throttle;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Stats;

/// <summary>
/// The window <c>GET /stats/data</c> answers over (mission "Clean up Your Throttle", rulings R4, R5, R9 and
/// R15): the default is a rolling SEVEN days and says so; <c>days=N</c> is honoured only for a served choice;
/// <c>week=YYYY-Www</c> is Monday to Monday in the caller's display zone, which is how the mentor report's
/// link asks for exactly the week it covered; an explicit window is both ends or neither, ends after it
/// starts, and is never longer than the ledger keeps. Every refusal names its reason, and a window the store
/// cannot honestly answer is never served with silent zeroes at the front.
/// </summary>
public sealed class StatsPageWindowTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 16, 0, 0, DateTimeKind.Utc);
    private const string Toronto = "America/Toronto";

    private static (ThrottleWindowDto? Window, string? Error) Resolve(
        string? from = null, string? to = null, string? days = null, string? week = null, string timeZone = Toronto)
        => StatsPageEndpoint.ResolveWindow(from, to, days, week, timeZone, Now);

    // ---- the default: a rolling seven days (R5, landed directly per R15) ------------------------------

    [Fact]
    public void NoWindowAsked_AnswersARollingSevenDays_AndSaysSo()
    {
        var (window, error) = Resolve();

        Assert.Null(error);
        Assert.NotNull(window);
        Assert.True(window!.IsDefault);
        Assert.Equal(ThrottleWindowKinds.Default, window.Kind);
        Assert.Equal(7, window.Days);
        Assert.Equal(7, ThrottleDefinition.DefaultWindowDays);
        Assert.Null(window.Week);
        Assert.Equal(Now, window.ToUtc);
        Assert.Equal(Now.AddDays(-7), window.FromUtc);
        Assert.Equal("Last 7 days", window.Label);
    }

    // ---- the choices, served on every answer ---------------------------------------------------------

    [Fact]
    public void TheChoices_RideEveryAnswer_InOrder_AndTheLastIsTheRetention()
    {
        var expected = new[] { (1, "Last 24 hours"), (7, "Last 7 days"), (14, "Last 14 days"), (30, "Last 30 days") };

        foreach (var (window, _) in new[]
                 {
                     Resolve(),
                     Resolve(days: "14"),
                     Resolve(week: "2026-W35"),
                     Resolve(from: "2026-08-24T04:00:00Z", to: "2026-08-31T04:00:00Z"),
                 })
        {
            Assert.NotNull(window);
            Assert.Equal(expected, window!.Choices.Select(c => (c.Days, c.Label)).ToArray());
        }

        // The thirty is the ledger's own retention, never a number typed into the choices (#2692).
        Assert.Equal(ThrottleDefinition.RetentionDays, ThrottleWindowChoices.Days[^1]);
        Assert.Contains(ThrottleDefinition.DefaultWindowDays, ThrottleWindowChoices.Days);
    }

    // ---- days=N -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1", "Last 24 hours")]
    [InlineData("7", "Last 7 days")]
    [InlineData("14", "Last 14 days")]
    [InlineData("30", "Last 30 days")]
    public void AServedChoice_IsARollingWindowEndingNow_WithTheGatewaysLabel(string days, string label)
    {
        var (window, error) = Resolve(days: days);

        Assert.Null(error);
        Assert.False(window!.IsDefault);
        Assert.Equal(ThrottleWindowKinds.Days, window.Kind);
        Assert.Equal(int.Parse(days), window.Days);
        Assert.Equal(Now, window.ToUtc);
        Assert.Equal(Now.AddDays(-int.Parse(days)), window.FromUtc);
        Assert.Equal(label, window.Label);
    }

    [Theory]
    [InlineData("9")]
    [InlineData("31")]
    [InlineData("0")]
    [InlineData("-7")]
    [InlineData("week")]
    public void ALengthThatIsNotAChoice_IsRefused_NamingTheChoices(string days)
    {
        var (window, error) = Resolve(days: days);

        Assert.Null(window);
        Assert.Contains("'days' must be one of 1, 7, 14, 30", error);
    }

    // ---- week=YYYY-Www: Monday to Monday in the caller's zone (R5) ----------------------------------

    [Fact]
    public void AWeek_IsMondayToMonday_InTheCallersZone_ConvertedToUtc()
    {
        // The mentor report's 2026-W35 in Toronto, which is four hours behind UTC in August.
        var (window, error) = Resolve(week: "2026-W35");

        Assert.Null(error);
        Assert.False(window!.IsDefault);
        Assert.Equal(ThrottleWindowKinds.Week, window.Kind);
        Assert.Equal("2026-W35", window.Week);
        Assert.Null(window.Days);
        Assert.Equal(new DateTime(2026, 8, 24, 4, 0, 0, DateTimeKind.Utc), window.FromUtc);
        Assert.Equal(DateTimeKind.Utc, window.FromUtc.Kind);
        Assert.Equal(new DateTime(2026, 8, 31, 4, 0, 0, DateTimeKind.Utc), window.ToUtc);
        Assert.Equal("Week 35 of 2026, Monday 24 August to Sunday 30 August (America/Toronto)", window.Label);
    }

    [Fact]
    public void AWeek_InAZoneWithNoDaylightSaving_IsThatZonesMidnight()
    {
        // Tokyo is nine hours ahead of UTC all year, so Monday 00:00 there is Sunday 15:00 UTC.
        var (window, error) = Resolve(week: "2026-W35", timeZone: "Asia/Tokyo");

        Assert.Null(error);
        Assert.Equal(new DateTime(2026, 8, 23, 15, 0, 0, DateTimeKind.Utc), window!.FromUtc);
        Assert.Equal(new DateTime(2026, 8, 30, 15, 0, 0, DateTimeKind.Utc), window.ToUtc);
        Assert.Equal("Week 35 of 2026, Monday 24 August to Sunday 30 August (Asia/Tokyo)", window.Label);
    }

    [Fact]
    public void AWeekStillInProgress_IsServed_EndingAtTheNextMonday()
    {
        // Now is Saturday 5 September 2026: 2026-W36 began on Monday 31 August and ends Monday 7 September.
        var (window, error) = Resolve(week: "2026-W36");

        Assert.Null(error);
        Assert.Equal(new DateTime(2026, 8, 31, 4, 0, 0, DateTimeKind.Utc), window!.FromUtc);
        Assert.Equal(new DateTime(2026, 9, 7, 4, 0, 0, DateTimeKind.Utc), window.ToUtc);
        Assert.True(window.ToUtc > Now, "the window ends after now; the record simply stops at now");
    }

    [Theory]
    [InlineData("2026-35")]
    [InlineData("2026W35")]
    [InlineData("2026-w35")]
    [InlineData("26-W35")]
    [InlineData("2026-W5")]
    [InlineData("2026-W00")]
    [InlineData("2026-W54")]
    [InlineData("last week")]
    public void AMalformedWeek_IsRefused_SayingWhatAWeekLooksLike(string week)
    {
        var (window, error) = Resolve(week: week);

        Assert.Null(window);
        Assert.Contains("'week' must be an ISO week such as 2026-W35", error);
    }

    [Theory]
    [InlineData("2026-W31")]
    [InlineData("2026-W32")]
    public void AWeekOlderThanTheLedgerKeeps_IsRefused_SayingTheLedgerKeepsThirtyDays(string week)
    {
        // Thirty days before now is 6 August; W32's Monday (3 August) and everything earlier are gone.
        var (window, error) = Resolve(week: week);

        Assert.Null(window);
        Assert.Contains("30 days", error);
        Assert.Contains("submission ledger keeps", error);
        Assert.StartsWith("week " + week + ":", error);

        // W33 begins on 10 August and is still held.
        var (held, heldError) = Resolve(week: "2026-W33");
        Assert.Null(heldError);
        Assert.NotNull(held);
    }

    [Fact]
    public void AWeekAfterNow_IsRefused()
    {
        var (window, error) = Resolve(week: "2026-W37");

        Assert.Null(window);
        Assert.Contains("has not begun", error);
    }

    [Fact]
    public void AZoneTheRuntimeDoesNotKnow_IsALoudFailure_NotAFallbackToUtc()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(week: "2026-W35", timeZone: "Mars/Olympus_Mons"));
        Assert.Contains("Mars/Olympus_Mons", ex.Message);
    }

    // ---- two forms at once --------------------------------------------------------------------------

    [Theory]
    [InlineData("7", "2026-W35", null, null, "'days' and 'week'")]
    [InlineData("7", null, "2026-08-24T04:00:00Z", "2026-08-31T04:00:00Z", "'from' and 'to' and 'days'")]
    [InlineData(null, "2026-W35", "2026-08-24T04:00:00Z", null, "'from' and 'to' and 'week'")]
    public void TwoFormsInOneRequest_IsRefused_NamingBoth(string? days, string? week, string? from, string? to, string named)
    {
        var (window, error) = Resolve(from, to, days, week);

        Assert.Null(window);
        Assert.Contains("only one of 'days', 'week', or 'from' and 'to' may be given", error);
        Assert.Contains(named, error);
    }

    // ---- from and to: explicit UTC instants, exactly as phase three built it -------------------------

    [Fact]
    public void AnExplicitWindow_IsEchoedInUtc_WithItsOwnLabel()
    {
        var (window, error) = Resolve(from: "2026-08-24T04:00:00Z", to: "2026-08-31T04:00:00Z");

        Assert.Null(error);
        Assert.False(window!.IsDefault);
        Assert.Equal(ThrottleWindowKinds.Explicit, window.Kind);
        Assert.Null(window.Days);
        Assert.Null(window.Week);
        Assert.Equal(new DateTime(2026, 8, 24, 4, 0, 0, DateTimeKind.Utc), window.FromUtc);
        Assert.Equal(DateTimeKind.Utc, window.FromUtc.Kind);
        Assert.Equal(new DateTime(2026, 8, 31, 4, 0, 0, DateTimeKind.Utc), window.ToUtc);
        Assert.Equal("2026-08-24 04:00 to 2026-08-31 04:00 UTC", window.Label);
    }

    [Fact]
    public void AnOffsetInstant_IsNormalisedToUtc()
    {
        var (window, _) = Resolve(from: "2026-08-24T00:00:00-04:00", to: "2026-08-31T00:00:00-04:00");
        Assert.Equal(new DateTime(2026, 8, 24, 4, 0, 0, DateTimeKind.Utc), window!.FromUtc);
    }

    [Theory]
    [InlineData("2026-08-24T04:00:00Z", null)]
    [InlineData(null, "2026-08-31T04:00:00Z")]
    [InlineData("", "2026-08-31T04:00:00Z")]
    public void HalfAWindow_IsRefused(string? from, string? to)
    {
        var (window, error) = Resolve(from, to);
        Assert.Null(window);
        Assert.Contains("given together", error);
    }

    [Fact]
    public void AWindowThatEndsBeforeItStarts_IsRefused()
    {
        var (window, error) = Resolve(from: "2026-08-31T04:00:00Z", to: "2026-08-24T04:00:00Z");
        Assert.Null(window);
        Assert.Contains("later than", error);
    }

    [Fact]
    public void AWindowLongerThanTheLedgerKeeps_IsRefusedWithTheReason()
    {
        // Ninety days was the old tally's reach; the ledger keeps thirty, and the selector must never offer
        // a length the store cannot honestly answer (#2692).
        var (window, error) = Resolve(from: "2026-06-01T00:00:00Z", to: "2026-09-01T00:00:00Z");
        Assert.Null(window);
        Assert.Contains("30 days", error);
        Assert.Contains("submission ledger keeps", error);

        // Exactly the retention is fine - when it also ENDS now. The old control here ran 1 to 31 August, which
        // is exactly thirty days long and begins before the ledger's reach on 5 September; it was served, and
        // that was finding F-04. Span is not age.
        var (exact, exactError) = Resolve(from: "2026-08-06T16:00:00Z", to: "2026-09-05T16:00:00Z");
        Assert.Null(exactError);
        Assert.NotNull(exact);
        var (old, oldError) = Resolve(from: "2026-08-01T00:00:00Z", to: "2026-08-31T00:00:00Z");
        Assert.Null(old);
        Assert.Contains("begins before the 30 days", oldError);
    }

    // ---- age, not only span (final inspection finding F-04) -----------------------------------------

    [Fact]
    public void AShortExplicitWindow_FromBeforeTheLedgersReach_IsRefused_NotServedAsSilentZeroes()
    {
        // The inspector's probe: eight days in January 2020, asked in September 2026. Short enough, and gone.
        var (window, error) = Resolve(from: "2020-01-01T00:00:00Z", to: "2020-01-08T00:00:00Z");
        Assert.Null(window);
        Assert.Contains("begins before the 30 days", error);
        Assert.Contains("oldest instant it can answer is 2026-08-06T16:00:00Z", error);
    }

    [Fact]
    public void TheOldestAnswerableStart_IsExactlyRetentionAgo_JustInsideIsServed_JustOutsideIsRefused()
    {
        var oldest = Now.AddDays(-30);
        string At(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var to = At(oldest.AddDays(7));

        var (exact, exactError) = Resolve(from: At(oldest), to: to);
        Assert.Null(exactError);
        Assert.Equal(oldest, exact!.FromUtc);

        var (inside, insideError) = Resolve(from: At(oldest.AddSeconds(1)), to: to);
        Assert.Null(insideError);
        Assert.NotNull(inside);

        var (outside, outsideError) = Resolve(from: At(oldest.AddSeconds(-1)), to: to);
        Assert.Null(outside);
        Assert.Contains("begins before the 30 days", outsideError);
    }

    [Fact]
    public void AnExplicitWindowThatHasNotBegun_IsRefused()
    {
        var (window, error) = Resolve(from: "2026-09-06T00:00:00Z", to: "2026-09-07T00:00:00Z");
        Assert.Null(window);
        Assert.Contains("has not begun", error);
    }

    [Fact]
    public void TheWeekForm_HasTheSameOldestStartBoundary_JustInsideExactlyAtAndJustOutside()
    {
        // Toronto's 2026-W33 begins Monday 10 August 2026 04:00Z. With now set so that thirty days ago is one
        // second AFTER that Monday, the week is just outside; one second BEFORE, just inside; exactly at, served.
        var monday = new DateTime(2026, 8, 10, 4, 0, 0, DateTimeKind.Utc);

        var (exact, exactError) = StatsPageEndpoint.ResolveWindow(null, null, null, "2026-W33", Toronto, monday.AddDays(30));
        Assert.Null(exactError);
        Assert.Equal(monday, exact!.FromUtc);

        var (inside, insideError) = StatsPageEndpoint.ResolveWindow(null, null, null, "2026-W33", Toronto, monday.AddDays(30).AddSeconds(-1));
        Assert.Null(insideError);
        Assert.NotNull(inside);

        var (outside, outsideError) = StatsPageEndpoint.ResolveWindow(null, null, null, "2026-W33", Toronto, monday.AddDays(30).AddSeconds(1));
        Assert.Null(outside);
        Assert.Contains("week 2026-W33: the window begins before the 30 days", outsideError);
    }

    [Fact]
    public void EveryRollingChoice_IsInsideThePolicy_ByConstruction()
    {
        // The choices are at most the retention, and a rolling window ends now, so the policy admits every one.
        foreach (var days in ThrottleWindowChoices.Days)
            Assert.Null(ThrottleDefinition.WindowRefusal(Now.AddDays(-days), Now, Now));
        Assert.Null(ThrottleDefinition.WindowRefusal(Now.AddDays(-ThrottleDefinition.DefaultWindowDays), Now, Now));
    }

    // ---- the week's bounds across a clock change (the inspector's mutation guard) -------------------
    //
    // These pin WHY the week resolver is right: the end of the week is the NEXT LOCAL MONDAY converted to
    // UTC, not the start plus seven UTC days. Replacing that calculation with fromUtc.AddDays(7) left the
    // 37 shipped tests green; it does not leave these green.

    [Fact]
    public void AWeekThatSpansTheAutumnClockChange_Is169HoursLong_NotSevenUtcDays()
    {
        // Toronto's clocks go back on Sunday 1 November 2026. 2026-W44 begins Monday 26 October 00:00 EDT
        // (04:00Z) and ends Monday 2 November 00:00 EST (05:00Z): 169 hours.
        var (window, error) = StatsPageEndpoint.ResolveWindow(null, null, null, "2026-W44", Toronto, new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc));
        Assert.Null(error);
        Assert.Equal(new DateTime(2026, 10, 26, 4, 0, 0, DateTimeKind.Utc), window!.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc), window.ToUtc);
        Assert.Equal(TimeSpan.FromHours(169), window.ToUtc - window.FromUtc);
    }

    [Fact]
    public void AWeekThatSpansTheSpringClockChange_InAHalfHourZone_Is167HoursLong()
    {
        // Adelaide is UTC+9:30 and moves to +10:30 on Sunday 4 October 2026. 2026-W40 begins Monday
        // 28 September 00:00 ACST (27 September 14:30Z) and ends Monday 5 October 00:00 ACDT (4 October
        // 13:30Z): 167 hours.
        var (window, error) = StatsPageEndpoint.ResolveWindow(null, null, null, "2026-W40", "Australia/Adelaide", new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));
        Assert.Null(error);
        Assert.Equal(new DateTime(2026, 9, 27, 14, 30, 0, DateTimeKind.Utc), window!.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 4, 13, 30, 0, DateTimeKind.Utc), window.ToUtc);
        Assert.Equal(TimeSpan.FromHours(167), window.ToUtc - window.FromUtc);
    }

    [Fact]
    public void GarbageInstants_AreRefused()
    {
        var (window, error) = Resolve(from: "last tuesday", to: "2026-08-31T04:00:00Z");
        Assert.Null(window);
        Assert.Contains("'from'", error);
    }
}
