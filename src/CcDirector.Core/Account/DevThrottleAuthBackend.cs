using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The DevThrottle backend's authentication endpoint and the public client key needed to call it
/// (issue #876; the embedded key is issue #911). The refresh exchange is
/// <c>POST {auth}/token?grant_type=refresh_token</c> with the backend's ANONYMOUS (publishable) key
/// in the <c>apikey</c> header. Both the endpoint and the anonymous key are embedded so every install
/// has a stable production target that works out of the box, each with an environment-variable
/// override for non-production targets.
///
/// The anonymous key is deliberately PUBLIC: it is the Supabase publishable key that already ships in
/// the website's browser bundle (<c>VITE_SUPABASE_ANON_KEY</c>) and carries only the "anon" role, so
/// embedding it here leaks nothing. It is NOT a service-role or any secret key - those must never be
/// embedded. Before #911 the Gateway had no key configured, so the refresh exchange sent no
/// <c>apikey</c> header and Supabase answered 401 "No API key found in request", which stalled every
/// token renewal about an hour after sign-in.
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
    /// The backend's PUBLIC anonymous (publishable) key, embedded at build time. This is the same
    /// Supabase "anon"-role key that ships in the website's browser bundle
    /// (<c>VITE_SUPABASE_ANON_KEY</c>) for project <c>ompujpfrglgqvqprilxa</c>; it is deliberately
    /// public and is safe to embed. It is NOT a secret or service-role key. Sent as the <c>apikey</c>
    /// header on the refresh exchange, which the endpoint requires (it answers 401 "No API key found
    /// in request" without it).
    /// </summary>
    public const string ProductionAnonymousKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9tcHVqcGZyZ2xncXZxcHJpbHhhIiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODE2MTQ4OTksImV4cCI6MjA5NzE5MDg5OX0.YKq4AK2af5O0HbI9Q6ujaFrvRbLDeY8HSn-OdK6RAgo";

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
    /// Resolves the public anonymous key sent as the <c>apikey</c> header: the environment override
    /// when set, otherwise the embedded production anonymous key. Never null - since #911 the Gateway
    /// always has the publishable key configured, so the refresh exchange always carries the required
    /// <c>apikey</c> header.
    /// </summary>
    public static string ResolveAnonymousKey()
    {
        var overrideValue = Environment.GetEnvironmentVariable(AnonymousKeyEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            FileLog.Write($"[DevThrottleAuthBackend] ResolveAnonymousKey: anonymous key resolved from {AnonymousKeyEnvVar}");
            return overrideValue.Trim();
        }

        return ProductionAnonymousKey;
    }
}
