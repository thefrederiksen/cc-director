namespace CcDirector.Core.Claude;

public sealed class ClaudeUsageInfo
{
    public string AccountId { get; init; } = "";
    public string AccountLabel { get; init; } = "";
    public string SubscriptionType { get; init; } = "";
    public string RateLimitTier { get; init; } = "";
    public double FiveHourUtilization { get; init; }
    public DateTimeOffset? FiveHourResetsAt { get; init; }
    public double SevenDayUtilization { get; init; }
    public DateTimeOffset? SevenDayResetsAt { get; init; }
    public double? OpusUtilization { get; init; }
    public DateTimeOffset? OpusResetsAt { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public bool IsStale { get; init; }
}
