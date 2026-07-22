using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof for the Lost Dictations mission, issue #1593: a dictation whose delivery attempt FAILS
/// must be re-baselined so the retry that follows is DELIVERED, not silently eaten by the moved-on guard.
///
/// THE DEFECT THIS PINS, as observed on 2026-07-15 (session 59d2e552 at 05:31):
///   1. The phone stamps a baseline when the clip is RECORDED. It never moves.
///   2. Delivery is attempted, the composer never echoes, and the attempt fails - but NOT before typing the
///      text and clearing it twice, writing thousands of bytes of OUR OWN noise into the session buffer.
///   3. The endpoint returns 502, which is retryable, so the phone correctly retries the same clip.
///   4. The moved-on guard compares the now-inflated buffer against the phone's original baseline, cannot
///      tell our own noise from other people's turns, and drops the user's words as stale - forever (the
///      drop writes a durable movedOn tombstone).
///
/// This drives the REAL endpoint over loopback HTTP: a real GatewayHost, the real
/// POST /dictation/{uploadId}/complete route, the real VoiceUploadStore on disk, the real moved-on guard, and
/// a REAL tunnel-connected Director (<see cref="FakeTunnelDirector"/>) whose prompt verb FAILS the first time
/// - growing its reported buffer exactly as the real failure did - and succeeds the second.
///
/// TUNNEL-BY-CONSTRUCTION: the Director is registered at a dead endpoint and its session arrives only via
/// PushSnapshot, so any delivery at all proves the Gateway rode the tunnel.
///
/// The one thing NOT real here is the speech-to-text provider: the delivery arm sits behind a successful
/// transcribe, and the hosted transcription URL is a compile-time constant with no local override, so the
/// provider is a stub returning fixed text. Everything the defect actually lives in - the guard, the record,
/// the failure arm, the retry - is the real thing.
/// </summary>
[Collection("DirectorRoot")]
public sealed class Issue1593FailedAttemptRebaselineTests : IAsyncLifetime
{
    private const string Token = "test-token-1593-rebaseline";
    private const string DirectorId = "dir-1593-rebaseline";
    private const string Transcript = "the words the user actually said";

    // The clip's record-time baseline, and the growth a FAILED attempt writes into the terminal by typing the
    // text and clearing it again. The real incident logged ~8,700 bytes of such noise; the exact figure does
    // not matter, only that it is far past the guard's 512-byte tolerance.
    private const long RecordTimeBaseline = 1_000;
    private const long FailedAttemptNoise = 8_700;

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-1593-inst-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultPath = Path.Combine(Path.GetTempPath(), "cc-1593-vault-" + Guid.NewGuid().ToString("N") + ".json");

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _director = null!;
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private long _pushSequence;

    public Issue1593FailedAttemptRebaselineTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-1593-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // The key must be present or the complete path bails before it ever transcribes, let alone delivers.
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_test_key");

