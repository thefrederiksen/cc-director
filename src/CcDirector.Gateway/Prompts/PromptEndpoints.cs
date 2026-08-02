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
/// here, because the Gateway already has the whole fleet's record. GET /prompts/export and
/// DELETE /prompts - the account data rights (CR-3b, devthrottle_internal issue #1180). All are
/// tenant-scoped; none can name another account's partition.
///
/// ==================================================================================================
/// THE DELETE RULE. THIS IS THE ONLY PLACE IT IS STATED. Everywhere else - the store, the log, the
/// watermark entity, the tests, /privacy and the data map - says its own local fact and POINTS HERE for
/// the rule. Four inspection rounds found the same defect each time: a careful paragraph told the truth
/// while a summary line, a table cell or a test name stated an absolute the code falsified. That is what
/// happens when a rule is restated in eight places - the restatements drift, and every round found the
/// ones the last round had not named. One statement cannot disagree with itself.
///
/// FOUR CLAUSES. A sentence anywhere in this product about what the delete does must be one of these,
/// or must not be written:
///
///  1. ON THE SERVICE SIDE IT IS IMMEDIATE AND COMPLETE IN THE ORDINARY CASE. It deletes this account's
///     prompt-log files (the Gateway keeps no backup of them) and erases what the Gateway derived from
///     them: the seven prompt-derived columns on <c>session_history</c>, the three summary metadata
///     fields, and the cached daily roll-ups. Sealed summaries go with the rest - arriving through the
///     seal route is an operation, not a provenance, so nothing establishes that a farewell was not
///     composed from the member's own prompts.
///
///  2. IT IS NOT A DISTRIBUTED TRANSACTION. Work already in flight can land after it: a Director
///     mid-delivery, a summarisation already running writing its own bookkeeping, an interrupted roll-up
///     write leaving a paragraph that is never served and that the next delete removes. These are
///     bounded, they settle, and none of them is a standing second copy. Closing them properly means
///     cross-process locking and provider-specific atomicity, which is deliberately not in this work.
///
///  3. AFTERWARDS THE SERVICE REFUSES MATERIAL IT CAN TELL IS OLDER - records dated at or before the
///     erasure, which is what an ordinary retry sends. It CANNOT tell when the timestamp comes from a
///     caller whose clock is wrong: a record dated after the erasure is indistinguishable from a prompt
///     sent a second ago and is admitted. "Material we can tell is older is refused" is the whole
///     promise; "it cannot come back" is not.
///
///  4. IT DOES NOT REACH THE MEMBER'S OWN MACHINE AT ALL. The Director keeps prompt text locally in
///     several places, and the list is OPEN rather than exhaustive - five independent searches have each
///     found another one. Openness is not licence to omit a store already proved. Issues #2380 (bring
///     them within the delete) and #2381 (the operational logs have no retention) track the work.
///
/// If the behaviour changes, THIS BLOCK changes, and everything else keeps pointing here.
/// ==================================================================================================
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
            // See the DELETE RULE at the top of this file; this comment adds only what is local to the
            // ordering here. A CONCURRENT INGEST IS NOT LOCKED OUT. The version of this
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
