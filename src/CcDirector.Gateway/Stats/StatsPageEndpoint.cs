using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The DevThrottle Stats private dashboard: the always-available page the owner opens on the Gateway to
/// see, from real usage, how much of his development is spoken vs typed and how much comes from the phone,
/// the desktop, or the cockpit. Served as a SELF-CONTAINED page embedded in this binary (not a wwwroot
/// React route), so it works even on a plain dev build where the React apps are not built (mission
/// New build B, core finding 8). It reads the Gateway's own aggregated totals with no cloud round-trip.
///
/// Two routes, both behind the normal Gateway auth (the owner's signed-in browser reaches them):
///   GET /stats       - the HTML dashboard (this embedded page).
///   GET /stats/data  - the aggregated totals as JSON, which the page fetches and refreshes.
///
/// Only counts and ratios are ever served - never the text of anything typed or said (mission decision 5).
/// The page states plainly which input paths are counted and which are not-captured (no-fallback rule).
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

    public static void Map(IEndpointRouteBuilder app, GatewayInputStatsAggregator aggregator,
        GatewaySessionConcurrencyStats? concurrency = null)
    {
        FileLog.Write("[StatsPageEndpoint] serving /stats (embedded, always available)");

        app.MapGet("/stats/data", () =>
        {
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
                notCaptured = NotCaptured,
            });
        });

        app.MapGet("/stats", (HttpContext ctx) =>
        {
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

    var nc = document.getElementById("notCaptured"); nc.innerHTML = "";
    (data.notCaptured || []).forEach(function (m) {
      var li = document.createElement("li"); li.textContent = m; nc.appendChild(li);
    });

    var when = data.generatedAtUtc ? new Date(data.generatedAtUtc).toLocaleString() : "";
    document.getElementById("foot").textContent = "Updated " + when + " - refreshes every few seconds. Counts only; no message text ever leaves your machine for this page.";
  }

  function load() {
    fetch("/stats/data", { credentials: "same-origin" })
      .then(function (r) { if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); })
      .then(render)
      .catch(function (e) { document.getElementById("foot").innerHTML = '<span class="err">Could not load stats: ' + e.message + "</span>"; });
  }

  load();
  setInterval(load, 4000);
})();
</script>
</body>
</html>
""";
}
