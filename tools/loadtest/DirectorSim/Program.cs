using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.LoadTest.Shared;
using Microsoft.AspNetCore.SignalR.Client;

// Stage 2 of the Gateway load-test plan (devthrottle_internal issue #1173): the Director push simulator.
//
// Opens N HubConnections to /director-stream (one per synthetic Director key from the LoadRig's
// directors.json), sends the real Hello + PushSnapshot, then streams PushDelta at a target TOTAL rate
// across all connections, keeping every per-connection sequence strictly increasing (the
// PushedSessionStore's acceptance rule). With EVENTS_PER_SEC=0 it holds the connections and their pushed
// rosters open silently - that is the background fleet Stage 1's roster polling reads.
//
// Environment:
//   GATEWAY_URL            REQUIRED. Refused unless local or explicitly named (never production).
//   KEYS_FILE              REQUIRED. The LoadRig's directors.json.
//   DIRECTORS              How many Directors to simulate (default: all keys in the file).
//   SESSIONS_PER_DIRECTOR  Sessions in each Director's snapshot (default 8, the plan's assumption).
//   EVENTS_PER_SEC         Target TOTAL PushDelta rate across all connections (default 0 = hold only).
//   DURATION_SECONDS       Stop after this long (default 0 = run until Ctrl+C).
//   CONNECT_PER_SEC        Connection open rate (default 50/s, so negotiate is a ramp, not a stampede).
//   METRICS_FILE           Optional JSONL output; one line per report interval.
//   METRICS_KEY            Optional viewer device key; when set, /diag/loadmetrics is scraped into the report.
//   REPORT_SECONDS         Report interval (default 5).

var gatewayUrl = Environment.GetEnvironmentVariable("GATEWAY_URL")
    ?? throw new InvalidOperationException("GATEWAY_URL is required (e.g. http://127.0.0.1:7891).");
LoadTargetGuard.AssertUrlAllowed(gatewayUrl);
gatewayUrl = gatewayUrl.TrimEnd('/');

var keysFile = Environment.GetEnvironmentVariable("KEYS_FILE")
    ?? throw new InvalidOperationException("KEYS_FILE is required (the LoadRig's directors.json).");
var allKeys = JsonSerializer.Deserialize<List<DirectorKey>>(File.ReadAllText(keysFile),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException($"KEYS_FILE {keysFile} parsed to null.");
if (allKeys.Count == 0)
    throw new InvalidOperationException($"KEYS_FILE {keysFile} holds no director keys.");

var directorCount = ReadInt("DIRECTORS", allKeys.Count);
if (directorCount > allKeys.Count)
    throw new InvalidOperationException($"DIRECTORS={directorCount} but KEYS_FILE holds only {allKeys.Count} keys. Seed a bigger rig (LOADTEST_TENANTS / LOADTEST_DIRECTORS_PER_TENANT).");
var sessionsPerDirector = ReadInt("SESSIONS_PER_DIRECTOR", 8);
var eventsPerSecond = ReadInt("EVENTS_PER_SEC", 0);
var durationSeconds = ReadInt("DURATION_SECONDS", 0);
var connectPerSecond = ReadInt("CONNECT_PER_SEC", 50);
var reportSeconds = ReadInt("REPORT_SECONDS", 5);
var metricsFile = Environment.GetEnvironmentVariable("METRICS_FILE");
var metricsKey = Environment.GetEnvironmentVariable("METRICS_KEY");

Console.WriteLine($"[DirectorSim] target={gatewayUrl} directors={directorCount} sessionsPerDirector={sessionsPerDirector} eventsPerSec={eventsPerSecond} duration={(durationSeconds == 0 ? "until Ctrl+C" : durationSeconds + "s")}");

var stopRequested = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopRequested.Cancel(); };

// ---- Open the connections at the configured ramp rate. -------------------------------------------
var sims = new List<SimDirector>(directorCount);
long connectFailures = 0;
var connectStart = DateTime.UtcNow;
for (var i = 0; i < directorCount && !stopRequested.IsCancellationRequested; i++)
{
    var sim = new SimDirector(gatewayUrl, allKeys[i], sessionsPerDirector);
    try
    {
        await sim.ConnectAndPushSnapshotAsync(stopRequested.Token);
        sims.Add(sim);
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Interlocked.Increment(ref connectFailures);
        Console.Error.WriteLine($"[DirectorSim] connect FAILED for {allKeys[i].DirectorId}: {ex.Message}");
    }

    // Ramp: no more than CONNECT_PER_SEC opens per second.
    var expectedElapsed = TimeSpan.FromSeconds((double)(i + 1) / connectPerSecond);
    var actualElapsed = DateTime.UtcNow - connectStart;
    if (expectedElapsed > actualElapsed)
        try { await Task.Delay(expectedElapsed - actualElapsed, stopRequested.Token); }
        catch (OperationCanceledException) { break; }

    if ((i + 1) % 250 == 0)
        Console.WriteLine($"[DirectorSim] {i + 1}/{directorCount} connected ({connectFailures} failures) in {(DateTime.UtcNow - connectStart).TotalSeconds:F0}s");
}
Console.WriteLine($"[DirectorSim] CONNECTED {sims.Count}/{directorCount} directors ({connectFailures} failures) in {(DateTime.UtcNow - connectStart).TotalSeconds:F0}s; " +
                  $"{sims.Count * sessionsPerDirector} sessions pushed");

