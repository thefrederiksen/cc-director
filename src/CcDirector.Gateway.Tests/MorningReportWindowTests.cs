using System;
using CcDirector.Gateway.Reports;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The window resolver (issue #2119): "the calendar day D in zone Z" -> the exact half-open UTC range the
/// report measures over. Every number in the morning email carries these coordinates, so getting them wrong
/// silently moves work between days.
///
/// The claim these tests exist for is the DAYLIGHT-SAVING one. A window computed as "start plus 24 hours"
/// passes every ordinary-day test and is wrong twice a year: it puts an hour of a spring-forward day into
/// the next report and drops an hour of an autumn-back day out of both. Revert-prove: change
/// <c>MorningReportWindow</c> to compute the end as <c>startUtc.AddDays(1)</c> and the two transition tests
/// below go RED while the ordinary-day tests stay green.
/// </summary>
public sealed class MorningReportWindowTests
{
    private const string Toronto = "America/Toronto";

    [Fact]
    public void An_ordinary_day_is_twenty_four_hours_and_starts_at_local_midnight()
    {
        var w = MorningReportWindow.Resolve("2026-07-23", Toronto);

        // Toronto is UTC-4 in July, so local midnight on the 23rd is 04:00 UTC.
        Assert.Equal(new DateTime(2026, 7, 23, 4, 0, 0, DateTimeKind.Utc), w.StartUtc);
        Assert.Equal(new DateTime(2026, 7, 24, 4, 0, 0, DateTimeKind.Utc), w.EndUtc);
        Assert.Equal(24, (w.EndUtc - w.StartUtc).TotalHours);
        Assert.Equal("2026-07-23", w.Date);
        Assert.Equal(Toronto, w.Tz);
    }

    [Fact]
    public void A_winter_day_uses_the_standard_time_offset_not_the_summer_one()
    {
        var w = MorningReportWindow.Resolve("2026-01-15", Toronto);

        // UTC-5 in January.
        Assert.Equal(new DateTime(2026, 1, 15, 5, 0, 0, DateTimeKind.Utc), w.StartUtc);
        Assert.Equal(24, (w.EndUtc - w.StartUtc).TotalHours);
    }

    [Fact]
    public void The_spring_forward_day_is_twenty_three_hours_long()
    {
        // 8 March 2026: North American clocks go forward at 02:00 local. The day is 23 hours.
        var w = MorningReportWindow.Resolve("2026-03-08", Toronto);

        Assert.Equal(new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc), w.StartUtc);
        Assert.Equal(new DateTime(2026, 3, 9, 4, 0, 0, DateTimeKind.Utc), w.EndUtc);
        Assert.Equal(23, (w.EndUtc - w.StartUtc).TotalHours);
    }

    [Fact]
    public void The_autumn_back_day_is_twenty_five_hours_long()
    {
        // 1 November 2026: clocks go back at 02:00 local. The day is 25 hours.
        var w = MorningReportWindow.Resolve("2026-11-01", Toronto);

        Assert.Equal(new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc), w.StartUtc);
        Assert.Equal(new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc), w.EndUtc);
        Assert.Equal(25, (w.EndUtc - w.StartUtc).TotalHours);
    }

    [Fact]
    public void Consecutive_days_abut_exactly_across_a_transition()
    {
        // The end of one day IS the start of the next - no gap, no overlap - even across the transition.
        // A gap loses work; an overlap reports the same work twice.
        var before = MorningReportWindow.Resolve("2026-03-07", Toronto);
        var during = MorningReportWindow.Resolve("2026-03-08", Toronto);
        var after = MorningReportWindow.Resolve("2026-03-09", Toronto);

        Assert.Equal(before.EndUtc, during.StartUtc);
        Assert.Equal(during.EndUtc, after.StartUtc);
    }

    [Fact]
    public void A_zone_whose_midnight_does_not_exist_starts_at_the_first_instant_that_does()
    {
        // Chile springs forward AT midnight, so 00:00 local simply does not occur on that date and the day
        // begins at 01:00. Resolving it must not throw and must not silently produce a nonexistent instant.
        var w = MorningReportWindow.Resolve("2026-09-06", "America/Santiago");

        Assert.True(w.EndUtc > w.StartUtc);
        Assert.Equal(23, (w.EndUtc - w.StartUtc).TotalHours);

        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(w.StartUtc, zone);
        Assert.Equal(new DateTime(2026, 9, 6), localStart.Date);
        Assert.Equal(1, localStart.Hour);
    }

    [Fact]
    public void A_zone_far_from_UTC_resolves_its_own_midnight()
    {
        var w = MorningReportWindow.Resolve("2026-07-23", "Asia/Tokyo");

        // UTC+9, no daylight saving: local midnight on the 23rd is 15:00 UTC on the 22nd.
        Assert.Equal(new DateTime(2026, 7, 22, 15, 0, 0, DateTimeKind.Utc), w.StartUtc);
        Assert.Equal(24, (w.EndUtc - w.StartUtc).TotalHours);
    }

    [Theory]
    [InlineData(null, Toronto)]
    [InlineData("2026-07-23", null)]
    [InlineData("", Toronto)]
    [InlineData("23-07-2026", Toronto)]
    [InlineData("2026-07-23T00:00:00", Toronto)]
    [InlineData("not-a-date", Toronto)]
    [InlineData("2026-07-23", "Mars/Olympus_Mons")]
    public void An_unusable_date_or_zone_is_refused_never_defaulted(string? date, string? tz)
    {
        // Defaulting a missing or unparseable coordinate would produce a report over a window nobody asked
        // for - which is exactly the kind of confidently-wrong number this slice must not emit.
        Assert.Throws<MorningReportWindowException>(() => MorningReportWindow.Resolve(date, tz));
    }
}
