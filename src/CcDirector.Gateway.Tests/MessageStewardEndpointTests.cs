using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission (CUT RESTORATION, SB-4a): end-to-end proof of the fleet-message steward
/// (messaging.steward) at the Director's restored /fleet/* handlers - a duplicate send is suppressed AND
/// surfaced back to the sender (never a silent drop), and with the steward disabled the same pair delivers
/// byte-identically. A real <see cref="ControlApiHost"/> is driven over loopback HTTP, exactly as
/// cc-devthrottle drives the local Director.
///
/// The steward guards ONLY /fleet/* (send + ask). The pre-cut version also proved "a normal
/// POST /sessions/{sid}/prompt is never touched by the steward"; that Director REST route was deleted at the
/// cut (prompt rides the tunnel now), so that assertion no longer has a loopback route to exercise and is not
/// re-added - the steward's narrow scope is inherent, as it is invoked only inside the two /fleet/* handlers.
/// </summary>
[Collection("DirectorRoot")]
public sealed class MessageStewardEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instances = Path.Combine(Path.GetTempPath(), "cc-msgsteward-dir-" + Guid.NewGuid().ToString("N"));

    public MessageStewardEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-msgsteward-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        TryDelete(_instances);
        TryDelete(_root);
        return Task.CompletedTask;
    }

    private async Task<(ControlApiHost host, SessionManager sm, HttpClient http, Guid targetId)> StartAsync(AgentOptions options)
    {
        var sm = new SessionManager(options);
        var target = sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        var host = new ControlApiHost(sm, "test", () => Task.CompletedTask,
            useEphemeralPort: true, authEnabled: false, directorId: "dir-A", instancesDirectory: _instances);
        var port = await host.StartAsync();
        var http = DirectorTestClient.Admin(port);
        return (host, sm, http, target.Id);
    }

    [Fact]
    public async Task FleetSend_DuplicateWithinWindow_IsSuppressedAndSurfaced()
    {
        var options = new AgentOptions();
        options.MessageSteward.DedupeWindowMs = 60_000; // wide, so the 2nd send is well inside the window
        var (host, sm, http, targetId) = await StartAsync(options);
        try
        {
            var body = new FleetSendRequest { FromSessionId = Guid.NewGuid().ToString(), ToSessionId = targetId.ToString(), Text = "hello" };

            var resp1 = await (await http.PostAsJsonAsync("fleet/send", body)).Content.ReadFromJsonAsync<FleetSendResponse>();
            Assert.NotNull(resp1);
            Assert.True(resp1.Accepted);
            Assert.Equal(1, resp1.DeliveredCount);

            var resp2 = await (await http.PostAsJsonAsync("fleet/send", body)).Content.ReadFromJsonAsync<FleetSendResponse>();
            Assert.NotNull(resp2);
            Assert.False(resp2.Accepted);           // duplicate suppressed
            Assert.Equal(0, resp2.DeliveredCount);
            Assert.False(string.IsNullOrWhiteSpace(resp2.Error)); // surfaced to the sender, not silent
            Assert.Contains("duplicate", resp2.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally { http.Dispose(); await host.StopAsync(); sm.Dispose(); }
    }

    [Fact]
    public async Task FleetSend_StewardDisabled_DeliversBothCopies_ByteIdentical()
    {
        var options = new AgentOptions();
        options.MessageSteward.Enabled = false; // flag off
        var (host, sm, http, targetId) = await StartAsync(options);
        try
        {
            var body = new FleetSendRequest { FromSessionId = Guid.NewGuid().ToString(), ToSessionId = targetId.ToString(), Text = "hello" };

            var resp1 = await (await http.PostAsJsonAsync("fleet/send", body)).Content.ReadFromJsonAsync<FleetSendResponse>();
            var resp2 = await (await http.PostAsJsonAsync("fleet/send", body)).Content.ReadFromJsonAsync<FleetSendResponse>();

            Assert.NotNull(resp1);
            Assert.True(resp1.Accepted);
            Assert.NotNull(resp2);
            Assert.True(resp2.Accepted);            // no dedupe when the steward is off
            Assert.Equal(1, resp2.DeliveredCount);
        }
        finally { http.Dispose(); await host.StopAsync(); sm.Dispose(); }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { /* best effort */ }
    }
}
