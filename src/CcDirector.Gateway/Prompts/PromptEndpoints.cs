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
/// POST /prompts - a Director pushes what it captured. This is the SERVICE-SIDE copy and the only one
/// DevThrottle holds, which is why the write is acknowledged with a real count rather than
/// fire-and-forget.
///
/// It is NOT the only copy in the world, and this comment used to say it was. A Director also keeps
/// prompt-derived text in local files on the member's own machine - a first-prompt snippet and per-turn
/// summaries in its own session-history JSON, and an expected-first-prompt in <c>sessions.json</c> and its
/// backup - and those survive restarts. Issue #2380 tracks bringing them within the delete. The
/// distinction matters and is not a technicality: a copy DevThrottle holds on its own servers and a file
/// on the member's own disk are different things to a member, and the wording on /privacy says so
/// separately rather than blurring them.
///
/// GET /prompts  - anyone asking for history asks here. That is the point of the log living on the
/// Gateway: it already has the whole fleet's record, so nothing has to go hunting across machines.
///
/// GET /prompts/export and DELETE /prompts - the account data rights (CR-3b, devthrottle_internal issue
/// #1180). Export returns the requesting account's ENTIRE prompt history as a downloadable JSON document;
/// delete removes every one of that account's daily files AND every copy the Gateway derived from them
/// (<see cref="History.SessionHistoryStore.ErasePromptDerived"/>). Both are tenant-scoped exactly like the
/// verbs above; neither can name another account's partition.
///
/// A SEALED SUMMARY IS ERASED WITH THE REST, and that reverses what this comment said for two rounds. The
/// exemption rested on the seal being the session's own farewell rather than prompt material - but the seal
/// route accepts whatever prose it is sent, with no material time and no provenance, so nothing establishes
/// that. Arriving through the seal route is an OPERATION, not a provenance. Keeping content that MAY be the
/// member's prompts is the worse error, and the exemption would have kept it through every later delete.
///
/// THE DELETE IS TWO STORES, AND THEY HAVE DIFFERENT TRUTHS. Say them separately or one of them is a lie:
///
///  - The prompt log is FILES. The Gateway makes no backup of them, so deleting them removes DevThrottle's
///    copy at once. Afterwards <see cref="GatewayPromptLog.Append"/> refuses records DATED at or before the
///    erasure - which is what a Director retrying an old batch usually sends - but a record DATED after it
///    is admitted, because nothing here can distinguish that from a prompt the member sent a second ago.
///    So the honest sentence is "material we can tell is older is refused", never "it cannot come back".
///    It also does not reach the Director's own local files on the member's machine (issues #2380, #2381).
///  - The derived copies are DATABASE ROWS. They are erased from the live database immediately, and they
///    carry the same seven-day platform backup tail that every database-stored class already discloses.
///
/// The version of this comment before the erasure existed said the delete WAS the erasure while the
/// derived copy sat in <c>session_history</c> for another ninety days, served on the History page. The
/// sentence was the more expensive half of that defect: an engineer reading it had no reason to look.
/// Whichever way this behaviour changes next, these two paragraphs change WITH it or they become the
/// same trap again.
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
            // A CONCURRENT INGEST IS NOT LOCKED OUT, and it no longer needs to be. The version of this
            // comment before the inspection argued the window was harmless because any racing material
            // must have been sent DURING the member's own delete. That was FALSE, and worth recording as
            // a lesson rather than quietly deleting: the Director's ingest deliberately RETRIES records
            // it previously failed to deliver, so a push landing here can carry prompts from weeks ago -
            // exactly the ones the member just erased. The reasoning was comfortable and wrong, and it
            // was reasoning about a race rather than closing one.
            //
            // What closes it is the erasure watermark (PromptErasureWatermarkEntity): the derived-content
            // writers refuse material older than the delete, so an ingest arriving mid-delete or long
            // afterwards cannot put erased words back. The prompt LOG can still accept a retried old
            // record - that is a decision about the Director-held copies, tracked in issue #2380.
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
