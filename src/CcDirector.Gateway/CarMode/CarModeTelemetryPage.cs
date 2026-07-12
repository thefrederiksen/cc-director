namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The self-contained HTML for GET /carmode/telemetry (Car Mode performance round): a private dashboard the
/// owner opens on the Gateway to see, per turn, exactly where a Car Mode turn spent its time - the client
/// stamps (pause to transcribe, the brain round trip, first audio) and the server stamps (each model call,
/// the fleet reads, the whole-turn wall-clock). It reads the Gateway's own telemetry store with no cloud
/// round trip. Embedded in the binary (not a wwwroot React route) so it works even on a plain dev build,
/// exactly like the Stats page. All CSS and JavaScript inline, no external requests, ASCII only,
/// light/dark aware.
/// </summary>
internal static class CarModeTelemetryPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Car Mode Timing</title>
<style>
  :root {
    --bg: #f5f6f8; --card: #ffffff; --ink: #1b1f24; --muted: #5b636d; --line: #e2e5ea;
    --accent: #2f6feb; --good: #0b8f5a; --warn: #8a6d1a; --bad: #c0392b;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #12151a; --card: #1b1f26; --ink: #e8ebef; --muted: #9aa3ad; --line: #2a2f38;
      --accent: #5b8def; --good: #3fbf86; --warn: #d8b451; --bad: #e06c5b;
    }
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--ink);
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
    line-height: 1.5; }
  .wrap { max-width: 1100px; margin: 0 auto; padding: 24px 16px 64px; }
  h1 { font-size: 20px; margin: 0 0 2px; }
  .sub { color: var(--muted); font-size: 13px; margin: 0 0 20px; }
  .cards { display: grid; grid-template-columns: repeat(4, 1fr); gap: 12px; margin-bottom: 22px; }
  @media (max-width: 720px) { .cards { grid-template-columns: 1fr 1fr; } }
  .card { background: var(--card); border: 1px solid var(--line); border-radius: 12px; padding: 16px; }
  .card .big { font-size: 30px; font-weight: 700; letter-spacing: -0.5px; line-height: 1; }
  .card .lbl { color: var(--muted); font-size: 12.5px; margin-top: 6px; }
  .card .sm { color: var(--muted); font-size: 11.5px; margin-top: 2px; }
  .section { background: var(--card); border: 1px solid var(--line); border-radius: 12px;
    padding: 12px 14px; margin-bottom: 16px; overflow-x: auto; }
  .section h2 { font-size: 14px; margin: 4px 2px 10px; text-transform: uppercase; letter-spacing: 0.04em;
    color: var(--muted); }
  table { width: 100%; border-collapse: collapse; font-size: 12.5px; white-space: nowrap; }
  th, td { text-align: right; padding: 6px 8px; border-bottom: 1px solid var(--line);
    font-variant-numeric: tabular-nums; }
  th:first-child, td:first-child { text-align: left; }
  th { color: var(--muted); font-weight: 600; position: sticky; top: 0; background: var(--card); }
  td.slow { color: var(--bad); font-weight: 600; }
  td.zero { color: var(--good); }
  .empty { color: var(--muted); font-size: 14px; padding: 12px 4px; }
  .foot { color: var(--muted); font-size: 12px; margin-top: 18px; }
  .err { color: var(--bad); }
  .pill { display: inline-block; font-size: 11px; padding: 1px 7px; border-radius: 20px;
    border: 1px solid var(--line); color: var(--muted); margin-left: 6px; }
</style>
</head>
<body>
<div class="wrap">
  <h1>Car Mode Timing</h1>
  <p class="sub">Per-turn, per-stage latency for hands-free Car Mode - measured end to end, client and server. All numbers are milliseconds. Counts and timings only; no command or reply text is ever recorded.</p>

  <div class="cards" id="cards"></div>

  <div class="section">
    <h2>Recent turns <span class="pill" id="heldPill"></span></h2>
    <table id="tbl">
      <thead>
        <tr>
          <th>When</th>
          <th>Total<br>(felt)</th>
          <th>Pause-&gt;<br>transcribe</th>
          <th>Brain<br>(round trip)</th>
          <th>First<br>audio</th>
          <th>TTS<br>(1st)</th>
          <th>Server<br>total</th>
          <th>Model<br>calls</th>
          <th>Model<br>ms</th>
          <th>Fleet<br>reads</th>
          <th>Fleet<br>ms</th>
          <th>Rounds</th>
          <th>Cmd<br>chars</th>
          <th>Reply<br>chars</th>
        </tr>
      </thead>
      <tbody id="rows"></tbody>
    </table>
  </div>

  <div class="foot" id="foot"></div>
</div>

