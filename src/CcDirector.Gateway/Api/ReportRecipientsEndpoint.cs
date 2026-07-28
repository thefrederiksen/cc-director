using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Who the daily report should go to.
///
///   GET /gateway/reports/recipients  ->  { recipients: [ { account, email } ] }
///
/// The report endpoint answers "what does THIS account's day look like" and has always required the
/// caller to already know the account. The sender therefore could only ever mail a hard-coded
/// allowlist. This is the missing half: the list itself, so the daily email reaches every account on
/// the Gateway without anyone maintaining a list of addresses by hand in a deployment variable.
///
/// SAME GATE AS THE REPORT. It presents the same service token, is public in AuthMiddleware for the
/// same reason, and fails the same way: no token configured is 503 and never an open door; a wrong
/// token is 401. It is deliberately NOT a second, weaker way in - a list of every account's email
/// address is more sensitive than any single report, not less.
///
/// AN ACCOUNT WITH NO EMAIL IS OMITTED, not returned blank. The tenants table records the email as it
/// was at mint time and it is nullable; a recipient with nothing to send to is not a recipient, and
/// returning an empty address would invite the sender to try anyway.
///
/// WHAT THIS DOES NOT DECIDE: whether a given account WANTS the email. There is no such setting yet,
/// and it is NOT coming from the first-run wizard - that step was removed (issue #996) because the
/// wizard runs once per director per machine while this preference is one per ACCOUNT, so it was
/// asked N times and the answers never reconciled. Until the account-scoped setting exists the report
/// is simply daily, and the email itself carries the instructions for changing or stopping it.
///
/// When the setting does exist, the filtering belongs HERE - one place that answers "who should be
/// mailed" - and not in the sender, so that every future channel inherits the same answer instead of
/// each one re-deriving it.
/// </summary>
internal static class ReportRecipientsEndpoint
{
    /// <summary>The route. Exact-match public in <c>AuthMiddleware</c>; this endpoint carries its own gate.</summary>
    public const string Path = "/gateway/reports/recipients";

    public static void Map(IEndpointRouteBuilder app, TenantRegistry tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);

        app.MapGet(Path, (HttpContext ctx) => Handle(ctx, tenants));

        FileLog.Write($"[ReportRecipientsEndpoint] mapped {Path} (service-token authorized, read-only)");
    }

    internal static IResult Handle(HttpContext ctx, TenantRegistry tenants)
    {
        // Reuses the report endpoint's gate rather than repeating it: one token, one rule, and no
        // chance of the two drifting so that the recipient list is guarded less well than the reports.
        if (MorningReportEndpoint.ServiceTokenDenial(ctx) is { } denial) return denial;

        var recipients = tenants.ListAll()
            .Where(t => !string.IsNullOrWhiteSpace(t.Email))
            .Select(t => new { account = t.Email!.Trim(), email = t.Email!.Trim() })
            .DistinctBy(r => r.email, StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.email, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Count only - the addresses themselves are personally identifying and this log is not the
        // place for them (the tenants table says the email is never logged).
        FileLog.Write($"[ReportRecipientsEndpoint] served {recipients.Count} recipient(s)");
        return Results.Json(new { recipients });
    }
}
