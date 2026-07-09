using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway-side enforced session lock (issue #1188): a session is LOCKED for human input at the Gateway
/// front door exactly while a PENDING dictation record exists for it. The lock is a pure projection of the
/// durable PENDING marker - it never auto-releases and it survives a Gateway restart (the marker + session id
/// are on disk). These tests drive a real <see cref="GatewayHost"/> over the HTTP front door with no Director
/// present: a locked session is rejected BEFORE the session lookup, so the 423 is observable without one.
/// </summary>
public sealed class DictationSessionLockTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";
    private const string LockMessage =
        "This session is receiving a dictation. You cannot send input until it arrives or is cancelled.";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _originalRoot;

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-lock-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-lock-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _originalRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);
        _gateway = NewGateway();
        await _gateway.StartAsync();
        _http = NewClient(_gateway.Port);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _originalRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
    }

    [Fact]
    public async Task RegisteringADictation_LocksTheSession_PromptRejectedWith423AndTheMessage()
    {
        var sessionId = Guid.NewGuid().ToString();

        // Register a dictation for the session through the real front door: this writes the PENDING marker.
        using var reg = new HttpRequestMessage(HttpMethod.Post, "/dictation/upload")
        {
            Content = JsonContent.Create(new { sessionId, baselineBufferBytes = 0 }),
        };
        reg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var regResp = await _http.SendAsync(reg);
        Assert.Equal(HttpStatusCode.OK, regResp.StatusCode);

        var (status, body) = await PromptAsync(sessionId);

        Assert.Equal(StatusCodes.Status423Locked, (int)status);
        Assert.Equal(LockMessage, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeliveredClearsTheLock()
    {
        await AssertLockClearsWhen(id => Store().MarkDelivered(id, submitted: true, movedOn: false, transcript: "hi"));
    }

    [Fact]
    public async Task AbandonedClearsTheLock()
    {
        await AssertLockClearsWhen(id => Store().MarkAbandoned(id, "cancelled"));
    }

    [Fact]
    public async Task FailedClearsTheLock()
    {
        await AssertLockClearsWhen(id => Store().MarkFailed(id, "audio_too_large"));
    }

    [Fact]
    public async Task LockSurvivesAFreshGatewayInstance()
    {
        var sessionId = Guid.NewGuid().ToString();
        Store().MarkPending(Guid.NewGuid().ToString(), sessionId);

        // A fresh GatewayHost over the SAME on-disk root recomputes the lock from disk and still rejects.
        var restarted = NewGateway();
        await restarted.StartAsync();
        try
        {
            using var http2 = NewClient(restarted.Port);
            var (status, body) = await PromptAsync(sessionId, http2);
            Assert.Equal(StatusCodes.Status423Locked, (int)status);
            Assert.Equal(LockMessage, body.GetProperty("error").GetString());
        }
        finally
        {
            await restarted.StopAsync();
        }
    }

    [Fact]
    public async Task UploadImageAndRecap_AreAlsoRejectedWhenLocked()
    {
        var sessionId = Guid.NewGuid().ToString();
        Store().MarkPending(Guid.NewGuid().ToString(), sessionId);

        var image = await _http.PostAsync($"/sessions/{sessionId}/upload-image", content: null);
        Assert.Equal(StatusCodes.Status423Locked, (int)image.StatusCode);

        var recap = await _http.PostAsync($"/sessions/{sessionId}/recap", content: null);
        Assert.Equal(StatusCodes.Status423Locked, (int)recap.StatusCode);
    }

    // Seed a PENDING marker for a fresh session (locked), confirm /prompt is 423, then apply the terminal
    // transition and confirm the lock is gone (/prompt is no longer 423 - here 404, since there is no
    // Director to locate, which is exactly the "proceeded past the lock" signal).
    private async Task AssertLockClearsWhen(Action<string> transition)
    {
        var sessionId = Guid.NewGuid().ToString();
        var uploadId = Guid.NewGuid().ToString();
        Store().MarkPending(uploadId, sessionId);

        Assert.Equal(StatusCodes.Status423Locked, (int)(await PromptAsync(sessionId)).status);

        transition(uploadId);

        var (status, _) = await PromptAsync(sessionId);
        Assert.NotEqual(StatusCodes.Status423Locked, (int)status);
        Assert.Equal(HttpStatusCode.NotFound, status); // lock cleared -> proceeded to the (absent) session lookup
    }

    // ===== helpers =================================================================================

    private static VoiceUploadStore Store() => new(CcStorage.DictationUploads());

    private async Task<(HttpStatusCode status, JsonElement body)> PromptAsync(string sessionId, HttpClient? http = null)
    {
        var resp = await (http ?? _http).PostAsJsonAsync($"/sessions/{sessionId}/prompt", new { text = "hello" });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (resp.StatusCode, body);
    }

    private GatewayHost NewGateway() => new(
        port: AllocateFreePort(), token: GatewayToken, authEnabled: false,
        instancesDirectory: _instancesDir,
        workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));

    private static HttpClient NewClient(int port)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
        return http;
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
