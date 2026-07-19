using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The DevThrottle Stats private dashboard: the page the owner opens on his OWN Gateway to
/// see, from real usage, how much of his development is spoken vs typed and how much comes from the phone,
/// the desktop, or the cockpit. Served as a SELF-CONTAINED page embedded in this binary (not a wwwroot
/// React route), so it works even on a plain dev build where the React apps are not built (mission
/// New build B, core finding 8). It reads the Gateway's own aggregated totals with no cloud round-trip.
///
/// Two routes, both behind the normal Gateway auth (the owner's signed-in browser reaches them) - and both
/// REFUSED on hosted, so "always available" describes self-host only:
///   GET /stats       - the HTML dashboard (this embedded page).
///   GET /stats/data  - the aggregated totals as JSON, which the page fetches and refreshes.
///
/// Only counts and ratios are ever served - never the text of anything typed or said (mission decision 5).
/// The page states plainly which input paths are counted and which are not-captured (no-fallback rule).
///
/// DENIED ON HOSTED (issue #1848). Both routes are refused on the hosted Gateway. This is the OWNER'S
/// private view of HIS OWN gateway, and on shared hosted infrastructure "the owner" does not survive as a
/// concept - so there is no correct per-tenant answer to serve here, only a disclosure to close. What the
/// feed actually carries makes that concrete: every repository name the fleet has driven, the per-agent and
/// per-model tallies, and the token SPEND - all fleet-global, and reachable by any tenant's device key
/// through the host-wide gate.
///
/// It is a DENY rather than a partition because there is nothing to partition BY. These are pre-aggregated
/// fleet totals with no tenant anywhere in the schema, so a per-tenant answer would have to be recomputed
/// from an attribution that was never recorded - and inventing one is a half-partition, which is worse than
/// an honest refusal. The store behind it is also the SQLite-plus-write-ahead-log-on-a-file-share hazard
/// booked as #1861, so a per-tenant partition would either multiply that hazard per tenant or force a
/// schema change for a dashboard nobody has asked to be multi-tenant. Per-tenant stats are booked as a
/// deferred product decision, blocked on that.
///
/// The refusal is a REFUSAL, not an empty dashboard. Serving zeroed or empty series would be a false
/// statement rather than an absent one - the same mistake /healthz made when it zeroed its fleet counts and
/// anything monitoring them read a permanently dead fleet. A caller is told the route is not available
/// here; it is never shown a dashboard implying no work has been done.
///
/// Self-host is COMPLETELY unchanged, and that is the control.
/// </summary>
public static class StatsPageEndpoint
{
    /// <summary>
    /// Honesty caveats shown on the page and returned in the JSON: exactly which input paths are counted
    /// and which are not-captured, so a share the owner might publish is never quietly flattered.
    /// </summary>
    private static readonly string[] NotCaptured =
    {
        "Your main phone voice (the Speak button / durable dictation) is counted as voice. If you pause a voice-mode reply and then send the already-typed transcript, that one is counted as typed.",
        "Raw keystrokes typed directly into a browser's live terminal stream are not attributed to a surface, so they are not counted. The message composer, and terminal typing on the desktop app, are counted.",
        "Surface (phone / cockpit) for remote input is read from the signed-in device. Remote input with no device identity (a shared-token or fleet call) is not counted as an operator surface.",
    };

    /// <summary>
    /// The hosted refusal for both stats routes (issue #1848), or null on self-host where nothing changes.
    ///
    /// Gated on <see cref="GatewayHostedMode.IsHosted"/> - the INDEPENDENT signal - and not on a boundary
    /// or tenant argument being passed in. A security branch that depends on an optional argument fails
    /// OPEN when a caller forgets it, which is exactly how the hosted account-status fix nearly shipped a
    /// hole: omit the argument and a hosted Gateway silently takes the self-host path. Asking hosted mode
    /// directly means these routes cannot serve the fleet-global feed on hosted however this is wired.
    ///
    /// 404 rather than 403: on hosted this route does not exist as a concept, so "not here" is the truthful
    /// answer. 403 would imply the right credential could reach it, and none can - there is no owner.
    /// </summary>
    private static IResult? DenyOnHosted()
    {
        if (!GatewayHostedMode.IsHosted) return null;

        FileLog.Write("[StatsPageEndpoint] DENIED on hosted: the stats feed is fleet-global and has no per-tenant answer to serve");
        return Results.Json(
            new { error = "the stats dashboard is not available on the hosted gateway" },
            statusCode: StatusCodes.Status404NotFound);
    }

