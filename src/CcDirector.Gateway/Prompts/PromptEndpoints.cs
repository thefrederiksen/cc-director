using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Prompts;

/// <summary>
/// The Gateway's prompt-log front door (issue #1551).
///
/// POST /prompts - a Director pushes what it captured. GET /prompts - anyone asking for history asks
/// here. GET /prompts/export and DELETE /prompts - the account data rights (CR-3b,
/// devthrottle_internal issue #1180). All are tenant-scoped; none can name another account's partition.
///
/// TENANT-SCOPED (issue #1848). "The whole fleet's record" means the REQUESTING ACCOUNT'S fleet. Both verbs
/// resolve the request's tenant from the authenticated device key with the same seam the cockpit read path
/// uses, and write into / read out of only that tenant's partition. Before this, neither handler took an
/// <c>HttpContext</c> at all - so neither could resolve a tenant even in principle, and a hosted GET returned
/// every account's full prompt TEXT. On hosted a request whose key has no bound tenant is DENIED (403); it is
/// never served the Local partition. Self-host (no boundary) is always Local, exactly as before.
/// </summary>
public static class PromptEndpoints
{
    public static void Map(IEndpointRouteBuilder app, GatewayPromptLog log,
        // REQUIRED, not defaulted (finding CR-7): a forgotten boundary must be a compile error, never Local.
        Tenancy.HostedTenantBoundary? tenantBoundary,
        // REQUIRED for the same reason, and it is the same failure shape: a forgotten store would leave
        // DELETE /prompts erasing the files, reporting success, and quietly keeping the derived copy -
        // which is precisely the defect this parameter exists to close. A caller with no database (the
        // self-host-only test harnesses) states the absence rather than inheriting it from a default.
        History.SessionHistoryStore? historyStore,
        History.SessionHistoryRecorder? history = null)
    {
        var store = log ?? throw new ArgumentNullException(nameof(log));

        app.MapPost("/prompts", (HttpContext ctx, PromptIngestRequest? request) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            if (request?.Records is null || request.Records.Count == 0)
                return Results.BadRequest(new { error = "records is required and must not be empty" });

            var written = store.Append(tenant.Value, request.Records);
            // Issue #2194: each session's FIRST user prompt is a work-history description source
            // (#1862 priority two). Fed inside the request tenant's ambient scope because the
            // recorder writes the tenant-scoped history table; memoized, so this is one store call
            // per session ever, and the recorder never throws into the ingest path.
            if (history is not null)
            {
                using (EnterScope(tenant.Value, tenantBoundary))
                    history.ObservePrompts(tenant.Value, request.Records);
            }
            FileLog.Write($"[PromptEndpoints] POST /prompts: tenant={tenant.Value.ToLogString()}, received {request.Records.Count}, wrote {written}");
            return Results.Ok(new PromptIngestResponse { Written = written });
        });

