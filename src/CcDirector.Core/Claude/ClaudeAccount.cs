namespace CcDirector.Core.Claude;

public sealed class ClaudeAccount
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string SubscriptionType { get; set; } = "";
    public string RateLimitTier { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public long ExpiresAt { get; set; }
    public bool IsActive { get; set; }
}
