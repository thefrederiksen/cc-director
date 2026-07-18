using CcDirector.Core.Utilities;
using CcDirector.Gateway.Governance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The governance report surface (issue #1771, spine item 4) - the weekly Outcome Ledger. Read-only: it
/// assembles the run tables, event ledger, spend, and audit trail into one report; it writes nothing.
///
///   GET /gateway/governance/outcome-ledger  ?since=&amp;until=  -> OutcomeLedgerReportDto
///
/// The window defaults to the trailing seven days when omitted. Inherits the host-wide token middleware.
/// </summary>
internal static class GovernanceReportEndpoints
{
    public static void Map(IEndpointRouteBuilder app, OutcomeLedgerReporter reporter)
    {
        app.MapGet("/gateway/governance/outcome-ledger", (DateTime? since, DateTime? until) =>
        {
            var untilUtc = until ?? DateTime.UtcNow;
            var sinceUtc = since ?? untilUtc.AddDays(-7);
            try
            {
                return Results.Json(reporter.Build(sinceUtc, untilUtc));
            }
            catch (GovernanceValidationException ex)
            {
                FileLog.Write($"[GovernanceReportEndpoints] rejected: {ex.Message}");
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        FileLog.Write("[GovernanceReportEndpoints] mapped /gateway/governance/outcome-ledger route");
    }
}