    public static void Map(IEndpointRouteBuilder app, GatewayInputStatsAggregator aggregator,
        GatewaySessionConcurrencyStats? concurrency = null)
    {
        FileLog.Write($"[StatsPageEndpoint] mapping /stats (embedded); hosted={GatewayHostedMode.IsHosted} - on hosted BOTH routes are refused (issue #1848)");

        app.MapGet("/stats/data", (HttpContext ctx) =>
        {
            if (DenyOnHosted() is { } denied) return denied;

            var totals = aggregator.CurrentTotals();
            return Results.Json(new
            {
                generatedAtUtc = DateTime.UtcNow,
                // The display time zone the hourly charts render local clock hours in (IANA id).
                // Auto-defaults to this Gateway machine's own zone; the owner can override it in Settings.
                timeZone = CcDirector.Core.Configuration.TimeZoneConfig.Get(),
                buckets = totals.Buckets,
                // DevThrottle Stats: the "working day" series - turns (by modality) + characters per UTC hour.
                hourlyTurns = aggregator.HourlyTurns(),
                // Wingman usage: turns submitted while a session had voice mode on, and the count of distinct
                // sessions ever in voice mode ("using the wingman" = voice mode on for that session).
                wingman = aggregator.WingmanUsage(),
                // DevThrottle Stats: fleet concurrency (both series: live loaded/running, and actively
                // working). Null until the aggregator is wired (old callers / tests).
                concurrency = concurrency?.Snapshot(DateTime.UtcNow),
                // DevThrottle Stats (private Repos page): the per-repository all-time tally, ranked
                // most-driven first, so the owner can see where development actually happens. Same
                // owner-only auth as the rest of this feed; rendered on a SEPARATE page from Your Throttle
                // so it never rides along when the throttle is shared.
                repos = aggregator.RepoTotals(),
                // DevThrottle Stats (private Agents page): the per-agent all-time tally, ranked most-driven
                // first, so the owner can see which agent CLI the work actually goes through. Unlike the
                // other series this one starts at agentsSinceUtc - the breakdown was added after the totals
                // had been accumulating - so the page states that window rather than implying the earlier
                // turns ran under no agent.
                agents = aggregator.AgentTotals(),
                agentsSinceUtc = aggregator.AgentsSinceUtc,
                // DevThrottle Stats (issue #1637): the per-model all-time tally - which model actually did
                // the work, ranked most-driven first. Like the agents series it starts at modelsSinceUtc
                // rather than at the beginning of the totals, so the page states that window instead of
                // implying the earlier turns ran under no model. A null model in this list is the honest
                // "the agent had not recorded one yet" bucket, not a missing value to hide.
                models = aggregator.ModelTotals(),
                modelsSinceUtc = aggregator.ModelsSinceUtc,
                // DevThrottle Stats (issue #1637): TOKEN SPEND - what the work actually cost. Three views of
                // one number: the all-time total, the per-hour series for "what did I spend today / this
                // week / this month", and the per-model split for "which model cost what". Cumulative,
                // additive tokens only (input / output / cache) - never context-window occupancy, which is a
                // gauge and cannot be summed. Claude-only until other agents' drivers report cumulative spend.
                tokenSpend = aggregator.TokenSpend(),
                tokenSpendByHour = aggregator.TokenSpendByHour(),
                tokenSpendByModel = aggregator.TokenSpendByModel(),
                // DevThrottle Stats (issue #1636): turns the fleet drove into ITSELF - one agent prompting
                // another. Reported alongside the human tally but never inside it: "how do you drive" and
                // "how much does the fleet drive itself" are different questions, and the ratio between
                // them is the leverage the owner actually gets per turn they spend.
                agentDrivenTurns = aggregator.AgentDrivenUsage().Turns,
                agentDrivenCharacters = aggregator.AgentDrivenUsage().Characters,
                notCaptured = NotCaptured,
            });
        });

        app.MapGet("/stats", (HttpContext ctx) =>
        {
            if (DenyOnHosted() is { } denied) return denied.ExecuteAsync(ctx);

            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return ctx.Response.WriteAsync(PageHtml);
        });
    }

