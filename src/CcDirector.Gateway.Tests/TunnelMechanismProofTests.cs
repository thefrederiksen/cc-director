using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2: the up-stream MECHANISM proof. Boots a REAL <see cref="GatewayHost"/> with
/// stream mode ON, dials the REAL DirectorHub with a REAL SignalR client, and drives the browser-facing legs
/// (roster, a session read, prompt, terminal, file) end to end over the tunnel. The Director side runs the REAL
/// <see cref="DirectorUpStreamHandler"/> and the REAL producers, and the Gateway side runs the REAL
/// <see cref="GatewayStreamRegistry"/> + the REAL sinks - nothing about the up-stream is stubbed.
///
/// TUNNEL-BY-CONSTRUCTION: the Director is registered with a DELIBERATELY UNREACHABLE control endpoint, so an
/// HTTP dial cannot succeed - anything that works can ONLY have ridden the tunnel. This is a stronger proof than
/// reading log lines.
///
/// It exercises the Architect's four invariants: backpressure (ruling 1 - the producer blocks on a stalled sink
/// over the real SignalR StreamBufferCapacity), no-monopoly concurrency (ruling 2 - a terminal stream and a
/// large file read progress together), teardown (ruling 3 - a browser disconnect fires close-stream and stops
/// the Director producer), and error parity (a missing file / session returns 404 over the tunnel, as the HTTP
/// dial did).
/// </summary>
[Collection("DirectorRoot")]
public sealed class TunnelMechanismProofTests : IAsyncLifetime
{
    private const string Token = "test-token-mechanism-proof";
    private const string DirectorId = "dir-mech";
    private const string MissingSid = "00000000-0000-0000-0000-0000000000ff";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-mech-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private SessionManager _sm = null!;
    private DirectorUpStreamHandler _handler = null!;
    private HubConnection _conn = null!;
    private Session _session = null!;
    private string _sid = "";
    private int _closeStreamCount;

    public TunnelMechanismProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-mech-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // A REAL session with real terminal bytes, driven by a test backend (no real process).
        _sm = new SessionManager(new AgentOptions());
        var backend = new ExecuteActionTestBackend();
        _session = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        backend.Write(Encoding.UTF8.GetBytes("hello from the real terminal producer\r\n"));
        _sid = _session.Id.ToString();

