using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The morning-report surface (issue #2119, slice 2 of #2096):
///
///   GET /gateway/reports/morning?account=&amp;date=yyyy-MM-dd&amp;tz=IANA  ->  MorningReportDto
///
/// One honest JSON report per account per calendar day. The website's 7:00 cron calls this, renders the
/// approved email design from it, and sends it. Read-only: it writes nothing.
///
/// AUTHORIZATION IS ITS OWN, AND IT IS NOT A DEVICE KEY. The caller is a SERVER (the website cron), which
/// holds no Director/phone device credential, so this route is exempt from the host-wide token gate
/// (<c>AuthMiddleware.PublicPaths</c>) and carries its own: a bearer service token supplied out of band as
/// the <c>REPORT_SERVICE_TOKEN</c> environment variable. Exempt from that gate does NOT mean open - every
/// path below denies by default:
///   - the variable unset or blank    -> 503. The endpoint refuses to serve rather than serve unguarded.
///   - no / malformed / wrong bearer  -> 401.
///   - a valid token naming an account this Gateway does not know -> 404.
///   - a valid token naming an ambiguous account                  -> 409, never a guess.
/// The token comparison is fixed-time, so a wrong token cannot be discovered a byte at a time.
///
/// A VALID TOKEN IS NOT AUTHORITY OVER EVERY ACCOUNT'S DATA IN TURN - it is authority to ASK, and the answer
/// is scoped to exactly the one tenant the named account resolves to. The tenant is resolved here, once, and
/// handed to <see cref="MorningReportBuilder"/> explicitly; the builder reads through a context stamped with
/// that tenant, so the global query filter makes another tenant's rows unreachable rather than merely unasked-for.
/// </summary>
internal static class MorningReportEndpoint
{
    /// <summary>The route. Exact-match public in <c>AuthMiddleware</c>; this endpoint carries its own gate.</summary>
    public const string Path = "/gateway/reports/morning";

    /// <summary>The environment variable holding the bearer service token the website cron presents.</summary>
    public const string ServiceTokenEnvVar = "REPORT_SERVICE_TOKEN";

    public static void Map(
        IEndpointRouteBuilder app,
        MorningReportBuilder reports,
        TenantRegistry tenants,
        HostedTenantBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(boundary);

        app.MapGet(Path, (HttpContext ctx, string? account, string? date, string? tz) =>
            Handle(ctx, account, date, tz, reports, tenants, boundary));

        FileLog.Write($"[MorningReportEndpoint] mapped {Path} (service-token authorized, read-only)");
    }

    /// <summary>Internal (not private) so the authorization and resolution rules can be unit-tested directly
    /// through InternalsVisibleTo, without standing a Kestrel host up per case.</summary>
    internal static IResult Handle(
        HttpContext ctx,
        string? account,
        string? date,
        string? tz,
        MorningReportBuilder reports,
        TenantRegistry tenants,
        HostedTenantBoundary boundary)
    {
        if (ServiceTokenDenial(ctx) is { } tokenDenial) return tokenDenial;

        MorningReportWindow window;
        try
        {
            window = MorningReportWindow.Resolve(date, tz);
        }
        catch (MorningReportWindowException ex)
        {
            FileLog.Write($"[MorningReportEndpoint] rejected: {ex.Message}");
            return Results.BadRequest(new { error = ex.Message });
        }

        if (string.IsNullOrWhiteSpace(account))
            return Results.BadRequest(new { error = "an 'account' is required (the account id or the account email)" });

        if (!TryResolveTenant(account, tenants, boundary, out var tenant, out var denial))
            return denial;

        // Enter the resolved tenant's ambient scope for the duration of the build. The builder scopes its own
        // reads explicitly (belt), and this makes any tenant-scoped collaborator it consults resolve to the
        // same tenant rather than to nothing (braces). Inert on self-host.
        using (boundary.EnterScope(tenant))
        {
            var report = reports.Build(account.Trim(), tenant, window);
            FileLog.Write($"[MorningReportEndpoint] served a morning report: tenant={tenant.ToLogString()} " +
                          $"date={window.Date} tz={window.Tz} attention={report.Attention.Count}");
            return Results.Json(report);
        }
    }

    /// <summary>
    /// Resolve the named account to the tenant whose data the report will contain.
    ///
    /// SELF-HOST HAS EXACTLY ONE TENANT AND NO ACCOUNT CENSUS. There is no <c>tenants</c> mapping table to
    /// look an account up in, and every row on the install belongs to <see cref="TenantId.Local"/>, so the
    /// question "which account?" has one answer and it is not a guess. On HOSTED the census is authoritative
    /// and an unknown or ambiguous identifier is refused.
    /// </summary>
    private static bool TryResolveTenant(
        string account, TenantRegistry tenants, HostedTenantBoundary boundary,
        out TenantId tenant, out IResult denial)
    {
        if (!boundary.IsHosted)
        {
            tenant = TenantId.Local;
            denial = null!;
            FileLog.Write("[MorningReportEndpoint] self-host: the report is served for the single local tenant");
            return true;
        }

        var (outcome, resolved) = tenants.LookupByAccount(account);
        switch (outcome)
        {
            case TenantRegistry.AccountLookupOutcome.Found:
                tenant = resolved;
                denial = null!;
                return true;

            case TenantRegistry.AccountLookupOutcome.Ambiguous:
                tenant = default;
                denial = Results.Json(
                    new { error = "more than one account carries that identifier; name the account by its account id" },
                    statusCode: StatusCodes.Status409Conflict);
                return false;

            default:
                tenant = default;
                denial = Results.NotFound(new { error = "no such account" });
                return false;
        }
    }

    /// <summary>
    /// Whether the request presents the configured bearer token. Compared in FIXED TIME over the raw bytes:
    /// an ordinary string comparison returns as soon as two bytes differ, which leaks the correct prefix
    /// length to anyone who can time the response, and this token guards a person's whole account report.
    /// The length difference is itself unavoidable and is handled by comparing fixed-size digests, so even
    /// the length does not leak through the comparison.
    /// </summary>
    /// <summary>
    /// The report service-token gate, or null when the caller is authorized. Extracted so the recipient
    /// list endpoint enforces the SAME rule rather than growing a second, subtly different one - a list of
    /// every account's email address must never be guarded more weakly than a single account's report.
    /// </summary>
    internal static IResult? ServiceTokenDenial(HttpContext ctx)
    {
        var configured = Environment.GetEnvironmentVariable(ServiceTokenEnvVar);
        if (string.IsNullOrWhiteSpace(configured))
        {
            // Fail loud and CLOSED. An unconfigured token is a deployment error, not a reason to serve to
            // anyone who asks; and it is not a reason to invent an allow-anything mode either.
            FileLog.Write($"[MorningReportEndpoint] DENIED: {ServiceTokenEnvVar} is not set on this Gateway - the report endpoints refuse to serve unguarded");
            return Results.Json(
                new { error = $"the report service token ({ServiceTokenEnvVar}) is not configured on this Gateway" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!PresentedTokenMatches(ctx, configured))
        {
            FileLog.Write("[MorningReportEndpoint] DENIED: missing or incorrect service token");
            return Results.Json(new { error = "a valid report service token is required" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return null;
    }

    private static bool PresentedTokenMatches(HttpContext ctx, string configured)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
            return false;

        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var presented = header[scheme.Length..].Trim();
        if (presented.Length == 0)
            return false;

        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(configured.Trim()));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
