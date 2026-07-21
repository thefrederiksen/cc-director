using System;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Reads a Bearer token from the <c>Authorization</c> header - the ONE place that parse lives, so every hosted
/// entry point that carries its authorization in the header (the hosted device enrollment and the hosted
/// <c>/mobile/enroll</c> account-token branch) reads it identically. Returns null when the header is missing, is not
/// a <c>Bearer</c> header, or carries an empty token; the caller turns a null into its own 401. The token is
/// never logged (security rule DT-05).
/// </summary>
internal static class BearerToken
{
    public static string? Read(HttpContext ctx)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var header))
            return null;

        var raw = header.ToString();
        const string prefix = "Bearer ";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = raw.Substring(prefix.Length).Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }
}
