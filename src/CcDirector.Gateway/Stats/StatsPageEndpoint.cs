using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The DevThrottle Stats feed: the aggregated totals behind the Cockpit and mobile "Your Throttle"
/// pages - how much of the owner's development is spoken vs typed, from which surface, and what it cost.
/// It reads the Gateway's own aggregated totals with no cloud round-trip.
///
/// Two routes, both behind the normal Gateway auth (the owner's signed-in browser reaches them):
///   GET /stats       - RETIRED as a page (issue #587): the embedded standalone dashboard duplicated the
///                      Cockpit's Your Throttle outside the Cockpit shell, which read as a broken white
///                      page. It now redirects to the Cockpit /your-throttle route.
///   GET /stats/data  - the aggregated totals as JSON, which the Cockpit and mobile pages fetch through
///                      client-core.
///
/// Only counts and ratios are ever served - never the text of anything typed or said (mission decision 5).
/// The feed states plainly which input paths are counted and which are not-captured (no-fallback rule).
///
/// SERVES PER-TENANT ON HOSTED. The hosted deny (issue #1848) has been RETIRED: the aggregator behind this
/// feed is fully tenant-partitioned (MTR-08) - every map is keyed by (tenant, ...), and every read takes a
/// <see cref="TenantId"/> - so there IS a correct per-tenant answer to serve. The data route resolves the
/// CALLER'S tenant from its authenticated device key and serves that tenant's totals: its own turns, repos,
/// agents, models and token spend, and no one else's. On the single-tenant self host the caller is always
/// <see cref="TenantId.Local"/>, so the page is byte-identical to before.
///
/// A REQUEST WITH NO RESOLVABLE TENANT IS DENIED (403), NEVER SERVED THE LOCAL PARTITION. On hosted an
/// authenticated device key that is bound to no account carries no tenant, and serving it Local would be a
/// wrong-tenant read; it is refused instead. The gate is <c>ResolveReadTenant</c> returning null - the same
/// tenant-boundary seam every other served hosted route uses (issue #2017/#2022) - read directly, never a
/// fallback: the fix that makes this feed serve is the same one that makes it fail closed.
///
/// The store's file-share write-ahead-log hazard (#1861) is orthogonal and unchanged: the reads here are
/// in-memory per-tenant aggregates, so serving one tenant its own totals adds no new persistence surface.
///
/// Self-host is COMPLETELY unchanged, and that is the control: there the sole tenant is Local and the page
/// serves exactly as it always has.
/// </summary>
public static class StatsPageEndpoint
{
    /// <summary>
    /// Honesty caveats returned in the JSON: exactly which input paths are counted
    /// and which are not-captured, so a share the owner might publish is never quietly flattered.
    ///
    /// EVERY SENTENCE HERE NAMES ITS UNIT. The headline on this page is a ratio of TURNS, and the second
    /// sentence used to say only that terminal typing on the desktop app "is counted" - which was true of
    /// characters and false of turns, the only unit the reader sees. It read as a reassurance and it was
    /// covering the largest defect in the number: 594 of the owner's 771 typed submissions in the week of
    /// 2026-W35 were absent from the ring's denominator while that sentence said they were there. A
    /// caveat that does not say WHICH TALLY it is talking about cannot be checked by the person reading
    /// it, so it is worse than no caveat at all. Guarded by StatsPageDisclosureTests.
    /// </summary>
    private static readonly string[] NotCaptured =
    {
        "Your main phone voice (the Speak button / durable dictation) is counted as voice. If you pause a voice-mode reply and then send the already-typed transcript, that one is counted as typed.",
        "The message composer, and typing at the terminal in the desktop app, each count as one submitted turn when you press Enter, with the characters of the whole line. Raw keystrokes typed into a browser's live terminal stream are not attributed to a surface, so they are not counted at all.",
        "Surface (phone / cockpit) for remote input is read from the signed-in device. Remote input with no device identity (a shared-token or fleet call) is not counted as an operator surface.",
    };

