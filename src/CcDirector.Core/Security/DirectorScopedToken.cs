using System.Security.Cryptography;
using System.Text;

namespace CcDirector.Core.Security;

/// <summary>
/// The authority a caller of the Director's Control API holds. There are two real levels on a
/// single-user desktop, and saying so plainly is more honest than pretending there are four.
/// </summary>
public enum ControlApiScope
{
    /// <summary>Full authority. The machine secret itself, and the derived admin/cli tokens.</summary>
    Full,

    /// <summary>
    /// An agent running INSIDE a session, bound to that one session's id. It may read its own
    /// session and the safe discovery set; it may not spawn, shut down, prompt another session,
    /// change settings, or drive a browser.
    /// </summary>
    SessionChild,
}

/// <summary>
/// What a presented credential turned out to be. <see cref="Scope"/> is meaningless unless
/// <see cref="IsValid"/> is true.
/// </summary>
public readonly record struct ControlApiPrincipal(bool IsValid, ControlApiScope Scope, string ScopeName, Guid? SessionId)
{
    public static readonly ControlApiPrincipal Invalid = new(false, ControlApiScope.SessionChild, "", null);

    public static ControlApiPrincipal FullAuthority(string scopeName) => new(true, ControlApiScope.Full, scopeName, null);

    public static ControlApiPrincipal Child(Guid sessionId) => new(true, ControlApiScope.SessionChild, ScopeNames.SessionChild, sessionId);
}

/// <summary>The scope names that appear on the wire, inside a token.</summary>
public static class ScopeNames
{
    /// <summary>The local desktop / any local administrative caller. Full authority.</summary>
    public const string Admin = "admin";

    /// <summary>The cc-devthrottle / cc-director command line. Full authority, distinct token value.</summary>
    public const string Cli = "cli";

    /// <summary>An agent spawned inside a session, bound to that session's id. Least privilege.</summary>
    public const string SessionChild = "session-child";

    /// <summary>The name reported for the raw machine secret itself (never minted, only recognised).</summary>
    public const string Root = "root";
}

/// <summary>
/// Scoped credentials for the Director's Control API, derived from the ONE machine secret the
/// Director already keeps (<see cref="DirectorAuth.TokenFile"/>, or the shared fleet token when this
/// Director is attached to a Gateway).
///
/// A scoped token is an opaque string the server can verify from the root secret alone:
///
///   v1.&lt;scope&gt;.&lt;sessionId-or-empty&gt;.&lt;base64url HMAC-SHA256(root, "&lt;scope&gt;\n&lt;sessionId&gt;")&gt;
///
/// The server recomputes the signature and compares it in constant time; there is nothing to store,
/// look up, or revoke individually, which is why this needs no second secret store and no new
/// persistence. The scope and the bound session id are READ OFF the token, and are only trusted
/// because the signature over exactly those two fields verified.
///
/// The raw machine secret continues to authenticate as full authority - it IS the root - so every
/// client that presents it today keeps working. Scoped tokens exist so the wire does not have to
/// carry the raw secret, and so a credential handed to an agent child grants strictly less than the
/// one held by the desktop.
/// </summary>
public static class DirectorScopedToken
{
    private const string Version = "v1";

    /// <summary>
    /// Mint a scoped token from the root secret. <paramref name="sessionId"/> is required for
    /// <see cref="ScopeNames.SessionChild"/> and must be null for the full-authority scopes - the
    /// binding is part of the signed material, so a child token can never be re-pointed at another
    /// session without the root secret.
    /// </summary>
    public static string Mint(string rootSecret, string scope, Guid? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(rootSecret))
            throw new ArgumentException("The root secret is required to mint a scoped token.", nameof(rootSecret));
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("A scope name is required.", nameof(scope));
        if (scope.Contains('.'))
            throw new ArgumentException("A scope name must not contain '.' - it is the token separator.", nameof(scope));

        if (scope == ScopeNames.SessionChild && sessionId is null)
            throw new ArgumentException("A session-child token must be bound to a session id.", nameof(sessionId));
        if (scope != ScopeNames.SessionChild && sessionId is not null)
            throw new ArgumentException($"Scope '{scope}' is not session-bound; pass no session id.", nameof(sessionId));

        var boundId = sessionId?.ToString("D") ?? "";
        return $"{Version}.{scope}.{boundId}.{Sign(rootSecret, scope, boundId)}";
    }

    /// <summary>
    /// Decide what a presented credential is. Returns <see cref="ControlApiPrincipal.Invalid"/> for
    /// anything that does not verify - a wrong signature, an unknown scope, a malformed session id,
    /// or a value that is neither the root secret nor a well-formed scoped token.
    /// </summary>
    public static ControlApiPrincipal Verify(string? presented, string rootSecret)
    {
        if (string.IsNullOrWhiteSpace(presented) || string.IsNullOrWhiteSpace(rootSecret))
            return ControlApiPrincipal.Invalid;

        // The machine secret itself. Constant-time, because the old ordinal string.Equals here leaked
        // the length of the matching prefix to anything that could time a loopback request.
        if (FixedTimeEquals(presented, rootSecret))
            return ControlApiPrincipal.FullAuthority(ScopeNames.Root);

        var parts = presented.Split('.');
        if (parts.Length != 4 || parts[0] != Version)
            return ControlApiPrincipal.Invalid;

        var scope = parts[1];
        var boundId = parts[2];

        // Recompute over exactly the fields we are about to trust. Anything that has been edited -
        // the scope raised from session-child to admin, the session id swapped for another - changes
        // the signed material and fails here.
        if (!FixedTimeEquals(parts[3], Sign(rootSecret, scope, boundId)))
            return ControlApiPrincipal.Invalid;

        switch (scope)
        {
            case ScopeNames.Admin:
            case ScopeNames.Cli:
                return boundId.Length == 0
                    ? ControlApiPrincipal.FullAuthority(scope)
                    : ControlApiPrincipal.Invalid;

            case ScopeNames.SessionChild:
                return Guid.TryParse(boundId, out var sid)
                    ? ControlApiPrincipal.Child(sid)
                    : ControlApiPrincipal.Invalid;

            default:
                // An unknown scope is a DENY, never a default-to-something. A future scope this build
                // has never heard of must not be silently promoted to the authority it happens to
                // resemble.
                return ControlApiPrincipal.Invalid;
        }
    }

    private static string Sign(string rootSecret, string scope, string boundId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(rootSecret));
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{scope}\n{boundId}"));
        return Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// Compare two secrets without leaking where they diverge. Length differences are unavoidable
    /// (the strings are hashed to a fixed width first, so only the equality answer escapes).
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ha = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var hb = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }
}