    // Self-contained: all CSS and JavaScript inline, no external requests, ASCII only. Light/dark aware.
    private const string PageHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Your Throttle</title>
<style>
  :root {
    --bg: #f5f6f8; --card: #ffffff; --ink: #1b1f24; --muted: #5b636d; --line: #e2e5ea;
    --voice: #2f6feb; --typed: #8a94a3; --accent: #0b8f5a; --warn: #8a6d1a; --warnbg: #fdf6e3;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #12151a; --card: #1b1f26; --ink: #e8ebef; --muted: #9aa3ad; --line: #2a2f38;
      --voice: #5b8def; --typed: #6b7480; --accent: #3fbf86; --warn: #d8b451; --warnbg: #211d10;
    }
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--ink);
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
    line-height: 1.5; }
  .wrap { max-width: 820px; margin: 0 auto; padding: 24px 16px 64px; }
  h1 { font-size: 20px; margin: 0 0 2px; }
  .sub { color: var(--muted); font-size: 13px; margin: 0 0 20px; }
  .cards { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; margin-bottom: 22px; }
  @media (max-width: 560px) { .cards { grid-template-columns: 1fr; } }
  .card { background: var(--card); border: 1px solid var(--line); border-radius: 12px; padding: 18px; }
  .headline .big { font-size: 44px; font-weight: 700; letter-spacing: -1px; line-height: 1; }
  .headline .lbl { color: var(--muted); font-size: 13px; margin-top: 6px; }
  .headline .of { color: var(--muted); font-size: 12px; margin-top: 2px; }
  .section { background: var(--card); border: 1px solid var(--line); border-radius: 12px;
    padding: 18px; margin-bottom: 16px; }
  .section h2 { font-size: 14px; margin: 0 0 12px; text-transform: uppercase; letter-spacing: 0.04em;
    color: var(--muted); }
  .row { margin: 10px 0; }
  .row .top { display: flex; justify-content: space-between; font-size: 13px; margin-bottom: 4px; }
  .row .top .n { color: var(--muted); }
  .bar { height: 12px; border-radius: 6px; background: var(--line); overflow: hidden; }
  .bar > span { display: block; height: 100%; border-radius: 6px; }
  .fill-voice { background: var(--voice); }
  .fill-typed { background: var(--typed); }
  .fill-accent { background: var(--accent); }
  table { width: 100%; border-collapse: collapse; font-size: 13px; }
  th, td { text-align: left; padding: 7px 6px; border-bottom: 1px solid var(--line); }
  th { color: var(--muted); font-weight: 600; }
  td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; }
  .notes { font-size: 12.5px; color: var(--ink); background: var(--warnbg);
    border: 1px solid var(--line); border-left: 3px solid var(--warn); border-radius: 8px;
    padding: 12px 14px; }
  .notes h2 { color: var(--warn); }
  .notes ul { margin: 8px 0 0; padding-left: 18px; }
  .notes li { margin: 6px 0; }
  .empty { color: var(--muted); font-size: 14px; padding: 8px 0; }
  .foot { color: var(--muted); font-size: 12px; margin-top: 18px; }
  .err { color: #c0392b; }
  .toggle { display: inline-flex; border: 1px solid var(--line); border-radius: 8px;
    overflow: hidden; margin-bottom: 14px; }
  .toggle button { border: 0; background: var(--card); color: var(--muted); font: inherit;
    font-size: 13px; padding: 6px 14px; cursor: pointer; border-right: 1px solid var(--line); }
  .toggle button:last-child { border-right: 0; }
  .toggle button.on { background: var(--voice); color: #fff; }
  .lede { color: var(--muted); font-size: 12.5px; margin: -4px 0 12px; }
</style>
</head>
<body>
<div class="wrap">
  <h1>Your Throttle</h1>
  <p class="sub">Where your development actually happens - counted at the Director, so desktop typing is included, not just what reaches the Gateway.</p>

  <div class="cards">
    <div class="card headline">
      <div class="big" id="voiceShare">-</div>
      <div class="lbl">of your turns are spoken</div>
      <div class="of" id="voiceOf"></div>
    </div>
    <div class="card headline">
      <div class="big" id="phoneShare">-</div>
      <div class="lbl">of your turns are from the phone</div>
      <div class="of" id="phoneOf"></div>
    </div>
  </div>

  <p class="sub" id="denom"></p>

  <div class="section">
    <h2>Turns by input - voice vs typed</h2>
    <div id="modalityRows"></div>
  </div>

  <div class="section">
    <h2>Turns by surface - phone vs desktop vs cockpit</h2>
    <div id="surfaceRows"></div>
  </div>

  <div class="section">
    <h2>Character volume (secondary cross-check)</h2>
    <table>
      <thead><tr><th>Modality</th><th>Surface</th><th class="num">Turns</th><th class="num">Characters</th></tr></thead>
      <tbody id="bucketTable"></tbody>
    </table>
  </div>

  <div class="section" id="spendSection">
    <h2>What you have spent</h2>
    <div class="cards">
      <div class="card headline">
        <div class="big" id="tokenTotal">-</div>
        <div class="lbl">tokens spent</div>
        <div class="of" id="tokenBreakdown"></div>
      </div>
      <div class="card headline">
        <div class="big" id="turnTotal">-</div>
        <div class="lbl">turns submitted, all time</div>
        <div class="of" id="turnChars"></div>
      </div>
    </div>
    <p class="lede" id="spendNote"></p>
  </div>

  <div class="section">
    <h2>Your activity</h2>
    <div class="toggle" id="periodToggle">
      <button type="button" data-period="day" class="on">By day</button>
      <button type="button" data-period="week">By week</button>
      <button type="button" data-period="month">By month</button>
    </div>
    <p class="lede" id="activityLede">Turns and token spend, grouped by your local calendar. Most recent first.</p>
    <table>
      <thead><tr><th id="periodHead">Day</th><th class="num">Turns</th><th class="num">Tokens</th></tr></thead>
      <tbody id="activityTable"></tbody>
    </table>
  </div>

  <div class="section">
    <h2>Spend by model</h2>
    <table>
      <thead><tr><th>Model</th><th class="num">Tokens</th><th class="num">Share</th></tr></thead>
      <tbody id="modelSpendTable"></tbody>
    </table>
  </div>

  <div class="notes section">
    <h2>What is counted, and what is not-captured</h2>
    <ul id="notCaptured"></ul>
  </div>

  <div class="foot" id="foot"></div>
</div>

<script>
(function () {
  var TITLE = { voice: "Voice", typed: "Typed", phone: "Phone", desktop: "Desktop", cockpit: "Cockpit", unknown: "Unknown" };

  function pct(part, whole) { return whole > 0 ? Math.round((part / whole) * 100) : 0; }
  function fmt(n) { return (n || 0).toLocaleString(); }

  // Compact form for the big token numbers, which run to millions: 1234567 -> "1.2M". The exact figure is
  // always available in the by-model and by-period tables below, so the headline trades precision for a
  // number the eye can read at a glance.
  function fmtCompact(n) {
    n = n || 0;
    if (n >= 1e9) return (n / 1e9).toFixed(1).replace(/\.0$/, "") + "B";
    if (n >= 1e6) return (n / 1e6).toFixed(1).replace(/\.0$/, "") + "M";
    if (n >= 1e3) return (n / 1e3).toFixed(1).replace(/\.0$/, "") + "K";
    return String(n);
  }

  // The local calendar date ("YYYY-MM-DD") of a UTC hour key, in the Gateway's configured display zone. The
  // stored hour keys are UTC ("yyyy-MM-ddTHH"); grouping by the OWNER'S local day/week/month is what makes
  // "what did I do today" mean his today, not UTC's. en-CA formats as YYYY-MM-DD, which sorts correctly as a
  // string. A bad/blank zone falls back to the browser's own, never throws.
  function localYmd(hourUtc, tz) {
    var d = new Date(hourUtc + ":00:00Z");
    try {
      var f = new Intl.DateTimeFormat("en-CA", { timeZone: tz || undefined, year: "numeric", month: "2-digit", day: "2-digit" });
      var p = {}; f.formatToParts(d).forEach(function (x) { p[x.type] = x.value; });
      return p.year + "-" + p.month + "-" + p.day;
    } catch (e) {
      return d.getFullYear() + "-" + String(d.getMonth() + 1).padStart(2, "0") + "-" + String(d.getDate()).padStart(2, "0");
    }
  }

  // Monday-of-the-week for a local YYYY-MM-DD, as YYYY-MM-DD. Computed from the calendar date alone (parsed
  // as a plain local date), so it never depends on the hour or the zone offset - the date was already
  // resolved to the owner's zone by localYmd.
  function weekStart(ymd) {
    var parts = ymd.split("-");
    var d = new Date(Number(parts[0]), Number(parts[1]) - 1, Number(parts[2]));
    var dow = (d.getDay() + 6) % 7; // Monday = 0
    d.setDate(d.getDate() - dow);
    return d.getFullYear() + "-" + String(d.getMonth() + 1).padStart(2, "0") + "-" + String(d.getDate()).padStart(2, "0");
  }

  var MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  function labelDay(ymd) { var p = ymd.split("-"); return MONTHS[Number(p[1]) - 1] + " " + Number(p[2]) + ", " + p[0]; }
  function labelWeek(ymd) { var p = ymd.split("-"); return "Week of " + MONTHS[Number(p[1]) - 1] + " " + Number(p[2]); }
  function labelMonth(ym) { var p = ym.split("-"); return MONTHS[Number(p[1]) - 1] + " " + p[0]; }

  // How many recent periods to show, by grain. Enough to see a trend without an endless table.
  var PERIOD_LIMIT = { day: 14, week: 8, month: 6 };

  // Build a <tr> from cells using textContent, never innerHTML. cells = [{ t: text, num: bool }]. This is the
  // ONLY way an untrusted value reaches these tables: the model name is records-derived free text taken from
  // the agent's own transcript, so composing it into an HTML string would let a model string containing
  // markup parse as HTML in the Gateway's own origin. textContent makes that impossible by construction -
  // the same discipline the page's row() helper already uses for the bar rows above.
  function trow(cells) {
    var tr = document.createElement("tr");
    cells.forEach(function (c) {
      var td = document.createElement("td");
      if (c.num) td.className = "num";
      td.textContent = c.t;
      tr.appendChild(td);
    });
    return tr;
  }

  function sumBy(buckets, field, keyName, keyVal) {
    var t = 0;
    for (var i = 0; i < buckets.length; i++) {
      if (buckets[i][keyName] === keyVal) t += (buckets[i][field] || 0);
    }
    return t;
  }
  function total(buckets, field) {
    var t = 0; for (var i = 0; i < buckets.length; i++) t += (buckets[i][field] || 0); return t;
  }

  function row(label, value, whole, fillClass) {
    var p = pct(value, whole);
    var div = document.createElement("div");
    div.className = "row";
    var top = document.createElement("div"); top.className = "top";
    var l = document.createElement("span"); l.textContent = label;
    var n = document.createElement("span"); n.className = "n"; n.textContent = fmt(value) + " turns (" + p + "%)";
    top.appendChild(l); top.appendChild(n);
    var bar = document.createElement("div"); bar.className = "bar";
    var span = document.createElement("span"); span.className = fillClass; span.style.width = p + "%";
    bar.appendChild(span);
    div.appendChild(top); div.appendChild(bar);
    return div;
  }

  function render(data) {
    var buckets = data.buckets || [];
    var turns = total(buckets, "turns");

    var voiceTurns = sumBy(buckets, "turns", "modality", "voice");
    var phoneTurns = sumBy(buckets, "turns", "surface", "phone");

    document.getElementById("voiceShare").textContent = turns > 0 ? pct(voiceTurns, turns) + "%" : "--";
    document.getElementById("phoneShare").textContent = turns > 0 ? pct(phoneTurns, turns) + "%" : "--";
    document.getElementById("voiceOf").textContent = turns > 0 ? fmt(voiceTurns) + " of " + fmt(turns) + " turns" : "";
    document.getElementById("phoneOf").textContent = turns > 0 ? fmt(phoneTurns) + " of " + fmt(turns) + " turns" : "";

    // The denominator behind the shares, and the excluded/unknown-surface count, so the shares are always
    // inspectable and you can see the unknown slice is small (decision 9: surface it, never hide it).
    var unknownTurns = sumBy(buckets, "turns", "surface", "unknown");
    document.getElementById("denom").textContent = turns > 0
      ? ("Shares are over " + fmt(turns) + " counted turns. Unknown surface: " + fmt(unknownTurns) + " turn" + (unknownTurns === 1 ? "" : "s") + ".")
      : "";

    var mod = document.getElementById("modalityRows"); mod.innerHTML = "";
    var surf = document.getElementById("surfaceRows"); surf.innerHTML = "";
    if (turns === 0) {
      mod.innerHTML = '<div class="empty">No turns counted yet. Send a message by voice from your phone, or type one from the desktop, and it will appear here.</div>';
      surf.innerHTML = '<div class="empty">No turns counted yet.</div>';
    } else {
      mod.appendChild(row(TITLE.voice, voiceTurns, turns, "fill-voice"));
      mod.appendChild(row(TITLE.typed, sumBy(buckets, "turns", "modality", "typed"), turns, "fill-typed"));
      ["phone", "desktop", "cockpit", "unknown"].forEach(function (s) {
        var v = sumBy(buckets, "turns", "surface", s);
        if (v > 0 || s !== "unknown") surf.appendChild(row(TITLE[s], v, turns, "fill-accent"));
      });
    }

    var tb = document.getElementById("bucketTable"); tb.innerHTML = "";
    if (buckets.length === 0) {
      tb.innerHTML = '<tr><td colspan="4" class="empty">Nothing counted yet.</td></tr>';
    } else {
      buckets.slice().sort(function (a, b) { return (b.characters || 0) - (a.characters || 0); }).forEach(function (b) {
        var tr = document.createElement("tr");
        tr.innerHTML = "<td>" + (TITLE[b.modality] || b.modality) + "</td><td>" + (TITLE[b.surface] || b.surface) +
          "</td><td class='num'>" + fmt(b.turns) + "</td><td class='num'>" + fmt(b.characters) + "</td>";
        tb.appendChild(tr);
      });
    }

    renderSpend(data, turns);
    renderActivity(data);
    renderModelSpend(data);

    var nc = document.getElementById("notCaptured"); nc.innerHTML = "";
    (data.notCaptured || []).forEach(function (m) {
      var li = document.createElement("li"); li.textContent = m; nc.appendChild(li);
    });

    var when = data.generatedAtUtc ? new Date(data.generatedAtUtc).toLocaleString() : "";
    document.getElementById("foot").textContent = "Updated " + when + " - refreshes every few seconds. Counts only; no message text ever leaves your machine for this page.";
  }

  function renderSpend(data, turns) {
    var spend = data.tokenSpend || {};
    var totalTokens = spend.totalTokens || 0;
    document.getElementById("tokenTotal").textContent = totalTokens > 0 ? fmtCompact(totalTokens) : "--";
    document.getElementById("tokenBreakdown").textContent = totalTokens > 0
      ? (fmt(spend.inputTokens) + " in / " + fmt(spend.outputTokens) + " out / "
         + fmt((spend.cacheReadTokens || 0) + (spend.cacheCreationTokens || 0)) + " cache")
      : "";

    var chars = total(data.buckets || [], "characters");
    document.getElementById("turnTotal").textContent = turns > 0 ? fmt(turns) : "--";
    document.getElementById("turnChars").textContent = turns > 0 ? fmt(chars) + " characters" : "";

    // Say plainly that spend is Claude-only today, so a small number is read as "only Claude reports it yet",
    // never as "I barely spent anything". Honesty caveat, same spirit as the not-captured notes.
    document.getElementById("spendNote").textContent = totalTokens > 0
      ? "Token spend is recorded for agents that report it from their own records - Claude today. Other agents show no spend until their drivers report it."
      : "No token spend recorded yet. It appears once an agent that reports its usage - Claude today - finishes a turn.";
  }

  // Roll the per-hour turn and token series into the owner's local day / week / month buckets, newest first.
  function renderActivity(data) {
    var tz = data.timeZone;
    var hoursTurns = data.hourlyTurns || [];
    var hoursTokens = data.tokenSpendByHour || [];
    var period = PERIOD;

    var keyOf = period === "month"
      ? function (ymd) { return ymd.slice(0, 7); }
      : period === "week"
        ? function (ymd) { return weekStart(ymd); }
        : function (ymd) { return ymd; };

    var acc = {}; // key -> { turns, tokens }
    function bump(hourUtc, field, value) {
      if (!value) return;
      var key = keyOf(localYmd(hourUtc, tz));
      if (!acc[key]) acc[key] = { turns: 0, tokens: 0 };
      acc[key][field] += value;
    }
    hoursTurns.forEach(function (h) { bump(h.hour, "turns", h.turns || 0); });
    hoursTokens.forEach(function (h) { bump(h.hour, "tokens", h.totalTokens || 0); });

    var label = period === "month" ? labelMonth : period === "week" ? labelWeek : labelDay;
    document.getElementById("periodHead").textContent = period.charAt(0).toUpperCase() + period.slice(1);

    var keys = Object.keys(acc).sort().reverse().slice(0, PERIOD_LIMIT[period]);
    var tb = document.getElementById("activityTable"); tb.innerHTML = "";
    if (keys.length === 0) {
      tb.innerHTML = '<tr><td colspan="3" class="empty">Nothing recorded in the retained window yet.</td></tr>';
      return;
    }
    keys.forEach(function (k) {
      tb.appendChild(trow([{ t: label(k) }, { t: fmt(acc[k].turns), num: true }, { t: fmt(acc[k].tokens), num: true }]));
    });
  }

  function renderModelSpend(data) {
    var rows = data.tokenSpendByModel || [];
    var grand = 0; rows.forEach(function (r) { grand += (r.totalTokens || 0); });
    var tb = document.getElementById("modelSpendTable"); tb.innerHTML = "";
    if (rows.length === 0) {
      tb.innerHTML = '<tr><td colspan="3" class="empty">No token spend recorded yet.</td></tr>';
      return;
    }
    rows.forEach(function (r) {
      // A null model is the honest "not recorded yet" bucket - the first turn of every session folds before
      // its model is known - shown as such, never hidden and never an empty name. The name is UNTRUSTED
      // free text and reaches the DOM only through trow's textContent, so markup in it renders as literal
      // characters, never as HTML.
      var name = r.model ? r.model : "Not recorded";
      tb.appendChild(trow([{ t: name }, { t: fmt(r.totalTokens), num: true }, { t: pct(r.totalTokens, grand) + "%", num: true }]));
    });
  }

  function load() {
    fetch("/stats/data", { credentials: "same-origin" })
      .then(function (r) { if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); })
      .then(function (data) { LAST = data; render(data); })
      .catch(function (e) { document.getElementById("foot").innerHTML = '<span class="err">Could not load stats: ' + e.message + "</span>"; });
  }

  // The chosen day/week/month grain, kept across the 4-second refreshes and re-applied to the latest data
  // without a refetch, so a periodic reload never snaps the toggle back to Day mid-read.
  var LAST = null;
  var PERIOD = "day";
  document.getElementById("periodToggle").addEventListener("click", function (e) {
    var btn = e.target.closest("button"); if (!btn) return;
    PERIOD = btn.getAttribute("data-period");
    Array.prototype.forEach.call(this.querySelectorAll("button"), function (b) {
      b.classList.toggle("on", b === btn);
    });
    if (LAST) renderActivity(LAST);
  });

  load();
  setInterval(load, 4000);
})();
</script>
</body>
</html>
""";
}