    /// <summary>The 403 the data route answers when the caller's tenant cannot be resolved (issue #2017 seam).
    /// On the hosted Gateway an authenticated request whose device key has no bound tenant is refused, NEVER
    /// served the Local partition (that would be a wrong-tenant read). Self-host always resolves to Local, so
    /// this never fires there.</summary>
    private static IResult TenantRequired()
        => Results.Json(new { error = "a tenant could not be resolved for this request" },
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// The session-origin block of the feed (devthrottle_internal issue #982): how sessions came to
    /// exist, over the whole window the work-history table retains and over the last seven days.
    ///
    /// TWO WINDOWS, ON PURPOSE. The all-time counts are the ones a claim gets made from, and they are
    /// the ones that will be wrong first: the record only began the day the fields shipped, so an
    /// all-time share silently mixes "before we were asking" into the denominator forever. The
    /// seven-day window is the one that is honestly comparable to itself week over week, and it states
    /// its own bounds so a reader can see how much record there actually is.
    ///
    /// The all-time block reports where the RECORD begins (the oldest birth actually stored), not the
    /// floor of the query. Those differ by more than a detail: retention prunes from the front, and the
    /// origin fields only began being written the day they shipped, so quoting a share over "all time"
    /// without that date is quoting a denominator the Gateway has not got. The floor of the query is
    /// <see cref="DateTime.MinValue"/> deliberately - anything higher would silently drop a row whose
    /// start time is missing rather than showing it in the not-recorded bucket where it belongs.
    /// </summary>
    private static object OriginBlock(History.SessionHistoryStore sessionHistory)
    {
        var now = DateTime.UtcNow;
        var allTime = sessionHistory.OriginTotals(DateTime.MinValue, now);
        var week = sessionHistory.OriginTotals(now.AddDays(-7), now);
        return new
        {
            // What each bucket key means, served beside the numbers so a reader never has to guess
            // whether "notRecorded" is a kind of session or the absence of a record.
            notRecordedMeans = "the session's row predates the origin fields - the Gateway was not "
                             + "asking. Distinct from \"unknown\", which is a recorded answer: the "
                             + "create path was asked and had nothing to say.",
            allTime = new
            {
                // Where the RECORD begins, not where the query floor was set. Null when nothing is
                // stored at all - honestly empty rather than dated to the epoch.
                recordBeginsUtc = allTime.EarliestStartUtc,
                toUtc = allTime.ToUtc,
                sessions = allTime.Sessions,
                withParentSession = allTime.WithParent,
                byKind = allTime.ByKind,
                bySurface = allTime.BySurface,
            },
            last7Days = new
            {
                sinceUtc = week.FromUtc,
                toUtc = week.ToUtc,
                recordBeginsUtc = week.EarliestStartUtc,
                sessions = week.Sessions,
                withParentSession = week.WithParent,
                byKind = week.ByKind,
                bySurface = week.BySurface,
            },
        };
    }

    /// <summary>
    /// Maps the two stats routes. Returns the group builder they were mapped through (kept for callers that
    /// compose onto it). The hosted deny is gone: the data route serves the caller's own tenant's totals and
    /// answers 403 when no tenant resolves.
    /// </summary>
    /// <summary>Convenience for callers that already hold a settled aggregator - the self-host probe hosts
    /// and the unit tests. Production passes the handle, because on hosted the answer can change.</summary>
    public static RouteGroupBuilder Map(IEndpointRouteBuilder outer, GatewayInputStatsAggregator aggregator,
        Tenancy.HostedTenantBoundary tenantBoundary,
        ISessionConcurrencyRecorder? concurrency = null,
        Settings.TenantSettingsResolver? tenantSettings = null,
        History.SessionHistoryStore? sessionHistory = null) =>
        Map(outer, InputStatsHandle.Available(aggregator), tenantBoundary, () => concurrency, tenantSettings,
            sessionHistory);

    public static RouteGroupBuilder Map(IEndpointRouteBuilder outer, InputStatsHandle statistics,
        // The tenant boundary the data route resolves the CALLER's tenant through. REQUIRED AND NON-NULLABLE
        // (finding I1-01), and moved AHEAD of the optional tail so it cannot sit in a defaulted position: a
        // forgotten boundary must be a compile error, never a silent default. Self-host callers construct it
        // over the SingleTenantContext, which always resolves the single Local tenant; on hosted it resolves
        // the authenticated device key's tenant and the route answers 403 when there is none - never Local.
        Tenancy.HostedTenantBoundary tenantBoundary,
        // A FUNCTION, not an instance, for the same reason the handle replaced the aggregator above: on
        // hosted the recorder arrives when the statistics store publishes its factory, which may be after
        // startup. Capturing the instance here would freeze that decision a third time.
        Func<ISessionConcurrencyRecorder?> concurrency,
        // Issue #2017: the per-tenant settings resolver. The display time zone is read for the caller's tenant
        // (TimeZone(tenant)) instead of the process-global config. Null (older callers, tests) keeps the global
        // read, byte-identical to before.
        Settings.TenantSettingsResolver? tenantSettings = null,
        // The durable work-history store (devthrottle_internal issue #982), source of the session-origin
        // counts. Null (older callers, tests) omits that block from the feed entirely rather than serving
        // zeroes - a zero here would read as "no agent ever started a session", which is a different and
        // much more interesting claim than "this Gateway is not keeping the record".
        History.SessionHistoryStore? sessionHistory = null)
    {
        FileLog.Write($"[StatsPageEndpoint] mapping /stats (redirect to /your-throttle) and /stats/data; hosted={GatewayHostedMode.IsHosted} - the data route serves the caller's own tenant totals, 403 when unresolved (issue #1848 deny retired)");

        // The empty prefix keeps the route paths written out in full, exactly as before.
        var app = outer.MapGroup("");

        app.MapGet("/stats/data", (HttpContext ctx) =>
        {
            // ASKED PER REQUEST. On hosted this can be null now and non-null in a moment, when a statistics
            // store that opened past the startup deadline publishes its factory. The named 503 is what the
            // route answers meanwhile - never a vanished route, which reads as a broken deploy, and never a
            // 200 carrying somebody else's partition.
            var aggregator = statistics.Aggregator;
            if (aggregator is null)
                return Results.Json(
                    new { available = false, reason = statistics.UnavailableReason ?? "Statistics are unavailable." },
                    statusCode: 503);

            // Serve the CALLER's own tenant's totals (the aggregator is tenant-partitioned, MTR-08). On the
            // hosted Gateway the tenant comes from the authenticated device key; a request with no bound tenant
            // is refused (403), NEVER served the Local partition. On self-host the sole tenant is Local.
            var resolved = Api.GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (resolved is null) return TenantRequired();
            var tenant = resolved.Value;
            var totals = aggregator.CurrentTotals(tenant);
            return Results.Json(new
            {
                generatedAtUtc = DateTime.UtcNow,
                // The display time zone the hourly charts render local clock hours in (IANA id), read for the
                // (self-host) local tenant (issue #2017): the tenant override else the operator global default.
                // Auto-defaults to this Gateway machine's own zone; the owner can override it in Settings.
                timeZone = tenantSettings is not null
                    ? tenantSettings.TimeZone(tenant)
                    : CcDirector.Core.Configuration.TimeZoneConfig.Get(),
                buckets = totals.Buckets,
                // DevThrottle Stats: the "working day" series - turns (by modality) + characters per UTC hour.
                hourlyTurns = aggregator.HourlyTurns(tenant),
                // Wingman usage: turns submitted while a session had voice mode on, and the count of distinct
                // sessions ever in voice mode ("using the wingman" = voice mode on for that session).
                wingman = aggregator.WingmanUsage(tenant),
                // DevThrottle Stats: fleet concurrency (both series: live loaded/running, and actively
                // working). Null until the aggregator is wired (old callers / tests).
                concurrency = concurrency()?.Snapshot(DateTime.UtcNow, tenant),
                // DevThrottle Stats (private Repos page): the per-repository all-time tally, ranked
                // most-driven first, so the owner can see where development actually happens. Same
                // owner-only auth as the rest of this feed; rendered on a SEPARATE page from Your Throttle
                // so it never rides along when the throttle is shared.
                repos = aggregator.RepoTotals(tenant),
                // DevThrottle Stats (private Agents page): the per-agent all-time tally, ranked most-driven
                // first, so the owner can see which agent CLI the work actually goes through. Unlike the
                // other series this one starts at agentsSinceUtc - the breakdown was added after the totals
                // had been accumulating - so the page states that window rather than implying the earlier
                // turns ran under no agent.
                agents = aggregator.AgentTotals(tenant),
                agentsSinceUtc = aggregator.AgentsSinceUtc(tenant),
                // DevThrottle Stats (issue #1637): the per-model all-time tally - which model actually did
                // the work, ranked most-driven first. Like the agents series it starts at modelsSinceUtc
                // rather than at the beginning of the totals, so the page states that window instead of
                // implying the earlier turns ran under no model. A null model in this list is the honest
                // "the agent had not recorded one yet" bucket, not a missing value to hide.
                models = aggregator.ModelTotals(tenant),
                modelsSinceUtc = aggregator.ModelsSinceUtc,
                // DevThrottle Stats (issue #1637): TOKEN SPEND - what the work actually cost. Three views of
                // one number: the all-time total, the per-hour series for "what did I spend today / this
                // week / this month", and the per-model split for "which model cost what". Cumulative,
                // additive tokens only (input / output / cache) - never context-window occupancy, which is a
                // gauge and cannot be summed. Claude-only until other agents' drivers report cumulative spend.
                tokenSpend = aggregator.TokenSpend(tenant),
                tokenSpendByHour = aggregator.TokenSpendByHour(tenant),
                tokenSpendByModel = aggregator.TokenSpendByModel(tenant),
                // DevThrottle Stats (issue #1636): turns the fleet drove into ITSELF - one agent prompting
                // another. Reported alongside the human tally but never inside it: "how do you drive" and
                // "how much does the fleet drive itself" are different questions, and the ratio between
                // them is the leverage the owner actually gets per turn they spend.
                agentDrivenTurns = aggregator.AgentDrivenUsage(tenant).Turns,
                agentDrivenCharacters = aggregator.AgentDrivenUsage(tenant).Characters,
                // devthrottle_internal issue #982: how the fleet's sessions CAME TO EXIST, over the
                // durable work-history window. The agent-driven numbers above count turns - who does
                // the talking once a session is running. This counts births - who decides a session
                // should exist at all, which is the step that turns one person into a supervisor of
                // twenty-two. Null when no history store is wired; see the parameter note above for
                // why that is not zero.
                sessionOrigins = sessionHistory is null ? null : OriginBlock(sessionHistory),
                notCaptured = NotCaptured,
            });
        });

        // The standalone embedded dashboard is RETIRED (issue #587): it duplicated the Cockpit's
        // Your Throttle page outside the Cockpit shell. Anyone still opening /stats lands on the
        // real page. A temporary (302) redirect on purpose - browsers do not cache it, so the
        // destination can change without stranding old bookmarks.
        // The page rides the same per-request availability gate as the feed. Redirecting a caller to a
        // Cockpit page whose feed cannot serve is a dead end, so while there is no aggregator this answers
        // the named 503 in plain text - and it starts redirecting the moment there is one.
        app.MapGet("/stats", () => statistics.Aggregator is null
            ? Results.Text(statistics.UnavailableReason ?? "Statistics are unavailable.", "text/plain", statusCode: 503)
            : Results.Redirect("/your-throttle"));

        return app;
    }
}