        _gateway = new GatewayHost(port: AllocateFreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: _vaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true,
            dictationTranscription: StubTranscription());
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        _director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId);
        await PushInitialSessionAsync(RecordTimeBaseline);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _director.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* cleanup */ }
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* cleanup */ }
    }

    // ===== the fix =================================================================================

    [Fact]
    public async Task RetryAfterOurOwnFailedAttempt_IsDelivered_NotDroppedAsMovedOn()
    {
        var uploadId = await RegisterAndUploadAsync();

        // Attempt 1: the guard passes (the buffer is still at the record-time baseline), delivery is
        // attempted, and it FAILS - after growing the buffer with its own typing-and-clearing noise, exactly
        // as the real echo-verified submit failure did.
        var prompts = 0;
        _director.OnCommand(cmd =>
        {
            if (cmd.Verb != "prompt") return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
            if (Interlocked.Increment(ref prompts) == 1)
            {
                ReportBuffer(RecordTimeBaseline + FailedAttemptNoise);
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "composer never echoed the text");
            }
            return FakeTunnelDirector.Ok(new PromptResponse());
        });

        var first = await CompleteAsync(uploadId);
        Assert.Equal(HttpStatusCode.BadGateway, first.status); // 502 - retryable, so the phone retries

        // Attempt 2: the SAME clip, with the SAME record-time baseline the phone has always sent (the phone
        // needs no change for this fix). Today the guard reads our own 8,700 bytes of noise as "other turns
        // happened" and drops the words. It must deliver.
        var second = await CompleteAsync(uploadId);

        Assert.Equal(HttpStatusCode.OK, second.status);
        Assert.True(second.body.GetProperty("submitted").GetBoolean(),
            "the retry of a delivery WE failed must inject the user's words, not be dropped as stale");
        Assert.False(second.body.GetProperty("movedOn").GetBoolean());
        Assert.Equal(2, prompts); // the retry really did reach the Director's prompt verb

        // The durable record is a DELIVERED tombstone that says submitted - the words landed for good.
        var record = Store().ReadRecord(uploadId)!;
        Assert.Equal(DictationDeliveryState.Delivered, record.State);
        Assert.True(record.Submitted);
        Assert.False(record.MovedOn);
    }

    [Fact]
    public async Task TheReBaselineIsPersistedOnTheServerRecord_SoAPhoneThatMissedThe502StillRetriesHonestly()
    {
        // Ruling 2: the re-baseline lives on the SERVER's durable record, not in the phone's request. A phone
        // whose 502 response never arrived does not know an attempt happened at all - it just re-registers the
        // upload id and completes again with its original baseline. That path must still be judged honestly.
        var uploadId = await RegisterAndUploadAsync();

        var prompts = 0;
        _director.OnCommand(cmd =>
        {
            if (cmd.Verb != "prompt") return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
            if (Interlocked.Increment(ref prompts) == 1)
            {
                ReportBuffer(RecordTimeBaseline + FailedAttemptNoise);
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "composer never echoed the text");
            }
            return FakeTunnelDirector.Ok(new PromptResponse());
        });

        Assert.Equal(HttpStatusCode.BadGateway, (await CompleteAsync(uploadId)).status);

        // The honest baseline is on disk, on the server's record, and it accounts for the noise.
        var afterFailure = Store().ReadRecord(uploadId)!;
        Assert.Equal(DictationDeliveryState.Pending, afterFailure.State); // still pending - the clip is alive
        Assert.NotNull(afterFailure.RebaselineBufferBytes);
        Assert.Equal(RecordTimeBaseline + FailedAttemptNoise, afterFailure.RebaselineBufferBytes!.Value);

        // The phone re-REGISTERS (it never saw the 502) and retries. The re-register must not wipe the value.
        await RegisterAsync(uploadId);
        Assert.Equal(RecordTimeBaseline + FailedAttemptNoise, Store().ReadRecord(uploadId)!.RebaselineBufferBytes);

        var retry = await CompleteAsync(uploadId);
        Assert.Equal(HttpStatusCode.OK, retry.status);
        Assert.True(retry.body.GetProperty("submitted").GetBoolean());
    }

    // ===== the control: a GENUINE move-on must still drop ===========================================

    [Fact]
    public async Task GenuineMoveOn_BeforeAnyDeliveryAttempt_IsStillDropped()
    {
        // The control that stops the fix from becoming "never drop anything". Here the buffer grew BEFORE any
        // delivery attempt of ours - nothing of ours is in that growth, so it is somebody else's turns and the
        // session really has moved on. No attempt has failed, so there is no re-baseline, and the guard must
        // drop exactly as it always did.
        var uploadId = await RegisterAndUploadAsync();
        ReportBuffer(RecordTimeBaseline + FailedAttemptNoise);

        var prompts = 0;
        _director.OnCommand(cmd =>
        {
            if (cmd.Verb == "prompt") Interlocked.Increment(ref prompts);
            return FakeTunnelDirector.Ok(new PromptResponse());
        });

        var result = await CompleteAsync(uploadId);

        Assert.Equal(HttpStatusCode.OK, result.status);
        Assert.False(result.body.GetProperty("submitted").GetBoolean());
        Assert.True(result.body.GetProperty("movedOn").GetBoolean(), "a genuine move-on must still drop");
        Assert.Equal(0, prompts); // it never reached the session at all
        Assert.Null(Store().ReadRecord(uploadId)!.RebaselineBufferBytes); // nothing of ours ever failed
    }

    [Fact]
    public async Task GrowthAfterOurFailedAttempt_BeyondTheReBaseline_IsStillDropped()
    {
        // The sharper control: our own failed attempt is forgiven, but growth ON TOP of it is not. The
        // re-baseline moves the bar to account for OUR noise only - a real turn landing after that is still a
        // move-on and the clip is still stale.
        var uploadId = await RegisterAndUploadAsync();

        var prompts = 0;
        _director.OnCommand(cmd =>
        {
            if (cmd.Verb != "prompt") return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
            if (Interlocked.Increment(ref prompts) == 1)
            {
                ReportBuffer(RecordTimeBaseline + FailedAttemptNoise);
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "composer never echoed the text");
            }
            return FakeTunnelDirector.Ok(new PromptResponse());
        });

        Assert.Equal(HttpStatusCode.BadGateway, (await CompleteAsync(uploadId)).status);

        // Somebody else's turn lands after our failure, well past the re-baseline.
        ReportBuffer(RecordTimeBaseline + FailedAttemptNoise + 50_000);

        var retry = await CompleteAsync(uploadId);
        Assert.Equal(HttpStatusCode.OK, retry.status);
        Assert.False(retry.body.GetProperty("submitted").GetBoolean());
        Assert.True(retry.body.GetProperty("movedOn").GetBoolean(),
            "growth beyond our own failed attempt is a real move-on and must still drop");
        Assert.Equal(1, prompts); // the retry never reached the session
    }

    // ===== helpers =================================================================================

    private static VoiceUploadStore Store() => new(CcStorage.DictationUploads(), TenantId.Local);

    private SessionDto SessionAt(long bufferBytes) => new()
    {
        SessionId = _sessionId,
        Name = "dictation target",
        Status = "Running",
        ActivityState = "Idle",
        TotalBufferBytes = bufferBytes,
    };

    /// <summary>
    /// The session's FIRST appearance, pushed through the REAL hub the way a Director reports its state. This
    /// is what puts the session in the push store at all, so the Gateway can locate it and its owner.
    /// </summary>
    private async Task PushInitialSessionAsync(long bufferBytes)
    {
        await _director.PushSnapshotAsync(SessionAt(bufferBytes));
        _pushSequence = 1; // FakeTunnelDirector's first push is sequence 1; later writes must climb from there.
    }

    /// <summary>
    /// Report a new buffer position for the session, as a Director's push stream does when its terminal grows.
    ///
    /// This writes the push store directly rather than invoking PushSnapshot over the hub, for one reason that
    /// the tests genuinely need: the interesting growth happens from INSIDE the Director's command handler,
    /// while the Gateway is still awaiting the prompt result - which is exactly when the real failure writes
    /// its noise, and what the re-baseline must see when it re-reads. Calling back into the hub from within a
    /// hub client callback would leave the connection's message loop waiting on itself. The store IS what the
    /// Gateway re-reads and this lands the same state a real push lands, so the seam under test is untouched;
    /// only the transport of these later snapshots is short-cut. One sequence counter is shared with the hub
    /// push above, because the store rejects an out-of-order sequence and would otherwise silently ignore it.
    /// </summary>
    private void ReportBuffer(long bufferBytes)
    {
        var connectionId = _gateway.PushedSessions.GetActiveConnectionId(TenantId.Local, DirectorId)!;
        var applied = _gateway.PushedSessions.ApplyDelta(
            TenantId.Local, DirectorId, connectionId, Interlocked.Increment(ref _pushSequence), SessionAt(bufferBytes));
        Assert.True(applied, "the test's own buffer report must actually land, or it proves nothing");
    }

    private async Task<string> RegisterAndUploadAsync()
    {
        var uploadId = Guid.NewGuid().ToString();
        await RegisterAsync(uploadId);
        // A real (if silent) clip: a short PCM WAV, under the split budget, so the pipeline sends it whole.
        var wav = PcmWav.Wrap(new byte[16_000 * 2], 16_000, 1, 16);
        using var content = new ByteArrayContent(wav);
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/dictation/{uploadId}/chunk/0") { Content = content };
        req.Headers.Add("X-Chunk-Sha256", Sha256Hex(wav));
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return uploadId;
    }

    private async Task RegisterAsync(string uploadId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dictation/upload")
        {
            Content = JsonContent.Create(new { sessionId = _sessionId, baselineBufferBytes = RecordTimeBaseline }),
        };
        req.Headers.Add("Idempotency-Key", uploadId);
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>Complete the clip exactly as the phone does on a resume: resumed, carrying the baseline it
    /// stamped when the clip was RECORDED - the same number on every retry, because the phone never moves it.</summary>
    private async Task<(HttpStatusCode status, JsonElement body)> CompleteAsync(string uploadId)
    {
        var resp = await _http.PostAsJsonAsync($"/dictation/{uploadId}/complete", new
        {
            sessionId = _sessionId,
            totalChunks = 1,
            mime = "audio/wav",
            ext = "wav",
            baselineBufferBytes = RecordTimeBaseline,
            resumed = true,
        });
        return (resp.StatusCode, await resp.Content.ReadFromJsonAsync<JsonElement>());
    }

    /// <summary>The real transcription owner over a stub provider socket, with its own local history + audio
    /// archive so it never writes into the real user's directories (the defaults are process-wide Shared
    /// instances whose paths are baked at type-init).</summary>
    private GatewayTranscriptionService StubTranscription() => new(
        new KeyVault(_vaultPath),
        http: new HttpClient(new TranscriptStub()),
        history: new TranscriptionHistoryLog(Path.Combine(_root, "transcription-history")),
        audioArchive: new TranscriptionAudioArchive(Path.Combine(_root, "audio")));

    /// <summary>Stands in for the hosted speech-to-text provider: always returns the same transcript.</summary>
    private sealed class TranscriptStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"text\":\"{Transcript}\"}}", Encoding.UTF8, "application/json"),
            });
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