        app.MapGet("/prompts", (HttpContext ctx, string? from, string? to) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            // Default to today so a bare GET /prompts is useful rather than an error.
            var fromUtc = ParseDay(from) ?? DateTime.UtcNow.Date;
            var toUtc = ParseDay(to) ?? DateTime.UtcNow.Date;
            if (toUtc < fromUtc)
                return Results.BadRequest(new { error = "'to' is earlier than 'from'" });

            var records = store.Read(tenant.Value, fromUtc, toUtc);
            return Results.Ok(new { count = records.Count, records });
        });

        app.MapGet("/prompts/export", (HttpContext ctx) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            var records = store.ReadAll(tenant.Value);
            var payload = new { exportedAtUtc = DateTime.UtcNow, count = records.Count, records };
            // Web defaults so the export's field names match what GET /prompts serves; indented because
            // this file is FOR the member to read and keep, not for a machine round-trip.
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true });
            FileLog.Write($"[PromptEndpoints] GET /prompts/export: tenant={tenant.Value.ToLogString()}, exported {records.Count} records");
            return Results.File(bytes, "application/json",
                $"prompt-history-{DateTime.UtcNow:yyyyMMdd}.json");
        });

        app.MapDelete("/prompts", (HttpContext ctx) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            // Both halves are loud on failure by design: an erasure that half-happened must surface as an
            // error to the caller (the pipeline's 500), never as a success with content left behind.
            //
            // ORDER MATTERS, and it is derived-copy first. The prompt log is the material the derived copy
            // is made FROM: erase the copy while the log still stands and the worst case is that the
            // background sweep re-derives from material the member has not yet asked to be rid of. Delete
            // the log first and the same failure leaves the copy orphaned - the exact state this work
            // exists to remove, and now with no source left to prove what it was.
            //
            // Order: derived copies first, then the files. If the pair fails half way, the recoverable
            // state is a copy whose source still exists rather than an orphan with nothing left to prove
            // what it was.
            var erased = historyStore is null
                ? new History.PromptDerivedErasure(0, 0)
                : EraseDerived(historyStore, tenant.Value, tenantBoundary);
            var deletedFiles = store.DeleteAll(tenant.Value);
            FileLog.Write($"[PromptEndpoints] DELETE /prompts: tenant={tenant.Value.ToLogString()}, deleted {deletedFiles} daily files, "
                + $"cleared {erased.SessionRows} history row(s), deleted {erased.RollupRows} rollup row(s)");
            return Results.Ok(new
            {
                deletedFiles,
                erasedHistoryRows = erased.SessionRows,
                deletedHistoryRollups = erased.RollupRows,
            });
        });
    }

    /// <summary>
    /// Resolve the request's tenant from the AUTHENTICATED device key the auth middleware stashed - the same
    /// seam the tenant-aware cockpit read path uses. Null means DENY: on hosted an authenticated request whose
    /// key has no bound tenant is refused, never served the Local partition. Self-host, or no boundary (older
    /// callers and tests), is always Local.
    /// </summary>
    private static TenantId? ResolveTenant(HttpContext ctx, Tenancy.HostedTenantBoundary? boundary)
    {
        // Finding CR-7: gated on GatewayHostedMode.IsHosted itself, never on whether a boundary was passed
        // in - deciding on the argument fails open. On hosted a missing or non-hosted-wired boundary
        // resolves null, a refusal. Self-host is Local exactly as before.
        if (!GatewayHostedMode.IsHosted)
            return boundary is null ? TenantId.Local : boundary.ResolveRequestTenant(ctx);
        if (boundary is null || !boundary.IsHosted)
            return null;
        return boundary.ResolveRequestTenant(ctx);
    }

    /// <summary>
    /// Erase the derived copies inside the request tenant's ambient scope. Written out rather than
    /// inlined because the scope is the whole safety property: the store's statements are filtered by the
    /// AMBIENT tenant, so an erasure run outside the scope would reach whatever tenant happened to be
    /// current - the failure would be silent, and it would be someone else's data.
    /// </summary>
    private static History.PromptDerivedErasure EraseDerived(History.SessionHistoryStore store,
        TenantId tenant, Tenancy.HostedTenantBoundary? boundary)
    {
        using (EnterScope(tenant, boundary))
            return store.ErasePromptDerived();
    }

    /// <summary>Enter the resolved tenant's ambient scope for a database-writing side effect (the
    /// history recorder); the file-backed prompt log itself takes the tenant explicitly. No boundary
    /// (tests, self-host) means the ambient tenant is already Local.</summary>
    private static IDisposable EnterScope(TenantId tenant, Tenancy.HostedTenantBoundary? boundary)
        => boundary is null ? NoScope.Instance : boundary.EnterScope(tenant);

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }

    /// <summary>Parse a yyyy-MM-dd day, or null when absent/unparseable.</summary>
    private static DateTime? ParseDay(string? value)
        => DateTime.TryParse(value, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.Date
            : null;
}
