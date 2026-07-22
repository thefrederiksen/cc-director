namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The self-contained HTML for GET /carmode/diagnostics (Car Mode performance round): a private dashboard the
/// owner opens on the Gateway to see, per turn, exactly where a Car Mode turn spent its time - the client
/// stamps (pause to transcribe, the brain round trip, first audio) and the server stamps (each model call,
/// the fleet reads, the whole-turn wall-clock). It reads the Gateway's own diagnostics store with no cloud
/// round trip. Embedded in the binary (not a wwwroot React route) so it works even on a plain dev build,
/// exactly like the Stats page. All CSS and JavaScript inline, no external requests, ASCII only,
/// light/dark aware.
/// </summary>
internal static class CarModeDiagnosticsPage
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
  button { border: 1px solid var(--line); background: var(--card); color: var(--ink);
    border-radius: 8px; padding: 7px 10px; cursor: pointer; }
</style>
</head>
<body>
<div class="wrap">
  <h1>Car Mode Timing</h1>
  <p class="sub">Per-turn, per-stage latency AND the two mobile failure diagnostics for hands-free Car Mode. The pipeline a real phone turn walks: pause, transcode (phone), transcribe, brain round trip, the network gap (brain minus server), each model call, the fleet read, text-to-speech, and first audio. The "over and out" finickiness shows as End-phrase tries (how many transcribe attempts before the turn was taken). The cut-off reply is split several ways: Clip length (the whole reply synthesized), Played to (how far playback reached), Play blocked (the reply's play() was refused by the mobile autoplay policy so it never sounded - the data-confirmed failure), and Mic in playback (whether the rolling "stop" watch re-opened the microphone while the reply was playing - a secondary suspect). The button-cut-off bug is proven from the phone: Buttons on-screen (were the primary buttons within the visible viewport this turn), Footer bottom (where the buttons end, in pixels), and the three raw viewport reads (Visual vp = window.visualViewport.height, Inner vp = window.innerHeight, Client vp = documentElement.clientHeight). All times are milliseconds. Counts and timings only; no command or reply text is ever recorded.</p>

  <div class="cards" id="cards"></div>

  <div class="section">
    <h2>Recent turns <span class="pill" id="heldPill"></span></h2>
    <table id="tbl">
      <thead>
        <tr>
          <th>When</th>
          <th>Total<br>(felt)</th>
          <th>End-phrase<br>tries</th>
          <th>Pause-&gt;<br>transcribe</th>
          <th>Transcode<br>(phone)</th>
          <th>Brain<br>(round trip)</th>
          <th>Network<br>(brain-server)</th>
          <th>Server<br>total</th>
          <th>Model<br>calls</th>
          <th>Model<br>ms</th>
          <th>Fleet<br>reads</th>
          <th>Fleet<br>ms</th>
          <th>TTS<br>(reply)</th>
          <th>First<br>audio</th>
          <th>Reply<br>played</th>
          <th>Clip<br>length</th>
          <th>Played<br>to</th>
          <th>Whole<br>reply?</th>
          <th>Play<br>blocked</th>
          <th>Mic in<br>playback</th>
          <th>Stop<br>polls</th>
          <th>Clips</th>
          <th>Rounds</th>
          <th>Cmd<br>chars</th>
          <th>Reply<br>chars</th>
          <th>Buttons<br>on-screen</th>
          <th>Footer<br>bottom</th>
          <th>Visual<br>vp</th>
          <th>Inner<br>vp</th>
          <th>Client<br>vp</th>
        </tr>
      </thead>
      <tbody id="rows"></tbody>
    </table>
  </div>

  <div class="foot" id="foot"></div>
  <button type="button" id="clear">Clear this device's local diagnostics</button>
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
      document.getElementById("rows").innerHTML = '<tr><td colspan="30" class="empty">No turns recorded yet. Open Car Mode on the phone, say something and "over and out", and it will appear here.</td></tr>';
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
    // The cut-off-reply watch: how many recent replies were synthesized but NOT heard to the end. This is
    // the card that would have made the "only the last couple of words" bug obvious the first time.
    var cutOff = recs.filter(function (r) { return r.completed === false; }).length;
    cards.appendChild(card(cutOff + " / " + recs.length, "Replies NOT fully heard",
      cutOff > 0 ? "these were cut off before the end" : "every reply played to its end"));

    // Finickiness of "over and out": the median number of transcribe tries before a turn was taken. 1 means
    // the phrase landed on the first try; a higher median means the phone kept missing it.
    var tries = recs.map(function (r) { return r.transcribeAttempts; }).filter(function (n) { return n > 0; });
    cards.appendChild(card(tries.length ? String(median(tries)) : "-", "Median 'over and out' tries",
      "1 = landed first try; higher = finicky"));

    // The data-confirmed mobile failure: how many recent replies were BLOCKED by the autoplay policy so
    // they never sounded. With the unlock-on-Start fix this should be 0.
    var blocked = recs.filter(function (r) { return r.playRejected === true; }).length;
    cards.appendChild(card(blocked + " / " + recs.length, "Replies BLOCKED (autoplay)",
      blocked > 0 ? "play() refused - the reply never sounded" : "no autoplay block - replies could sound"));

    // The mic-contention suspect: how many recent replies had the microphone re-opened WHILE they played.
    // On mobile this is a secondary suspect for a cut-off / half-heard reply.
    var micIn = recs.filter(function (r) { return r.micReacquiredDuringPlayback === true; }).length;
    cards.appendChild(card(micIn + " / " + recs.length, "Mic re-opened during the reply",
      "secondary cut-off suspect: mic grabbed mid-playback"));

    // v5 button-cut-off proof, read from the phone: how many recent turns had the footer's primary buttons
    // fall PAST the visible viewport (footerBottom > the visible height). 0 means the buttons were on-screen
    // every turn - the direct, from-the-device confirmation that the cut-off is fixed.
    var btnCut = recs.filter(function (r) { return r.footerVisible === false; }).length;
    cards.appendChild(card(btnCut + " / " + recs.length, "Turns with buttons CUT OFF",
      btnCut > 0 ? "footer fell past the visible viewport" : "buttons on-screen every turn"));

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
      // Network the browser saw (its brain round trip minus what the server spent inside), so a bad phone
      // network shows up as a large gap that the loopback numbers can never reveal.
      var net = (r.brainMs != null && r.serverTotalMs != null) ? Math.max(0, r.brainMs - r.serverTotalMs) : null;
      // A reply whose playback stopped well before the synthesized clip ended: the media-time cut-off. Flags
      // the case the "Whole reply?" flag alone can miss (played-to far below the clip length).
      var playedShort = (r.clipDurationMs != null && r.playedToMs != null && r.clipDurationMs > 1000
        && r.playedToMs < r.clipDurationMs - 750);
      tr.appendChild(td(when));
      tr.appendChild(td(ms(r.totalTurnMs), r.totalTurnMs > 6000 ? "slow" : ""));
      // End-phrase tries: how many transcribe attempts before the turn was taken. More than 1 is finicky.
      tr.appendChild(td(r.transcribeAttempts == null ? "-" : r.transcribeAttempts,
        (r.transcribeAttempts != null && r.transcribeAttempts > 1) ? "slow" : ""));
      tr.appendChild(td(ms(r.pauseToTranscribeMs), r.pauseToTranscribeMs > 4000 ? "slow" : ""));
      tr.appendChild(td(ms(r.transcodeMs)));
      tr.appendChild(td(ms(r.brainMs), r.brainMs > 5000 ? "slow" : ""));
      tr.appendChild(td(ms(net), (net != null && net > 2000) ? "slow" : ""));
      tr.appendChild(td(ms(r.serverTotalMs)));
      tr.appendChild(td(r.modelCallCount == null ? "-" : r.modelCallCount));
      tr.appendChild(td(ms(r.modelMsTotal)));
      tr.appendChild(td(r.fleetReadCount == null ? "-" : r.fleetReadCount, (r.fleetReadCount || 0) === 0 ? "zero" : ""));
      tr.appendChild(td(ms(r.fleetReadMsTotal)));
      tr.appendChild(td(ms(r.ttsMs), r.ttsMs > 3000 ? "slow" : ""));
      tr.appendChild(td(ms(r.firstAudioMs)));
      // The reply-audio lifecycle (the cut-off-reply diagnostic): how long the reply was actually audible,
      // the whole SYNTHESIZED clip length vs how far playback reached (the media-time cut-off), whether it
      // played to its end, whether the mic was re-opened mid-playback (the suspect), how many "stop" polls
      // ran, and how many clips it took (1 since the split was reverted). A reply that was NOT fully heard -
      // by the completed flag OR by playing short of the clip - is called out in red.
      tr.appendChild(td(ms(r.playMs)));
      tr.appendChild(td(ms(r.clipDurationMs)));
      tr.appendChild(td(ms(r.playedToMs), playedShort ? "slow" : ""));
      tr.appendChild(td(r.completed == null ? "-" : (r.completed ? "yes" : "CUT OFF"), r.completed === false ? "slow" : ""));
      // Play blocked: the reply's play() was refused by the mobile autoplay policy so it never sounded -
      // the data-confirmed mobile failure. Called out in red; "no" (with the unlock fix) is the good state.
      tr.appendChild(td(r.playRejected == null ? "-" : (r.playRejected ? "BLOCKED" : "no"), r.playRejected === true ? "slow" : ""));
      tr.appendChild(td(r.micReacquiredDuringPlayback == null ? "-" : (r.micReacquiredDuringPlayback ? "YES" : "no"),
        r.micReacquiredDuringPlayback === true ? "slow" : ""));
      tr.appendChild(td(r.speakingPollCount == null ? "-" : r.speakingPollCount));
      tr.appendChild(td(r.chunks == null ? "-" : r.chunks, (r.chunks != null && r.chunks !== 1) ? "slow" : ""));
      tr.appendChild(td(r.rounds == null ? "-" : r.rounds));
      tr.appendChild(td(r.commandChars == null ? "-" : r.commandChars));
      tr.appendChild(td(r.replyChars == null ? "-" : r.replyChars));
      // v5 button-visibility (from the phone): were the primary buttons on-screen, where did the footer end,
      // and the three raw viewport reads. "CUT OFF" in red is the bug this whole round is chasing.
      tr.appendChild(td(r.footerVisible == null ? "-" : (r.footerVisible ? "yes" : "CUT OFF"), r.footerVisible === false ? "slow" : ""));
      tr.appendChild(td(ms(r.footerBottom)));
      tr.appendChild(td(ms(r.visualViewportHeight)));
      tr.appendChild(td(ms(r.viewportInnerHeight)));
      tr.appendChild(td(ms(r.documentClientHeight)));
      body.appendChild(tr);
    });

    var when = data.generatedAtUtc ? new Date(data.generatedAtUtc).toLocaleString() : "";
    document.getElementById("foot").textContent = "Updated " + when + " - refreshes every 5 seconds. Retained about 90 days. Counts and timings only; no message text.";
  }

  function load() {
    fetch("/carmode/diagnostics/data?limit=200", { credentials: "same-origin" })
      .then(function (r) { if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); })
      .then(render)
      .catch(function (e) { document.getElementById("foot").innerHTML = '<span class="err">Could not load diagnostics: ' + e.message + "</span>"; });
  }

  load();
  document.getElementById("clear").addEventListener("click", function () {
    if (!window.confirm("Delete this device's retained Car Mode diagnostics?")) return;
    fetch("/carmode/diagnostics", { method: "DELETE", credentials: "same-origin" })
      .then(function (r) { if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); })
      .then(load)
      .catch(function (e) { document.getElementById("foot").textContent = "Could not clear diagnostics: " + e.message; });
  });
  setInterval(load, 5000);
})();
</script>
</body>
</html>
""";
}
