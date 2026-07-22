namespace CcDirector.Core.Account;

/// <summary>
/// Shared DevThrottle website and API base-address resolution for account and hosted-service calls.
/// </summary>
public static class DevThrottleApi
{
    /// <summary>
    /// Optional development/QA override for the DevThrottle website and API base address.
    /// </summary>
    public const string BaseUrlEnvVar = "DEVTHROTTLE_API_URL";

    /// <summary>The production website and API base address.</summary>
    public const string DefaultBaseUrl = "https://devthrottle.com";

    /// <summary>
    /// Resolves an explicit base address, then the environment override, then production.
    /// The result never carries a trailing slash.
    /// </summary>
    public static string ResolveBaseUrl(string? explicitBaseUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitBaseUrl))
            return explicitBaseUrl.Trim().TrimEnd('/');

        var fromEnv = Environment.GetEnvironmentVariable(BaseUrlEnvVar);
        return string.IsNullOrWhiteSpace(fromEnv)
            ? DefaultBaseUrl
            : fromEnv.Trim().TrimEnd('/');
    }
}