if (sims.Count == 0)
{
    Console.Error.WriteLine("[DirectorSim] no connection succeeded; nothing to do.");
    return 1;
}

// ---- Report loop + delta stream. ------------------------------------------------------------------
long deltasOk = 0, deltasFailed = 0;
var runUntil = durationSeconds > 0 ? DateTime.UtcNow.AddSeconds(durationSeconds) : DateTime.MaxValue;
using var httpForMetrics = new HttpClient { BaseAddress = new Uri(gatewayUrl + "/") };
if (!string.IsNullOrEmpty(metricsKey))
    httpForMetrics.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", metricsKey);

var reporter = Task.Run(async () =>
{
    long lastOk = 0, lastFailed = 0;
    while (!stopRequested.IsCancellationRequested && DateTime.UtcNow < runUntil)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(reportSeconds), stopRequested.Token); }
        catch (OperationCanceledException) { break; }
        var ok = Interlocked.Read(ref deltasOk);
        var failed = Interlocked.Read(ref deltasFailed);
        var okRate = (ok - lastOk) / (double)reportSeconds;
        var connected = sims.Count(s => s.IsConnected);
        Console.WriteLine($"[DirectorSim] connected={connected}/{sims.Count} deltasOk={ok} (+{okRate:F0}/s) deltasFailed={failed} (+{failed - lastFailed})");

        if (!string.IsNullOrEmpty(metricsFile))
        {
            object? gatewayMetrics = null;
            if (!string.IsNullOrEmpty(metricsKey))
                try
                {
                    var raw = await httpForMetrics.GetStringAsync("diag/loadmetrics", stopRequested.Token);
                    gatewayMetrics = JsonSerializer.Deserialize<JsonElement>(raw);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown cancelled an in-flight scrape - end the reporter, do not crash the run
                    // (a TaskCanceledException here took the whole first Stage 2 run's summary with it).
                    break;
                }
                catch (Exception ex)
                {
                    gatewayMetrics = new { scrapeError = ex.Message };
                }
            var line = JsonSerializer.Serialize(new
            {
                atUtc = DateTime.UtcNow,
                connected,
                total = sims.Count,
                deltasOk = ok,
                deltasFailed = failed,
                achievedPerSecond = okRate,
                gateway = gatewayMetrics,
            });
            File.AppendAllText(metricsFile, line + Environment.NewLine);
        }
        lastOk = ok; lastFailed = failed;
    }
});

if (eventsPerSecond > 0)
{
    // The delta stream: a token-bucket paced loop distributing PushDelta round-robin across all
    // connections. InvokeAsync (not fire-and-forget Send) so a server that stops keeping up shows as
    // rising in-flight here, bounded by the semaphore, instead of unbounded queueing hiding the ceiling.
    var inFlight = new SemaphoreSlim(2000);
    var scheduleStart = DateTime.UtcNow;
    long scheduled = 0;
    var simIndex = 0;
    while (!stopRequested.IsCancellationRequested && DateTime.UtcNow < runUntil)
    {
        var due = (long)((DateTime.UtcNow - scheduleStart).TotalSeconds * eventsPerSecond);
        if (scheduled >= due)
        {
            try { await Task.Delay(10, stopRequested.Token); } catch (OperationCanceledException) { break; }
            continue;
        }
        while (scheduled < due && !stopRequested.IsCancellationRequested)
        {
            var sim = sims[simIndex];
            simIndex = (simIndex + 1) % sims.Count;
            scheduled++;
            try { await inFlight.WaitAsync(stopRequested.Token); } catch (OperationCanceledException) { break; }
            _ = sim.SendOneDeltaAsync().ContinueWith(t =>
            {
                inFlight.Release();
                if (t.IsCompletedSuccessfully && t.Result) Interlocked.Increment(ref deltasOk);
                else Interlocked.Increment(ref deltasFailed);
            }, TaskScheduler.Default);
        }
    }
}
else
{
    // Hold mode: keep connections and their pushed rosters open (the Stage 1 background fleet).
    try { await Task.Delay(durationSeconds > 0 ? TimeSpan.FromSeconds(durationSeconds) : Timeout.InfiniteTimeSpan, stopRequested.Token); }
    catch (OperationCanceledException) { }
}

stopRequested.Cancel();
await reporter;

Console.WriteLine("[DirectorSim] closing connections...");
await Parallel.ForEachAsync(sims, new ParallelOptions { MaxDegreeOfParallelism = 64 },
    async (sim, _) => await sim.DisposeAsync());
