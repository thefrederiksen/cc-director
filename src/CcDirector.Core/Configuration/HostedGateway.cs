namespace CcDirector.Core.Configuration;

/// <summary>
/// Where DevThrottle's HOSTED Gateway lives. Hosted is ONE shared, multi-tenant Gateway that every hosted
/// account joins - it is NOT a gateway per customer and there is nothing to provision. An account does not
/// receive a gateway; it BECOMES A TENANT on this one, and that tenant binding is created by the enrollment
/// itself (<c>POST /devices/enroll-hosted</c>).
///
/// This is a single named address rather than something the person types, because the whole point of the
/// hosted choice is that there is no address to know: a machine that picks hosted enrolls here. The
/// self-hosted path is unchanged - it still discovers (or is told) the address of the gateway the account
/// runs itself.
///
/// <see cref="ResolveUrl"/> honours the <c>DEVTHROTTLE_HOSTED_GATEWAY_URL</c> environment variable so a test
/// or staging run can point the same code at a different hosted box. That is an explicit operator override,
/// not a fallback: when it is absent the shipped address is used, and when it is set and unusable the caller
/// gets a hard failure rather than a silent redirect to production.
/// </summary>
public static class HostedGateway
{
    /// <summary>The environment variable an operator sets to point the hosted choice at a different box.</summary>
    public const string UrlEnvironmentVariable = "DEVTHROTTLE_HOSTED_GATEWAY_URL";

    /// <summary>The shipped address of DevThrottle's hosted, multi-tenant Gateway.</summary>
    public const string DefaultUrl = "https://gateway.devthrottle.com";

    /// <summary>
    /// The hosted Gateway URL this machine should enroll against: the operator override when
    /// <c>DEVTHROTTLE_HOSTED_GATEWAY_URL</c> is set to an absolute http/https address, otherwise
    /// <see cref="DefaultUrl"/>. An override that is set but not a usable absolute URL throws, so a typo
    /// fails loudly instead of quietly enrolling the machine into production.
    /// </summary>
    public static string ResolveUrl()
    {
        var raw = Environment.GetEnvironmentVariable(UrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultUrl;

        var trimmed = raw.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{UrlEnvironmentVariable} is set to '{raw}', which is not an absolute http:// or https:// address. " +
                $"Set it to a full hosted Gateway address, or clear it to use {DefaultUrl}.");
        }

        return trimmed;
    }
}
