namespace CcDirector.Core.Network;

/// <summary>
/// Builds and validates a gateway address a person types in the installer connect step or the
/// app's gateway settings (issue #1233).
///
/// The primary manual form is a computer name plus a port - the simplest, most reliable thing to
/// type on a local network - which becomes <c>http://&lt;name&gt;:&lt;port&gt;</c> and works whenever the
/// gateway machine is not blocking that port with a firewall. A full address the user pastes (for
/// example a Tailscale <c>https://…</c> URL) is also accepted, normalized, and validated.
///
/// Pure and side-effect free so both entry paths are unit-tested directly, and so the installer and
/// the settings dialog build the exact same URL from the same input.
/// </summary>
public static class GatewayAddress
{
    /// <summary>Lowest valid TCP port.</summary>
    public const int MinPort = 1;

    /// <summary>Highest valid TCP port.</summary>
    public const int MaxPort = 65535;

    /// <summary>
    /// Build <c>http://&lt;computerName&gt;:&lt;port&gt;</c> from a machine name and a port. Returns true and
    /// the url when the name is a bare host (non-blank, no scheme, slash, or space) and the port is
    /// in range; otherwise false and a human-readable <paramref name="error"/> the UI can show. The
    /// name is trimmed. For anything that is already a full address, use <see cref="TryNormalize"/>.
    /// </summary>
    public static bool TryFromComputerNameAndPort(string? computerName, int port, out string url, out string? error)
    {
        url = string.Empty;
        var name = (computerName ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            error = "Enter the gateway computer name.";
            return false;
        }
        if (name.Contains("://", StringComparison.Ordinal) || name.Contains('/'))
        {
            error = "Enter just the computer name here, not a full address.";
            return false;
        }
        if (name.Contains(' '))
        {
            error = "A computer name cannot contain spaces.";
            return false;
        }
        if (port < MinPort || port > MaxPort)
        {
            error = $"Port must be a number between {MinPort} and {MaxPort}.";
            return false;
        }

        url = $"http://{name}:{port}";
        error = null;
        return true;
    }

    /// <summary>
    /// Normalize a full address a person pasted. When the input carries no scheme, <c>http://</c> is
    /// assumed (the common local-network case); the result must then parse as an absolute http or
    /// https URL. Returns true and the normalized url (trailing slash trimmed), or false and a
    /// reason. Used for the "paste a full address" path (for example a Tailscale https URL).
    /// </summary>
    public static bool TryNormalize(string? input, out string url, out string? error)
    {
        url = string.Empty;
        var raw = (input ?? string.Empty).Trim();

        if (raw.Length == 0)
        {
            error = "Enter a gateway address.";
            return false;
        }
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "http://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "That is not a valid http or https address.";
            return false;
        }

        url = raw.TrimEnd('/');
        error = null;
        return true;
    }
}