        // The Director registers UNREACHABLE, so a working op proves it rode the tunnel, never an HTTP dial.
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59919/", // nothing listens here
            MachineName = "mech-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        // The REAL up-stream handler streams frames UP over the REAL SignalR connection.
        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol() // speak the same binary tunnel protocol the real Director does
            .Build();
        _handler = new DirectorUpStreamHandler(_sm, (streamId, frames) => _conn.SendAsync("StreamUp", streamId, frames));
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", Dispatch);
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
        // Push the session so the Gateway resolves this Director as its owner (TryLocate) and targets the tunnel.
        await _conn.InvokeAsync("PushSnapshot", 1L, new[]
        {
            new SessionDto { SessionId = _sid, ActivityState = "WaitingForInput" },
            new SessionDto { SessionId = MissingSid, ActivityState = "WaitingForInput" }, // known to the roster, absent on the Director
        });
    }

    public async Task DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _sm.Dispose();
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    /// <summary>How many reads for the missing session actually REACHED the Director stub. Without this the
    /// 404 test asserts a status that a Gateway-side check could produce on its own.</summary>
    private int _missingReadCount;

    /// <summary>A message no Gateway-side check would ever produce, so finding it in the response body is
    /// proof the DIRECTOR's refusal is what the caller received - not merely that a 404 came back.</summary>
    private const string MissingReadReason = "director-stub says: no such session here";

    private DirectorCommandResult RecordMissingRead()
    {
        Interlocked.Increment(ref _missingReadCount);
        return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, MissingReadReason);
    }

    // The Director-side Command handler: stream verbs run the REAL handler; the rest return typed results.
    private DirectorCommandResult Dispatch(DirectorCommand cmd)
    {
        if (cmd.Verb == "close-stream")
            Interlocked.Increment(ref _closeStreamCount);
        if (DirectorUpStreamHandler.IsStreamVerb(cmd.Verb))
            return _handler.Handle(cmd);

        return cmd.Verb switch
        {
            // The representative READ. This was the turns read until the turn-push mission removed it; the
            // claims here were never about turns, they are about the tunnel carrying a read and its error
            // status, so they now travel a read that still exists.
            "usage" => cmd.SessionId == MissingSid
                ? RecordMissingRead()
                : DirectorCommandResult.Success(JsonSerializer.Serialize(new { sessionId = cmd.SessionId, status = "ok", widgets = Array.Empty<object>() })),
            "prompt" => DirectorCommandResult.Success(JsonSerializer.Serialize(new { accepted = true })),
            _ => DirectorCommandResult.Success(),
        };
    }

    // ---------------------------------------------------------------- representative (tunnel-only) ----

    [Fact]
    public async Task Roster_isServedFromTheTunnelPushCache_notAnHttpPull()
    {
        var (ids, errorDirectors) = await GetSessionsAsync();
        Assert.Contains(_sid, ids);
        Assert.DoesNotContain(DirectorId, errorDirectors); // never pulled the unreachable endpoint
    }

    [Fact]
    public async Task A_read_ridesTheTunnel()
    {
        var resp = await _http.GetAsync($"sessions/{_sid}/usage");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // an HTTP dial to the unreachable Director would have failed
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("ok", node?["status"]?.GetValue<string>());
    }

    [Fact]
    public async Task Prompt_write_ridesTheTunnel()
    {
        var resp = await _http.PostAsJsonAsync($"sessions/{_sid}/prompt", new { text = "hello" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ------------------------------------------------------------------------------- error parity ----

    [Fact]
    public async Task A_read_forAMissingSession_is404_thatCameFromTheDirector()
    {
        // TWO things, and the status alone is not one of them.
        //
        // Deliberately NOT the turns path: that path is unmapped now, so it would answer 404 because no verb
        // claims it - the same status this test asserts, arrived at without the tunnel being involved at all.
        //
        // And deliberately not the status alone even on a live path: a Gateway-side session check that
        // answered 404 before sending anything upstream would satisfy it while proving nothing about error
        // parity over the tunnel (found in review).
        //
        // So two things are asserted, and the second is the one that matters. The stub records that the
        // request REACHED it - contact. And the response body carries the stub's own distinctive reason -
        // causation, because a Gateway that called the Director and then answered 404 on its own account
        // could not produce that string. Contact alone would still allow "asked, ignored the answer".
        var before = Volatile.Read(ref _missingReadCount);

        var resp = await _http.GetAsync($"sessions/{MissingSid}/usage");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(before + 1, Volatile.Read(ref _missingReadCount));
        Assert.Contains(MissingReadReason, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReadFile_missingPath_is404_overTheTunnel()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-file-" + Guid.NewGuid().ToString("N"));
        var resp = await _http.GetAsync($"sessions/{_sid}/file?path={Uri.EscapeDataString(missing)}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ReadFile_outsideTheSessionWorkingDirectory_is400_overTheTunnel()
    {
        // Tenant-boundary hardening (CR-4): the session's working directory is the temp root, so a path that
        // resolves to its PARENT is outside the allowed root and must be refused end to end over the tunnel.
        var outside = Path.Combine(Path.GetTempPath(), "..", "cc-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        var resp = await _http.GetAsync($"sessions/{_sid}/file?path={Uri.EscapeDataString(outside)}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("outside the session's working directory", await resp.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------- real up-stream producers over the wire ----

    [Fact]
    public async Task Terminal_streamsRealProducerFramesOverTheTunnel()
    {
        using var ws = await OpenTerminalAsync(_sid);
        // First a size text frame, then the snapshot/tail binary carrying the buffer bytes.
        var size = await ReceiveTextAsync(ws);
        Assert.Equal("size", JsonNode.Parse(size)?["type"]?.GetValue<string>());
        var bin = await ReceiveBinaryAsync(ws);
        Assert.Contains("hello from the real terminal producer", Encoding.UTF8.GetString(bin));
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task File_streamsWithCorrectContentLength_overTheTunnel()
    {
        var (path, content) = await WriteTempFileAsync((DirectorStreamLimits.MaxBinaryFrameBytes * 3) + 777); // many full-cap chunks
        try
        {
            var resp = await _http.GetAsync($"sessions/{_sid}/file?path={Uri.EscapeDataString(path)}");
            var diag = resp.IsSuccessStatusCode ? "" : await resp.Content.ReadAsStringAsync();
            Assert.True(resp.StatusCode == HttpStatusCode.OK, $"file GET -> {resp.StatusCode}: {diag}");
            Assert.Equal(content.Length, resp.Content.Headers.ContentLength); // TotalBytes from the open reply (ruling 4)
            var body = await resp.Content.ReadAsByteArrayAsync();
            Assert.Equal(content, body); // reassembled byte-for-byte from the chunked up-stream
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    // ------------------------------------------------------------------- ruling 2: no-monopoly concurrency ----

    [Fact]
    public async Task TerminalAndLargeFile_concurrently_bothProgress()
    {
        var (path, content) = await WriteTempFileAsync(DirectorStreamLimits.MaxBinaryFrameBytes * 8);
        try
        {
            using var ws = await OpenTerminalAsync(_sid);
            var fileTask = _http.GetByteArrayAsync($"sessions/{_sid}/file?path={Uri.EscapeDataString(path)}");

            // The terminal makes progress (a size frame arrives) WHILE the large file is streaming on the same
            // shared tunnel connection - neither starves the other.
            var size = await ReceiveTextAsync(ws);
            Assert.Equal("size", JsonNode.Parse(size)?["type"]?.GetValue<string>());

            var fileBytes = await fileTask;
            Assert.Equal(content, fileBytes);
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    // ------------------------------------------------------------------------- ruling 3: teardown ----

    [Fact]
    public async Task BrowserDisconnect_firesCloseStream_andStopsTheDirectorProducer()
    {
        var ws = await OpenTerminalAsync(_sid);
        await ReceiveTextAsync(ws); // size - the producer is streaming

        // Browser goes away: the Gateway must send close-stream (ruling 3) and the Director producer must stop.
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        ws.Dispose();

        await WaitUntilAsync(() => Volatile.Read(ref _closeStreamCount) >= 1 && _handler.ActiveStreamCount == 0, TimeSpan.FromSeconds(5));
        Assert.True(Volatile.Read(ref _closeStreamCount) >= 1, "close-stream was not sent on browser disconnect");
        Assert.Equal(0, _handler.ActiveStreamCount); // producer stopped, nothing left streaming into a dead sink
    }

    // ------------------------------------------------------------- ruling 1: producer-side backpressure ----

    [Fact]
    public async Task Producer_blocksOnAStalledSink_overTheRealSignalRStreamBuffer()
    {
        // Register a REAL sink in the REAL registry that stalls every write, then stream an instrumented
        // producer UP over the REAL SignalR connection. Pull-then-forward + a bounded StreamBufferCapacity must
        // PIN the producer: with the sink held, the producer runs ahead only until the server channel, the
        // transport pipe, and the socket buffers fill, then it blocks on its yield - it must NOT run to
        // completion. Frames are at the cap so transport buffers cannot silently absorb the whole stream.
        //
        // The proof is that the producer's progress STOPS ramping and STAYS BELOW the total for as long as the
        // sink is held (bounded memory - the Architect's ruling 1), and then drains fully the instant the sink
        // releases.
        // (The exact pinning depth is buffer-dependent - on loopback it settles in the low tens because the OS
        // socket and pipe buffers dwarf the 4-deep server channel; over a real slow link it pins far tighter.)
        //
        // NOTE (Gateway Cleanup, MessagePack fix): SendAsync for a client-to-server stream completes as soon as
        // the invocation is DISPATCHED - the item pump then runs on a background task - so send.IsCompleted is
        // NOT the backpressure signal. The producer's own yield counter is. An overall CancellationToken makes
        // this FAIL FAST with a diagnostic instead of hanging if backpressure ever regresses, and the sink is
        // ALWAYS released in a finally so a failed assertion can never wedge teardown on the held sink.
        const int total = 100;
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var streamId = Guid.NewGuid().ToString("N");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new StallingSink(gate);
        var yielded = 0;

        // Issue #1923: the stream is owned by the tenant and Director this test's real hub connection is
        // bound to (self-host -> Local, Hello -> DirectorId), so the StreamUp below is the PERMITTED path.
        _gateway.StreamRegistry.Register(streamId, new StreamOwner(TenantId.Local, DirectorId), sink);

        async IAsyncEnumerable<DirectorStreamFrame> Produce([EnumeratorCancellation] CancellationToken ct = default)
        {
            var payload = new byte[DirectorStreamLimits.MaxBinaryFrameBytes];
            for (var i = 0; i < total; i++)
            {
                Interlocked.Increment(ref yielded);
                yield return new DirectorStreamFrame { StreamId = streamId, Kind = DirectorStreamFrameType.Binary, Data = payload };
            }
            yield return new DirectorStreamFrame { StreamId = streamId, Kind = DirectorStreamFrameType.Closed, Reason = "eof" };
        }

        var send = _conn.SendAsync("StreamUp", streamId, Produce(), testTimeout.Token);
        try
        {
            // POLL until the producer's progress STOPS advancing (buffers full -> pinned), rather than assuming
            // it pins within a fixed delay: the OS socket + pipe buffers fill at a machine-dependent rate, so a
            // fixed sample can catch the ramp mid-flight on a slow CI runner. Throughout the ramp the count MUST
            // stay below total - if backpressure were broken the producer would run to completion (caught here).
            var pinned = await PollUntilStableAsync(() => Volatile.Read(ref yielded), total, testTimeout.Token);

            // Now sample again after a further window, and assert what backpressure actually promises: the
            // producer cannot outrun a stalled sink WITHOUT BOUND, so however far it got it is still short of
            // total. It is deliberately NOT asserted that the count is EXACTLY unchanged over this window.
            // A producer under backpressure is not frozen - it advances in bursts, each time the socket and
            // pipe buffers drain enough to admit more - so "did not advance for 800 milliseconds" is a temporal
            // sample, not a permanent property. On a loaded runner a quiet stretch that long can fall in the
            // middle of the ramp, and the count then moves inside this window: 35 to 57, and 36 to 58, on the
            // shared continuous-integration runner, on commits that touched nothing under src at all. Widening
            // either window does not close that gap, it only lowers the probability, because any fixed quiet
            // period can be exceeded by a slower machine - the previous attempt here was exactly such a
            // widening. The bound below holds for as long as the sink is held, so it cannot flake, and it still
            // fails loudly the moment a regression lets the producer run away from a sink that is not draining.
            await Task.Delay(1000, testTimeout.Token);
            var afterWindow = Volatile.Read(ref yielded);
            Assert.True(afterWindow < total, $"producer ran to {afterWindow}/{total} with the sink stalled (it was at {pinned} a second earlier) - backpressure is broken");
            Assert.True(pinned < total, $"producer ran to {pinned}/{total} with the sink stalled - backpressure is broken");
            Assert.Equal(0, sink.CompletedWrites); // the first write is in-flight (held by the gate); none have completed
        }
        finally
        {
            gate.SetResult(); // ALWAYS release so a failed assertion cannot wedge teardown on the held sink
        }

        await send; // already completed at dispatch; harmless to await

        // With the sink released the whole stream drains: every frame is produced and every frame lands on the
        // sink (the 100 binary frames plus the trailing Closed frame = total + 1 writes).
        const int expectedWrites = total + 1;
        await WaitUntilAsync(() => Volatile.Read(ref yielded) == total && sink.CompletedWrites == expectedWrites, TimeSpan.FromSeconds(10));
        Assert.Equal(total, Volatile.Read(ref yielded));
        Assert.Equal(expectedWrites, sink.CompletedWrites); // 100 binary + 1 closed, delivered to the sink in order
    }

    // Poll `read` until it STOPS advancing (the same value for several consecutive samples = pinned), returning
    // that stable value. Deterministic where a fixed delay is not: the socket/pipe buffers fill at a
    // machine-dependent rate, so this waits for the ramp to finish instead of assuming it finished by some
    // instant. Throughout the ramp the value MUST stay below `total`; if it ever reaches `total` the producer
    // ran to completion and backpressure is broken - asserted here so the poll cannot mask a real regression.
    // Fails fast via the shared test timeout if it never stabilizes.
    private static async Task<int> PollUntilStableAsync(Func<int> read, int total, CancellationToken ct)
    {
        int last = -1, stable = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(200, ct);
            var cur = read();
            Assert.True(cur < total, $"producer ran to {cur}/{total} with the sink stalled - backpressure is broken");
            if (cur == last && cur > 0)
            {
                if (++stable >= 4) return cur; // ~800ms with no advance (and past the ramp) = pinned
            }
            else
            {
                stable = 0;
                last = cur;
            }
        }
        ct.ThrowIfCancellationRequested();
        return last; // unreachable: the line above always throws when cancelled
    }

    // A sink whose writes block on a gate, so a test can hold the Gateway's pull and observe backpressure. It
    // honors the cancellation token (as a real sink must on teardown) so a torn-down held stream cannot wedge,
    // and counts completed writes so a test can prove nothing drained while held and everything drained after.
    private sealed class StallingSink : IStreamSink
    {
        private readonly TaskCompletionSource _gate;
        private int _completedWrites;
        public StallingSink(TaskCompletionSource gate) => _gate = gate;
        public int CompletedWrites => Volatile.Read(ref _completedWrites);

        public async Task WriteFrameAsync(DirectorStreamFrame frame, CancellationToken cancellationToken)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            Interlocked.Increment(ref _completedWrites);
        }

        public Task CompleteAsync(string? reason) => Task.CompletedTask;
    }

    // -------------------------------------------------------------------------------------- helpers ----

    private async Task<ClientWebSocket> OpenTerminalAsync(string sid)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_gateway.Port}/sessions/{sid}/stream"), CancellationToken.None);
        return ws;
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws)
    {
        var (bytes, type) = await ReceiveAsync(ws);
        Assert.Equal(WebSocketMessageType.Text, type);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> ReceiveBinaryAsync(ClientWebSocket ws)
    {
        var (bytes, type) = await ReceiveAsync(ws);
        Assert.Equal(WebSocketMessageType.Binary, type);
        return bytes;
    }

    private static async Task<(byte[] bytes, WebSocketMessageType type)> ReceiveAsync(ClientWebSocket ws)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, timeout.Token);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (ms.ToArray(), result.MessageType);
    }

    private static async Task<(string path, byte[] content)> WriteTempFileAsync(int size)
    {
        var path = Path.Combine(Path.GetTempPath(), "ccd-mech-file-" + Guid.NewGuid().ToString("N") + ".bin");
        var content = new byte[size];
        for (var i = 0; i < size; i++) content[i] = (byte)(i % 251);
        await File.WriteAllBytesAsync(path, content);
        return (path, content);
    }

    private async Task<(List<string> ids, List<string> errorDirectors)> GetSessionsAsync()
    {
        var resp = await _http.GetAsync("sessions?envelope=true");
        resp.EnsureSuccessStatusCode();
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        var sessions = node?["sessions"]?.AsArray() ?? node?.AsArray() ?? new JsonArray();
        var ids = new List<string>();
        foreach (var s in sessions)
            if (s?["sessionId"]?.GetValue<string>() is { Length: > 0 } id) ids.Add(id);
        var errors = new List<string>();
        foreach (var e in node?["machineErrors"]?.AsArray() ?? new JsonArray())
            if (e?["directorId"]?.GetValue<string>() is { Length: > 0 } id) errors.Add(id);
        return (ids, errors);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var attempts = (int)(timeout.TotalMilliseconds / 15) + 1;
        for (var i = 0; i < attempts && !condition(); i++) await Task.Delay(15);
    }

}