Console.WriteLine($"SIM DONE connected={sims.Count} deltasOk={Interlocked.Read(ref deltasOk)} deltasFailed={Interlocked.Read(ref deltasFailed)} connectFailures={connectFailures}");
return 0;

static int ReadInt(string variable, int fallback)
{
    var raw = Environment.GetEnvironmentVariable(variable);
    if (string.IsNullOrWhiteSpace(raw)) return fallback;
    if (!int.TryParse(raw, out var value) || value < 0)
        throw new InvalidOperationException($"{variable} must be a non-negative integer, got '{raw}'.");
    return value;
}

internal sealed record DirectorKey(string Tenant, string DirectorId, string MachineName, string DeviceKey);

/// <summary>
/// One simulated Director: one HubConnection, one session set, one strictly increasing sequence. The
/// sequence discipline is the whole trick: PushedSessionStore drops any push whose sequence is not
/// greater than the last accepted one for the active connection, and resets its baseline when a NEW
/// connection re-Hellos - so on reconnect this sim re-Hellos and re-pushes a full snapshot with its
/// next sequence, exactly as a real Director does.
/// </summary>
internal sealed class SimDirector : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly SessionDto[] _sessions;
    private readonly DirectorKey _key;
    // ONE send at a time per connection. Sequence assignment and the send must be one atomic step:
    // two concurrent sends on the same connection could otherwise reach the wire in the opposite
    // order to their sequences, and PushedSessionStore would drop the lower one as stale - the
    // simulator would be load-testing the reject path and counting it as success (review finding).
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private long _sequence;
    private int _deltaIndex;

    public SimDirector(string gatewayUrl, DirectorKey key, int sessionsPerDirector)
    {
        _key = key;
        _connection = new HubConnectionBuilder()
            .WithUrl($"{gatewayUrl}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(key.DeviceKey))
            .WithAutomaticReconnect()
            .Build();
        _sessions = Enumerable.Range(1, sessionsPerDirector).Select(n => BuildSession(key, n)).ToArray();
        // On automatic reconnect the server sees a NEW connection id, so the store expects a fresh Hello
        // and treats the cache as stale until this sim pushes again: re-Hello + full snapshot, next sequence.
        _connection.Reconnected += async _ =>
        {
            await _sendGate.WaitAsync();
            try
            {
                await _connection.InvokeAsync("Hello", BuildHello());
                await _connection.InvokeAsync("PushSnapshot", Interlocked.Increment(ref _sequence), _sessions);
            }
            finally
            {
                _sendGate.Release();
            }
        };
    }

    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    public async Task ConnectAndPushSnapshotAsync(CancellationToken cancellation)
    {
        await _connection.StartAsync(cancellation);
        await _connection.InvokeAsync("Hello", BuildHello(), cancellation);
        await _connection.InvokeAsync("PushSnapshot", Interlocked.Increment(ref _sequence), _sessions, cancellation);
    }

    /// <summary>Mutate one session (round-robin) and push it as a delta, serialized with every other
    /// send on this connection so sequences reach the wire in order. Returns false on failure.</summary>
    public async Task<bool> SendOneDeltaAsync()
    {
        if (_connection.State != HubConnectionState.Connected)
            return false;
        await _sendGate.WaitAsync();
        try
        {
            var session = _sessions[_deltaIndex = (_deltaIndex + 1) % _sessions.Length];
            var now = DateTime.UtcNow;
            session.LastActivityAt = now;
            session.ActivityState = session.ActivityState == "working" ? "waiting" : "working";
            session.StatusColor = session.ActivityState == "working" ? "blue" : "green";
            await _connection.InvokeAsync("PushDelta", Interlocked.Increment(ref _sequence), session);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private DirectorStreamHello BuildHello() => new()
    {
        DirectorId = _key.DirectorId,
        Version = "loadtest-sim",
        MachineName = _key.MachineName,
        User = "loadtest",
        Pid = Environment.ProcessId,
        StartedAt = DateTime.UtcNow,
    };

    private static SessionDto BuildSession(DirectorKey key, int number) => new()
    {
        SessionId = $"{key.DirectorId}-sess-{number:D2}",
        DirectorId = key.DirectorId,
        Agent = "claude",
        RepoPath = $"D:\\repos\\loadtest\\synthetic-{number:D2}",
        RepoName = $"synthetic-{number:D2}",
        Status = "running",
        ActivityState = "working",
        CreatedAt = DateTime.UtcNow,
        Name = $"loadtest session {number:D2} of {key.DirectorId}",
        MachineName = key.MachineName,
        User = "loadtest",
        StatusColor = "green",
        LastActivityAt = DateTime.UtcNow,
        BackendType = "conpty",
    };

    public async ValueTask DisposeAsync()
    {
        try { await _connection.StopAsync(); } catch { /* closing anyway */ }
        await _connection.DisposeAsync();
    }
}
