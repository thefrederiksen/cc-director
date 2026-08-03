using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.History;

/// <summary>
/// The work-history front door (issue #2194). The consumable spine of the feature - the Cockpit
/// History page renders these, and the daily report email and the brain are meant to read the SAME
/// endpoints rather than a private backing.
///
/// GET  /history/report          - the range grouped by repository and day, with cached roll-up
///                                 paragraphs. Reads only; roll-ups are computed in the background
///                                 sweep, never on a page load (a deliberate cost rule).
/// GET  /history/sessions        - the flat session records over a range.
/// GET  /history/sessions/{id}   - one session's record.
/// POST /history/sessions/{id}/summary - a session SEALS its own record at clean shutdown.
///
/// TENANT-SCOPED exactly like <see cref="Prompts.PromptEndpoints"/>: every verb resolves the
/// request's tenant from the authenticated device key; on hosted a key with no bound tenant is
/// DENIED (403), never served the Local partition.
///
/// Range parameters are inclusive UTC day strings (yyyy-MM-dd), the /prompts convention. Defaults:
/// the last 7 days. The range is capped at 31 days - the named consumer is "the last 30 days".
/// </summary>
public static class HistoryEndpoints
{
    public const int MaxRangeDays = 31;

    public static void Map(IEndpointRouteBuilder app, SessionHistoryStore store,
        // REQUIRED, not defaulted (tenant-boundary hardening, release 2026-07-31, finding CR-7): the boundary
        // is a security argument, and when it was optional a forgotten argument silently served the Local
        // partition on hosted. A self-host-only caller must state the absence with an explicit null.
        Tenancy.HostedTenantBoundary? tenantBoundary)
    {
        ArgumentNullException.ThrowIfNull(store);

        app.MapGet("/history/report", (HttpContext ctx, string? from, string? to) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            var range = ResolveRange(from, to);
            if (range is null)
                return Results.BadRequest(new { error = $"'to' is earlier than 'from', or the range is over {MaxRangeDays} days" });
            var (fromDay, toDay) = range.Value;

            using (EnterScope(tenant.Value, tenantBoundary))
            {
                var report = BuildReport(store, fromDay, toDay);
                return Results.Ok(report);
            }
        });