<script>
(function () {
  function ms(n) { return (n == null) ? "-" : Math.round(n).toLocaleString(); }
  function median(arr) {
    if (!arr.length) return null;
    var a = arr.slice().sort(function (x, y) { return x - y; });
    var m = Math.floor(a.length / 2);
    return a.length % 2 ? a[m] : (a[m - 1] + a[m]) / 2;
  }
  function pctile(arr, p) {
    if (!arr.length) return null;
    var a = arr.slice().sort(function (x, y) { return x - y; });
    var idx = Math.min(a.length - 1, Math.floor((p / 100) * a.length));
    return a[idx];
  }
  function card(big, lbl, sm) {
    var d = document.createElement("div"); d.className = "card";
    var b = document.createElement("div"); b.className = "big"; b.textContent = big;
    var l = document.createElement("div"); l.className = "lbl"; l.textContent = lbl;
    d.appendChild(b); d.appendChild(l);
    if (sm) { var s = document.createElement("div"); s.className = "sm"; s.textContent = sm; d.appendChild(s); }
    return d;
  }

  function render(data) {
    var recs = data.records || [];
    var cards = document.getElementById("cards"); cards.innerHTML = "";
    document.getElementById("heldPill").textContent = (data.held || 0) + " stored";

    if (!recs.length) {
      cards.appendChild(card("-", "No turns recorded yet", "Take a turn in Car Mode on the phone"));
      document.getElementById("rows").innerHTML = '<tr><td colspan="14" class="empty">No turns recorded yet. Open Car Mode on the phone, say something and "over and out", and it will appear here.</td></tr>';
      document.getElementById("foot").textContent = "";
      return;
    }

    var totals = recs.map(function (r) { return r.totalTurnMs; }).filter(function (n) { return n > 0; });
    var brains = recs.map(function (r) { return r.brainMs; }).filter(function (n) { return n > 0; });
    var firsts = recs.map(function (r) { return r.firstAudioMs; }).filter(function (n) { return n > 0; });
    var fleetHit = recs.filter(function (r) { return (r.fleetReadCount || 0) > 0; }).length;

    cards.appendChild(card(ms(median(totals)), "Median total (felt)", "p90 " + ms(pctile(totals, 90))));
    cards.appendChild(card(ms(median(brains)), "Median brain round trip", "p90 " + ms(pctile(brains, 90))));
    cards.appendChild(card(ms(median(firsts)), "Median time to first audio", "p90 " + ms(pctile(firsts, 90))));
    cards.appendChild(card(fleetHit + " / " + recs.length, "Turns that read the fleet", "the rest answered without it"));

    var body = document.getElementById("rows"); body.innerHTML = "";
    recs.forEach(function (r) {
      var tr = document.createElement("tr");
      function td(v, cls) {
        var c = document.createElement("td");
        if (cls) c.className = cls;
        c.textContent = v;
        return c;
      }
      var when = r.receivedAtUtc ? new Date(r.receivedAtUtc).toLocaleTimeString() : "-";
      tr.appendChild(td(when));
      tr.appendChild(td(ms(r.totalTurnMs), r.totalTurnMs > 6000 ? "slow" : ""));
      tr.appendChild(td(ms(r.pauseToTranscribeMs)));
      tr.appendChild(td(ms(r.brainMs), r.brainMs > 5000 ? "slow" : ""));
      tr.appendChild(td(ms(r.firstAudioMs)));
      tr.appendChild(td(ms(r.ttsMs)));
      tr.appendChild(td(ms(r.serverTotalMs)));
      tr.appendChild(td(r.modelCallCount == null ? "-" : r.modelCallCount));
      tr.appendChild(td(ms(r.modelMsTotal)));
      tr.appendChild(td(r.fleetReadCount == null ? "-" : r.fleetReadCount, (r.fleetReadCount || 0) === 0 ? "zero" : ""));
      tr.appendChild(td(ms(r.fleetReadMsTotal)));
      tr.appendChild(td(r.rounds == null ? "-" : r.rounds));
      tr.appendChild(td(r.commandChars == null ? "-" : r.commandChars));
      tr.appendChild(td(r.replyChars == null ? "-" : r.replyChars));
      body.appendChild(tr);
    });

    var when = data.generatedAtUtc ? new Date(data.generatedAtUtc).toLocaleString() : "";
    document.getElementById("foot").textContent = "Updated " + when + " - refreshes every 5 seconds. Retained about 90 days. Counts and timings only; no message text.";
  }

  function load() {
    fetch("/carmode/telemetry/data?limit=200", { credentials: "same-origin" })
      .then(function (r) { if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); })
      .then(render)
      .catch(function (e) { document.getElementById("foot").innerHTML = '<span class="err">Could not load telemetry: ' + e.message + "</span>"; });
  }

  load();
  setInterval(load, 5000);
})();
</script>
</body>
</html>
""";
}
