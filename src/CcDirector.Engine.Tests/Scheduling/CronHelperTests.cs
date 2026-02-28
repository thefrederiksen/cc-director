using CcDirector.Engine.Scheduling;
using Xunit;

namespace CcDirector.Engine.Tests.Scheduling;

public sealed class CronHelperTests
{
    [Theory]
    [InlineData("* * * * *")]
    [InlineData("*/15 * * * *")]
    [InlineData("0 9 * * 1-5")]
    [InlineData("0 0 1 * *")]
    public void IsValid_ValidExpressions_ReturnsTrue(string cron)
    {
        Assert.True(CronHelper.IsValid(cron));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a cron")]
    [InlineData("* * *")]
    [InlineData("60 * * * *")]
    public void IsValid_InvalidExpressions_ReturnsFalse(string cron)
    {
        Assert.False(CronHelper.IsValid(cron));
    }

    [Fact]
    public void GetNextOccurrence_EveryMinute_ReturnsNextMinute()
    {
        var from = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var next = CronHelper.GetNextOccurrence("* * * * *", from);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 1, 1, 12, 1, 0, DateTimeKind.Utc), next.Value);
    }

    [Fact]
    public void GetNextOccurrence_Every15Minutes_ReturnsCorrectTime()
    {
        var from = new DateTime(2026, 1, 1, 12, 3, 0, DateTimeKind.Utc);
        var next = CronHelper.GetNextOccurrence("*/15 * * * *", from);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 1, 1, 12, 15, 0, DateTimeKind.Utc), next.Value);
    }

    [Fact]
    public void GetNextOccurrence_DailyAt9AM_ReturnsNextDay()
    {
        var from = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var next = CronHelper.GetNextOccurrence("0 9 * * *", from);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc), next.Value);
    }

    [Fact]
    public void GetNextOccurrenceUtc_ReturnsNonNull()
    {
        var next = CronHelper.GetNextOccurrenceUtc("* * * * *");
        Assert.NotNull(next);
    }

    [Fact]
    public void Describe_ValidCron_ContainsNext()
    {
        var desc = CronHelper.Describe("* * * * *");
        Assert.Contains("Next:", desc);
    }

    [Fact]
    public void Describe_InvalidCron_ReturnsInvalid()
    {
        var desc = CronHelper.Describe("bad");
        Assert.Equal("Invalid cron expression", desc);
    }
}
