using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1848: the prompt log is TENANT-SCOPED on the hosted Gateway, proven over real HTTP with two accounts.
///
/// This is the CONTENT-disclosure surface. Before this, <c>GET /prompts</c> did not take an HttpContext at all -
/// so it could not resolve a request tenant even in principle - and it answered fleet-globally: one account
/// read every other account's full prompt TEXT. This drives the REAL mapped endpoints through the REAL auth
/// middleware, exactly as SessionServingReadIsolationTests does for the cockpit read path.
///
/// THE PRODUCTION LINES THESE TESTS PROTECT (each revert-provable - revert it, watch these go red):
///   - <c>PromptEndpoints.ResolveTenant</c>, and the <c>store.Read(tenant.Value, ...)</c> /
///     <c>store.Append(tenant.Value, ...)</c> calls in the two /prompts handlers. Revert the resolution to a
///     fixed TenantId.Local and both accounts share one partition, so "B never sees A's text" goes RED and the
///     403 deny collapses to a 200.
///   - <c>GatewayPromptLog.DirectoryFor</c> - the ONE helper every read and write path goes through, which is
///     what makes the partition structural rather than conventional. Return the root directory for every
///     tenant and the same assertion goes RED.
///
/// The assembly runs sequentially (TestParallelization), so toggling CC_GATEWAY_HOSTED here is safe; it is
/// reset in DisposeAsync.
/// </summary>
public sealed class PromptLogTenantIsolationTests : IAsyncLifetime
{
    private const string Token = "test-token";

    // The thing one account must never be able to read out of the other.
    private const string SecretTextA = "alpha-account-secret-prompt-text";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private string _keyA = "";
    private string _keyB = "";
    private string _keyUnbound = "";

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "cc-prompt-iso-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        // The prompt log is pinned into a throwaway directory; it otherwise defaults to the running user's real one.
        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _tempDir,
            workListsPath: Path.Combine(_tempDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_tempDir, "snooze", "snooze.json"),
            promptLogPath: Path.Combine(_tempDir, "prompt-log"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Two accounts: two device keys, each bound to its OWN tenant, plus one registered-but-unbound key.
        _keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        _keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", "tenant-alice");
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", "tenant-bob");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task One_account_never_reads_another_accounts_prompt_text()
    {
        var today = DateTime.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await PostPrompt(_keyA, SecretTextA, today)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostPrompt(_keyB, "bravo-account-prompt-text", today)).StatusCode);

        // The account that wrote it reads its own text back...
        var ownBody = await ReadPromptsBody(_keyA, today);
        Assert.Contains(SecretTextA, ownBody);

        // ...and the OTHER account can read neither the text nor the record. This is the whole defect: the
        // handler used to have no request context, so this body was every account's log.
        var otherBody = await ReadPromptsBody(_keyB, today);
        Assert.DoesNotContain(SecretTextA, otherBody);
        Assert.Contains("bravo-account-prompt-text", otherBody);

        using var doc = JsonDocument.Parse(otherBody);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Prompts_deny_a_device_key_with_no_bound_tenant()
    {
        // Deny-by-default, on BOTH verbs: an authenticated but tenant-unbound key never falls back to the
        // Local partition - not to read it, and not to write into it.
        Assert.Equal(HttpStatusCode.Forbidden, (await ReadPrompts(_keyUnbound, DateTime.UtcNow)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostPrompt(_keyUnbound, "should never land", DateTime.UtcNow)).StatusCode);
    }

    private Task<HttpResponseMessage> Get(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> ReadPrompts(string deviceKey, DateTime day)
        => Get($"prompts?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}", deviceKey);

    private async Task<string> ReadPromptsBody(string deviceKey, DateTime day)
    {
        var resp = await ReadPrompts(deviceKey, day);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private Task<HttpResponseMessage> PostPrompt(string deviceKey, string text, DateTime tsUtc)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "prompts")
        {
            Content = JsonContent.Create(new PromptIngestRequest
            {
                Records = new List<PromptRecord>
                {
                    new()
                    {
                        TsUtc = tsUtc,
                        Machine = "M",
                        SessionId = "s",
                        Agent = "ClaudeCode",
                        Role = "user",
                        TimestampFromAgent = true,
                        CharCount = text.Length,
                        WordCount = 1,
                        Text = text,
                    },
                },
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