        app.MapGet("/history/sessions", (HttpContext ctx, string? from, string? to, int? limit) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            var range = ResolveRange(from, to);
            if (range is null)
                return Results.BadRequest(new { error = $"'to' is earlier than 'from', or the range is over {MaxRangeDays} days" });
            var (fromDay, toDay) = range.Value;

            using (EnterScope(tenant.Value, tenantBoundary))
            {
                var sessions = store.ReadRange(fromDay, EndOfDay(toDay), limit ?? 1000);
                return Results.Ok(new { count = sessions.Count, sessions });
            }
        });

        app.MapGet("/history/sessions/{sessionId}", (HttpContext ctx, string sessionId) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            using (EnterScope(tenant.Value, tenantBoundary))
            {
                var session = store.Get(sessionId);
                return session is null
                    ? Results.NotFound(new { error = "no history record for that session" })
                    : Results.Ok(session);
            }
        });

        app.MapPost("/history/sessions/{sessionId}/summary", (HttpContext ctx, string sessionId,
            SealSessionSummaryRequest? request) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            if (request is null || string.IsNullOrWhiteSpace(request.Summary))
                return Results.BadRequest(new { error = "summary prose is required" });

            using (EnterScope(tenant.Value, tenantBoundary))
            {
                // This endpoint passes NO material time, deliberately. The only value it could supply is
                // the moment the request arrived, which is a fact about the request rather than about the
                // prose it carries. What the store compares instead is documented at SealSummary.
                var sealedOk = store.SealSummary(sessionId, request);
                if (!sealedOk)
                    return Results.NotFound(new { error = "no history record for that session, or this account erased since" });
                FileLog.Write($"[HistoryEndpoints] summary sealed: tenant={tenant.Value.ToLogString()}, session={sessionId}");
                return Results.Ok(new { sealedRecord = true });
            }
        });
    }

    /// <summary>
    /// Fold the stored rows and cached roll-ups into the grouped report. Pure read + in-memory fold:
    /// no model call ever happens here. A day whose roll-up has not been written yet says so
    /// (<c>summaryPending</c>) instead of blocking or inventing a paragraph.
    /// </summary>
    public static WorkHistoryReportDto BuildReport(SessionHistoryStore store, DateTime fromDay, DateTime toDay)
    {
        var sessions = store.ReadRange(fromDay, EndOfDay(toDay), SessionHistoryStore.MaxListLimit);
        var groups = SessionHistorySummarizer.RollupGroups(sessions, fromDay, toDay);
        var rollups = store.ReadRollups(fromDay, toDay)
            .ToDictionary(r => (r.RepoKey, r.DayUtc.Date), r => r);

        var repos = groups
            .GroupBy(g => g.RepoKey, StringComparer.Ordinal)
            .Select(byRepo => new WorkHistoryRepoDto
            {
                RepoKey = byRepo.Key,
                DisplayName = byRepo.Key,
                Days = byRepo
                    .OrderByDescending(g => g.Day)
                    .Select(g =>
                    {
                        rollups.TryGetValue((g.RepoKey, g.Day), out var cached);
                        var upToDate = cached is not null && cached.SummaryText is not null
                                       && string.Equals(cached.InputHash, g.InputHash, StringComparison.Ordinal);
                        return new WorkHistoryDayDto
                        {
                            Day = g.Day.ToString("yyyy-MM-dd"),
                            // A stale paragraph (inputs changed since it was written) still shows -
                            // it was true of the sessions it covered - but is flagged pending so the
                            // reader knows the background pass will refresh it.
                            SummaryText = cached?.SummaryText,
                            SummaryPending = !upToDate,
                            Sessions = g.Sessions.OrderByDescending(s => s.StartedAtUtc).ToList(),
                        };
                    })
                    .ToList(),
            })
            // Most recently active repository first - the one the owner is most likely asking about.
            .OrderByDescending(r => r.Days.Max(d => d.Sessions.Max(s => s.LastSeenUtc)))
            .ToList();

        return new WorkHistoryReportDto
        {
            FromDay = fromDay.ToString("yyyy-MM-dd"),
            ToDay = toDay.ToString("yyyy-MM-dd"),
            Repos = repos,
        };
    }

    private static (DateTime FromDay, DateTime ToDay)? ResolveRange(string? from, string? to)
    {
        var toDay = ParseDay(to) ?? DateTime.UtcNow.Date;
        var fromDay = ParseDay(from) ?? toDay.AddDays(-6);
        if (toDay < fromDay) return null;
        if ((toDay - fromDay).TotalDays >= MaxRangeDays) return null;
        return (fromDay, toDay);
    }

    private static DateTime EndOfDay(DateTime day) => day.Date.AddDays(1).AddTicks(-1);

    /// <summary>
    /// Null means DENY. Gated on <see cref="GatewayHostedMode.IsHosted"/> ITSELF, never on whether a boundary
    /// was passed in (finding CR-7): deciding on the argument fails open, so on hosted a missing or
    /// non-hosted-wired boundary resolves null - a refusal, never Local. Self-host is Local as before.
    /// </summary>
    private static TenantId? ResolveTenant(HttpContext ctx, Tenancy.HostedTenantBoundary? boundary)
    {
        if (!GatewayHostedMode.IsHosted)
            return boundary is null ? TenantId.Local : boundary.ResolveRequestTenant(ctx);
        if (boundary is null || !boundary.IsHosted)
            return null;
        return boundary.ResolveRequestTenant(ctx);
    }

    private static IDisposable EnterScope(TenantId tenant, Tenancy.HostedTenantBoundary? boundary)
        => boundary is null ? NoScope.Instance : boundary.EnterScope(tenant);

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }

    private static DateTime? ParseDay(string? value)
        => DateTime.TryParse(value, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.Date
            : null;
}
