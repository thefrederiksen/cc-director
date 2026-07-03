using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The DevThrottle backend's authentication endpoint and the public client key needed to call it
/// (issue #876). The refresh exchange is
/// <c>POST {auth}/token?grant_type=refresh_token</c> with the backend's ANONYMOUS key in the
/// <c>apikey</c> header. The endpoint is embedded so installs have a stable production target, but
/// the key is intentionally supplied from configuration so this public repository does not contain
/// API key material.
/// </summary>
public static class DevThrottleAuthBackend
{
    /// <summary>The environment variable that overrides the refresh-exchange endpoint. Unset in normal production use.</summary>
    public const string RefreshUrlEnvVar = "DEVTHROTTLE_REFRESH_URL";

    /// <summary>The environment variable that overrides the public anonymous key. Unset in normal production use.</summary>
    public const string AnonymousKeyEnvVar = "DEVTHROTTLE_AUTH_ANONYMOUS_KEY";

    /// <summary>The backend's refresh-exchange endpoint, embedded at build time.</summary>
    public const string ProductionRefreshUrl =
        "https://ompujpfrglgqvqprilxa.supabase.co/auth/v1/token?grant_type=refresh_token";

    /// <summary>
    /// Resolves the refresh-exchange endpoint: the environment override when set, otherwise the
    /// embedded production endpoint. Never null - since #876 the refresh exchange is always
    /// configured.
    /// </summary>
    public static string ResolveRefreshUrl()
    {
        var overrideValue = Environment.GetEnvironmentVariable(RefreshUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            FileLog.Write($"[DevThrottleAuthBackend] ResolveRefreshUrl: refresh endpoint resolved from {RefreshUrlEnvVar}");
            return overrideValue.Trim();
        }

        return ProductionRefreshUrl;
    }

    /// <summary>
    /// Resolves the public anonymous key sent as the <c>apikey</c> header. A missing value returns
    /// null so callers can classify refresh as unavailable without logging or inventing a key.
    /// </summary>
    public static string? ResolveAnonymousKey()
    {
        var overrideValue = Environment.GetEnvironmentVariable(AnonymousKeyEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            FileLog.Write($"[DevThrottleAuthBackend] ResolveAnonymousKey: anonymous key resolved from {AnonymousKeyEnvVar}");
            return overrideValue.Trim();
        }

        FileLog.Write($"[DevThrottleAuthBackend] ResolveAnonymousKey: {AnonymousKeyEnvVar} is not configured");
        return null;
    }
}
