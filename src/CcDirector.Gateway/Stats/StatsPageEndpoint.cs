using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Throttle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The DevThrottle Stats feed: the figures behind the Cockpit and mobile "Your Throttle" pages - how much of
/// the owner's development is spoken vs typed, from which surface, into which repository and agent, and what
/// the fleet drove into itself.
///
/// Two routes, both behind the normal Gateway auth (the owner's signed-in browser reaches them):
///   GET /stats       - RETIRED as a page (issue #587): redirects to the Cockpit /your-throttle route.
///   GET /stats/data  - the figures as JSON, which the Cockpit and mobile pages fetch through client-core.
///                      Optional <c>from</c> and <c>to</c> (ISO 8601, UTC) name the window; absent, the
///                      window is the ledger's whole retention, and the answer says so either way.
///
/// EVERY COUNT OF TURNS ON THIS FEED COMES FROM THE SUBMISSION LEDGER, THROUGH THE ONE DEFINITION IN
/// <see cref="ThrottleDefinition"/> (mission "Clean up Your Throttle", ruling R9). The feed used to serve
/// the <c>stat_delta</c> tally the Directors push, which over the owner's measured week said he was 92 per
/// cent spoken when the ledger written at the same choke point said 57: a turn typed at the desktop terminal
/// was never counted, and the tally re-counted restated cumulatives. The ledger is append-only and idempotent,
/// and the mentor report reconciles against the same rows, so both consumers agree by construction. What
/// still comes from the statistics store is nothing that counts a turn: fleet concurrency, token spend and
/// the per-model spend split. Character volume is gone from the feed (ruling R16): the ledger carries none
/// and the tally's is inflated.
///
/// YOUR THROTTLE IS A HOSTED-GATEWAY FEATURE (owner's ruling R1). On a self-hosted Gateway the data route
/// answers 200 with <c>available=false</c> and one plain sentence saying so (ruling R6): the page renders
/// that sentence rather than vanishing (which reads as a broken build) or serving a number from a store the
/// report does not read (the defect the mission exists to remove). The Gateway decides; the client renders.
///
/// Only counts and ratios are ever served - never the text of anything typed or said (mission decision 5).
/// The feed states plainly which input paths are counted and which are not-captured (no-fallback rule).
///
/// SERVES PER-TENANT ON HOSTED. The data route resolves the CALLER'S tenant from its authenticated device key
/// and serves that tenant's figure: its own turns, repos and agents, and no one else's. A REQUEST WITH NO
/// RESOLVABLE TENANT IS DENIED (403), NEVER SERVED THE LOCAL PARTITION - the gate is <c>ResolveReadTenant</c>
/// returning null, the same tenant-boundary seam every other served hosted route uses (issue #2017/#2022).
/// </summary>
public static class StatsPageEndpoint
{
    /// <summary>
    /// Honesty caveats returned in the JSON: exactly which input paths are counted and which are
    /// not-captured, so a share the owner might publish is never quietly flattered.
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
        "Speaking counts as voice wherever you do it - the Speak button, the phone, and a voice-mode reply - whenever the words you send are the transcription and nothing else. Edit those words before sending, or send them alongside something you typed, and that turn is counted as typed.",
        "The message composer, and typing at the terminal in the desktop app, each count as one submitted turn when you press Enter. Raw keystrokes typed into a browser's live terminal stream are not attributed to a surface, so they are not counted at all.",
        "Surface (phone / cockpit) for remote input is read from the signed-in device. Remote input with no device identity (a shared-token or fleet call) is not counted as an operator surface.",
        "A submission the product could not place on a surface is not counted in any number here; how many there were is shown beside the share. Turns one session sent into another are the fleet driving itself, and they are shown beside your own turns, never inside them.",
    };

    /// <summary>The one sentence a self-hosted Gateway answers with (rulings R1 and R6). The clients render
    /// it verbatim and work nothing out for themselves.</summary>
    public const string SelfHostReason =
        "Your Throttle works only on the hosted DevThrottle Gateway. This Gateway is self-hosted, so there is " +
        "no figure to show here.";

    /// <summary>The 403 the data route answers when the caller's tenant cannot be resolved (issue #2017 seam).
    /// On the hosted Gateway an authenticated request whose device key has no bound tenant is refused, NEVER
    /// served the Local partition (that would be a wrong-tenant read).</summary>
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
    private static object OriginBlock(History.SessionHistoryStore sessionHistory, TenantId tenant,
        Tenancy.HostedTenantBoundary tenantBoundary)
    {
        var now = DateTime.UtcNow;
        // The history store reads through the AMBIENT tenant, so the route's resolved tenant is entered
        // explicitly for the two reads - the same seam the ledger routes use - rather than relying on
        // whatever scope the request happens to be in.
        using var scope = tenantBoundary.EnterScope(tenant);
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
    /// The window a request names, or the default. Both <c>from</c> and <c>to</c> or neither; ISO 8601 read
    /// as UTC; the end after the start; and never longer than the ledger keeps - a window the store cannot
    /// honestly answer is refused with the reason, not served with silent zeroes at the front.
    /// </summary>
    internal static (ThrottleWindowDto? Window, string? Error) ResolveWindow(string? from, string? to, DateTime nowUtc)
    {
        var retention = ThrottleDefinition.RetentionDays;
        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to))
        {
            var end = nowUtc;
            return (new ThrottleWindowDto
            {
                FromUtc = end.AddDays(-retention),
                ToUtc = end,
                IsDefault = true,
                Label = $"Last {retention} days",
            }, null);
        }
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return (null, "'from' and 'to' must be given together, both as ISO 8601 UTC instants");

        if (!TryParseUtc(from!, out var fromUtc)) return (null, "'from' is not an ISO 8601 instant");
        if (!TryParseUtc(to!, out var toUtc)) return (null, "'to' is not an ISO 8601 instant");
        if (toUtc <= fromUtc) return (null, "'to' must be later than 'from'");
        if (toUtc - fromUtc > TimeSpan.FromDays(retention))
            return (null, $"the window is longer than the {retention} days the submission ledger keeps");

        return (new ThrottleWindowDto
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            IsDefault = false,
            Label = $"{fromUtc:yyyy-MM-dd HH:mm} to {toUtc:yyyy-MM-dd HH:mm} UTC",
        }, null);
    }

    private static bool TryParseUtc(string text, out DateTime utc)
    {
        var ok = DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed);
        utc = ok ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : default;
        return ok;
    }

    /// <summary>Convenience for callers that already hold a settled aggregator - the self-host probe hosts
    /// and the unit tests. Production passes the handle, because on hosted the answer can change.</summary>
    public static RouteGroupBuilder Map(IEndpointRouteBuilder outer, GatewayInputStatsAggregator aggregator,
        Tenancy.HostedTenantBoundary tenantBoundary,
        ThrottleLedgerReader throttle,
        ISessionConcurrencyRecorder? concurrency = null,
        Settings.TenantSettingsResolver? tenantSettings = null,
        History.SessionHistoryStore? sessionHistory = null) =>
        Map(outer, InputStatsHandle.Available(aggregator), tenantBoundary, throttle, () => concurrency, tenantSettings,
            sessionHistory);

    /// <summary>
    /// Maps the two stats routes. Returns the group builder they were mapped through (kept for callers that
    /// compose onto it).
    /// </summary>
    public static RouteGroupBuilder Map(IEndpointRouteBuilder outer, InputStatsHandle statistics,
        // The tenant boundary the data route resolves the CALLER's tenant through. REQUIRED AND NON-NULLABLE
        // (finding I1-01), and moved AHEAD of the optional tail so it cannot sit in a defaulted position: a
        // forgotten boundary must be a compile error, never a silent default. Self-host callers construct it
        // over the SingleTenantContext, which always resolves the single Local tenant; on hosted it resolves
        // the authenticated device key's tenant and the route answers 403 when there is none - never Local.
        Tenancy.HostedTenantBoundary tenantBoundary,
        // The library (ruling R9): the one reader of the submission ledger every turn figure comes from.
        // REQUIRED, for the same reason the boundary is - a feed mapped without it would have nothing honest
        // to serve, and that must be a compile error rather than a route that serves the old tally.
        ThrottleLedgerReader throttle,
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
        ArgumentNullException.ThrowIfNull(tenantBoundary);
        ArgumentNullException.ThrowIfNull(throttle);
        FileLog.Write($"[StatsPageEndpoint] mapping /stats (redirect to /your-throttle) and /stats/data; hosted={GatewayHostedMode.IsHosted} - turn figures come from the submission ledger; a self-hosted Gateway answers available=false");
        // The empty prefix keeps the route paths written out in full, exactly as before.
        var app = outer.MapGroup("");

        app.MapGet("/stats/data", (HttpContext ctx, string? from, string? to) =>
        {
            // Ruling R1: hosted only. Ruling R6: say so in one sentence, on a 200 the page renders - the
            // absence of a figure is a fact about this Gateway, not an error in the request.
            if (!GatewayHostedMode.IsHosted)
                return Results.Json(new { available = false, reason = SelfHostReason });

            // Serve the CALLER's own tenant. On the hosted Gateway the tenant comes from the authenticated
            // device key; a request with no bound tenant is refused (403), NEVER served the Local partition.
            var resolved = Api.GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (resolved is null) return TenantRequired();
            var tenant = resolved.Value;

            var (window, windowError) = ResolveWindow(from, to, DateTime.UtcNow);
            if (window is null)
                return Results.BadRequest(new { error = windowError });

            var figure = throttle.Compute(tenant, window.FromUtc, window.ToUtc);
            figure.Window = window;

            // The statistics store is asked per request, because on hosted it can publish after startup.
            // Nothing that counts a turn depends on it any more, so its absence no longer takes the whole
            // page down: the blocks it feeds come back null with the store's own reason beside them.
            var aggregator = statistics.Aggregator;
            var statisticsReason = aggregator is null ? statistics.UnavailableReason ?? "Statistics are unavailable." : null;

            return Results.Json(new
            {
                available = true,
                generatedAtUtc = DateTime.UtcNow,
                // The display time zone the hourly charts render local clock hours in (IANA id), read for the
                // caller's tenant (issue #2017): the tenant override else the operator global default.
                timeZone = tenantSettings is not null
                    ? tenantSettings.TimeZone(tenant)
                    : CcDirector.Core.Configuration.TimeZoneConfig.Get(),
                // THE FIGURE. One definition, one substrate, the window stated, the excluded population
                // disclosed. Every count of turns the page shows is read from this one block - the buckets,
                // the hourly series, the per-repository and per-agent splits, and the turns the fleet drove
                // into itself (issue #1636: reported beside the human tally, never inside it). It is served
                // ONCE, so no page can end up with two copies of a number from two places.
                throttle = figure,
                // What the statistics store still feeds - none of it a count of turns. Null, with the
                // reason beside it, while the store has not published on hosted.
                statisticsUnavailableReason = statisticsReason,
                concurrency = aggregator is null ? null : concurrency()?.Snapshot(DateTime.UtcNow, tenant),
                models = aggregator?.ModelTotals(tenant),
                modelsSinceUtc = aggregator?.ModelsSinceUtc,
                tokenSpend = aggregator?.TokenSpend(tenant),
                tokenSpendByHour = aggregator?.TokenSpendByHour(tenant),
                tokenSpendByModel = aggregator?.TokenSpendByModel(tenant),
                // devthrottle_internal issue #982: how the fleet's sessions CAME TO EXIST, over the
                // durable work-history window. Null when no history store is wired; see the parameter note
                // above for why that is not zero.
                sessionOrigins = sessionHistory is null ? null : OriginBlock(sessionHistory, tenant, tenantBoundary),
                notCaptured = NotCaptured,
            });
        });

        // The standalone embedded dashboard is RETIRED (issue #587): it duplicated the Cockpit's
        // Your Throttle page outside the Cockpit shell. Anyone still opening /stats lands on the
        // real page. A temporary (302) redirect on purpose - browsers do not cache it, so the
        // destination can change without stranding old bookmarks. The page reads the feed, and the feed
        // answers on every Gateway - with the figure on hosted, with the one sentence on self-host - so
        // the redirect no longer has an availability gate in front of it.
        app.MapGet("/stats", () => Results.Redirect("/your-throttle"));
        return app;
    }
}
