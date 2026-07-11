using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Centralization Phase 2 (issue #638): the read-only <c>GET /account/status</c> endpoint that
/// answers "is the Gateway signed in to DevThrottle, and as whom?". The signed-in boolean and the
/// email/provider identity are computed ENTIRELY LOCALLY from the Gateway-hosted credential service
/// (issue #636, the reused <see cref="DevThrottleAccountService"/> exposed as <c>GatewayHost.Account</c>)
/// - no cloud call on that path. A Director's startup gate (issue #651) reads this. Issue #1357 adds the
/// user's chosen <c>nickname</c>, which DOES require a cloud read (the nickname lives server-side); that
/// read is best-effort and cached per account (see <c>NicknameCacheTtl</c>), so this hard-polled endpoint
/// hits the cloud at most once per TTL and a nickname failure never breaks the status read.
///
/// Wire contract: the response body is
/// <c>{ "signedIn": bool, "email"?: string, "provider"?: string, "nickname"?: string }</c>. When the
/// Gateway holds a valid credential, <c>signedIn</c> is true and the <c>email</c>/<c>provider</c> identity
/// fields are present (the same two values <see cref="JwtIdentityReader"/> extracts, surfaced through
/// <see cref="DevThrottleAccountService.GetIdentity"/>); <c>nickname</c> is present only when the account
/// has one set and it resolved. When the Gateway holds no credential, <c>signedIn</c> is false and the
/// identity fields are OMITTED - never present, never fabricated.
///
/// Security (carries DT-05 from #636): the response NEVER includes the access or refresh token - only
/// the boolean and the identity. The tokens are never written to the log on any path either; the log
/// records only the outcome (signed in / not signed in).
///
/// When Gateway auth is enabled, this endpoint inherits the host-wide Gateway token middleware exactly
/// like the other Gateway endpoints (it is not on the public-paths allow-list), so a call with no token
/// is answered 401 by that middleware before this delegate runs.
/// </summary>
internal static class AccountStatusEndpoint
{
    /// <summary>
    /// Maps <c>GET /account/status</c>. The Gateway token convention (when Gateway auth is enabled) is
    /// applied by the host-wide auth middleware, exactly like the other Gateway endpoints.
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <param name="account">
    /// The Gateway-hosted DevThrottle credential service (issue #636). Null on a host that has no
    /// credential service (a non-Windows host, where the operating-system credential store is not yet
    /// implemented); the endpoint then truthfully reports not-signed-in.
    /// </param>
    /// <param name="nickname">
    /// The cloud nickname client (issue #1357), used to resolve the signed-in user's chosen nickname for
    /// the session preamble. Null disables nickname resolution (the response then omits it); the identity
    /// email/provider path is unchanged and stays entirely local. Resolution is cached per account so the
    /// hard-polled status read makes at most one cloud call per <see cref="NicknameCacheTtl"/>.
    /// </param>
    public static void Map(IEndpointRouteBuilder app, DevThrottleAccountService? account, AccountNicknameClient? nickname = null)
    {
        // Per-account nickname cache so this hard-polled endpoint hits the cloud at most once per TTL.
        // Captured in the closure (Map runs once per host) and guarded by its own lock.
        var cache = new NicknameCache();

        app.MapGet("/account/status", async (HttpContext ctx) =>
        {
            // No credential service on this host (a non-Windows host where the operating-system
            // credential store is not yet implemented): the Gateway holds no account credential, so the
            // truthful answer is not-signed-in with no identity.
            if (account is null)
            {
                FileLog.Write("[AccountStatusEndpoint] GET /account/status: no credential service on this host -> signedIn=false");
                return Results.Json(new AccountStatusResponse(false, null, null, null));
            }

            // Both reads are entirely local (no network call): IsLoggedIn validates the cached token's
            // signature and expiry locally, and GetIdentity decodes the cached token's claims locally.
            var signedIn = account.IsLoggedIn();
            if (!signedIn)
            {
                FileLog.Write("[AccountStatusEndpoint] GET /account/status: signedIn=false (no valid credential)");
                return Results.Json(new AccountStatusResponse(false, null, null, null));
            }

            var identity = account.GetIdentity();
            var resolvedNickname = await ResolveNicknameAsync(account, nickname, cache, ctx.RequestAborted).ConfigureAwait(false);
            // The identity is only logged as resolved / unavailable - the email itself is user identity,
            // not a token, but we keep the log minimal and never log any credential material.
            FileLog.Write($"[AccountStatusEndpoint] GET /account/status: signedIn=true (identity {(identity is null ? "unavailable" : "resolved")}, nickname {(resolvedNickname is null ? "unset" : "resolved")})");
            return Results.Json(new AccountStatusResponse(true, identity?.Email, identity?.Provider, resolvedNickname));
        });
    }

    /// <summary>How long a resolved nickname is reused before the next status read re-reads the cloud.</summary>
    private static readonly TimeSpan NicknameCacheTtl = TimeSpan.FromMinutes(15);

    /// <summary>Per-account nickname cache, one instance per mapped endpoint (captured in the closure).</summary>
    private sealed class NicknameCache
    {
        public readonly object Gate = new();
        public string? Subject;
        public string? Nickname;
        public DateTime AtUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Resolves the signed-in account's nickname, serving a fresh per-account cached value without a
    /// cloud call and reading the cloud (once) only on a cold or stale cache or when the account changed.
    /// Returns null when nickname resolution is disabled (no client), when there is no forwarding token,
    /// when the account has no nickname set, or when the cloud call fails - in every one of those cases
    /// the caller falls back to the email, so a nickname problem never breaks the status read.
    /// </summary>
    private static async Task<string?> ResolveNicknameAsync(
        DevThrottleAccountService account,
        AccountNicknameClient? nickname,
        NicknameCache cache,
        CancellationToken ct)
    {
        if (nickname is null)
            return null;

        var subject = account.GetAccountSubject();
        if (string.IsNullOrEmpty(subject))
            return null;

        lock (cache.Gate)
        {
            if (cache.Subject == subject && DateTime.UtcNow - cache.AtUtc < NicknameCacheTtl)
                return cache.Nickname;
        }

        var token = account.GetAccessTokenForForwarding();
        if (string.IsNullOrEmpty(token))
            return null;

        string? resolved;
        try
        {
            resolved = await nickname.GetNicknameAsync(token, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A nickname read is best-effort context, never a gate: a cloud failure logs and yields null
            // (the preamble falls back to the email). We do NOT cache a failure, so the next read retries.
            FileLog.Write($"[AccountStatusEndpoint] nickname resolve failed (falling back to email): {ex.Message}");
            return null;
        }

        lock (cache.Gate)
        {
            cache.Subject = subject;
            cache.Nickname = resolved;
            cache.AtUtc = DateTime.UtcNow;
        }
        return resolved;
    }

    /// <summary>
    /// The <c>GET /account/status</c> response. <see cref="SignedIn"/> is always present;
    /// <see cref="Email"/> and <see cref="Provider"/> are present only when signed in with a resolvable
    /// identity, and are OMITTED from the JSON (not emitted as null) otherwise - so the not-signed-in
    /// response carries no identity fields. This type intentionally carries NO token field, so the
    /// response can never include the access or refresh token (security rule DT-05).
    /// </summary>
    /// <param name="SignedIn">Whether the Gateway holds a valid DevThrottle credential.</param>
    /// <param name="Email">The signed-in user's email, or null (omitted) when not signed in / unavailable.</param>
    /// <param name="Provider">The authentication provider, or null (omitted) when not signed in / unavailable.</param>
    /// <param name="Nickname">The chosen account nickname (issue #1357), or null (omitted) when not signed in / unset / unresolved.</param>
    private sealed record AccountStatusResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("signedIn")]
        bool SignedIn,
        [property: System.Text.Json.Serialization.JsonPropertyName("email")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Email,
        [property: System.Text.Json.Serialization.JsonPropertyName("provider")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Provider,
        [property: System.Text.Json.Serialization.JsonPropertyName("nickname")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Nickname);
}
