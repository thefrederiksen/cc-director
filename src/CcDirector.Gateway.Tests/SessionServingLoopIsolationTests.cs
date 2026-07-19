using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hosted Multi-Tenancy (session-serving PR2): the Gateway's BACKGROUND LOOPS are tenant-aware, and the ONE
/// down-channel lookup every command routes through resolves the tenant of the current unit of work instead
/// of a hard-coded <see cref="TenantId.Local"/>.
///
/// Two Directors connect on two DIFFERENT tenants over the real tunnel, each authenticated with its OWN
/// per-device key, exactly as <c>SessionServingReadIsolationTests</c> arranges the read path. This drives the
/// REAL production loop functions - <c>GatewayHost.SendCommandAsync</c> and <c>GatewayHost.SweepAutoDismiss</c>
/// (the auto-dismiss timer callback) - not a helper or a re-implementation.
///
/// Every absence claim here is preceded by a POSITIVE CONTROL in the same test: we first prove the command
/// DOES arrive for the tenant that owns the Director, then prove it does NOT arrive for the tenant that does
/// not. Without that ordering a total failure to deliver anything would satisfy every "did not arrive"
/// assertion and the test would pass vacuously.
///
/// REVERT-PROOFS (each guard independently, against the real production line):
///  - <c>GatewayHost.SendCommandAsync</c>: replace its <c>_tenantPass.Current</c> resolution with
///    <c>TenantId.Local</c> and <see cref="SendCommand_reaches_only_the_owning_tenants_director"/> goes RED on
///    its positive control - the Local partition holds no Director on hosted, so the same-tenant send that
///    must succeed returns null.
///  - <c>GatewayHost.SweepAutoDismiss</c>: replace its <c>_tenantPass.ForEachTenant(...)</c> with a single
///    direct call (the pre-PR2 implicit single pass) and
///    <see cref="AutoDismiss_sweep_runs_one_pass_per_tenant_and_closes_only_that_tenants_sessions"/> goes RED -
///    the unscoped pass reads no tenant's fleet, so neither session is closed.
///
/// The assembly runs sequentially (TestParallelization), so toggling CC_GATEWAY_HOSTED here is safe; it is
/// reset in DisposeAsync.
/// </summary>
public sealed class SessionServingLoopIsolationTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string SessA = "loop-sess-a";
    private const string SessB = "loop-sess-b";
    private static readonly TenantId TenantA = new("tenant-alice");
    private static readonly TenantId TenantB = new("tenant-bob");

    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;

    // Every command each Director received over the tunnel, so a test can assert BOTH that the right one
    // arrived and that the wrong one never did.
    private readonly ConcurrentQueue<DirectorCommand> _seenByA = new();
    private readonly ConcurrentQueue<DirectorCommand> _seenByB = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-loop-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();

        // Two accounts: two device keys, each bound to its OWN tenant. The tunnel Hello binds each Director
        // into its key's tenant, so its pushed sessions land in that tenant's partition.
        var keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", TenantA.Value);
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", TenantB.Value);

        _dirA = await FakeTunnelDirector.StartAsync(_gateway, keyA, "dir-a", "MA",
            dispatch: cmd => { _seenByA.Enqueue(cmd); return FakeTunnelDirector.Ok(new { ok = true }); });
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, keyB, "dir-b", "MB",
            dispatch: cmd => { _seenByB.Enqueue(cmd); return FakeTunnelDirector.Ok(new { ok = true }); });
    }

    public async Task DisposeAsync()
    {
        await _dirA.DisposeAsync();
        await _dirB.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task SendCommand_reaches_only_the_owning_tenants_director()
    {
        await _dirA.PushSnapshotAsync(Sample(SessA, autoDismiss: false));
        await _dirB.PushSnapshotAsync(Sample(SessB, autoDismiss: false));

        // POSITIVE CONTROL FIRST: inside tenant A's scope, a command for A's OWN Director is delivered. If
        // this did not hold, every absence assertion below would pass vacuously.
        DirectorCommandResult? sameTenant;
        using (_gateway.TenantBoundary.EnterScope(TenantA))
            sameTenant = await _gateway.SendCommandAsync("dir-a", Prompt("hello-a"));

        Assert.NotNull(sameTenant);
        Assert.Contains(_seenByA, c => c.PayloadJson.Contains("hello-a") == true);

        // ABSENCE: inside tenant B's scope, the SAME command aimed at tenant A's Director resolves nothing.
        // The connection lookup runs in B's partition, which holds no dir-a, so the send is denied - B can
        // never drive A's Director even by naming its id directly.
        DirectorCommandResult? crossTenant;
        using (_gateway.TenantBoundary.EnterScope(TenantB))
            crossTenant = await _gateway.SendCommandAsync("dir-a", Prompt("cross-tenant"));

        Assert.Null(crossTenant);
        Assert.DoesNotContain(_seenByA, c => c.PayloadJson.Contains("cross-tenant") == true);
        Assert.DoesNotContain(_seenByB, c => c.PayloadJson.Contains("cross-tenant") == true);

        // DENY-BY-DEFAULT: with NO tenant scope at all (an unconverted background caller), the send resolves
        // no tenant and delivers nothing - it must never fall back to the Local partition.
        var unscoped = await _gateway.SendCommandAsync("dir-a", Prompt("unscoped"));

        Assert.Null(unscoped);
        Assert.DoesNotContain(_seenByA, c => c.PayloadJson.Contains("unscoped") == true);
    }

    [Fact]
    public async Task AutoDismiss_sweep_runs_one_pass_per_tenant_and_closes_only_that_tenants_sessions()
    {
        // Both tenants have a session that qualifies for auto-dismiss (auto-dismiss + "done" verdict + settled
        // at a turn end). A single implicit pass can serve at most ONE tenant; a correct per-tenant pass
        // serves both, and each kill goes down its OWN tenant's Director.
        await _dirA.PushSnapshotAsync(Sample(SessA, autoDismiss: true));
        await _dirB.PushSnapshotAsync(Sample(SessB, autoDismiss: true));

        // The REAL timer callback - the production function, not a helper.
        _gateway.SweepAutoDismiss();

        // POSITIVE CONTROL FIRST: BOTH tenants' sessions were closed. This is what proves the sweep iterated
        // tenants at all; without it the two absence assertions below would hold trivially in a sweep that did
        // nothing whatsoever.
        var killA = await WaitForKill(_seenByA, SessA);
        var killB = await WaitForKill(_seenByB, SessB);
        Assert.NotNull(killA);
        Assert.NotNull(killB);

        // ABSENCE: neither Director was ever asked to kill the OTHER tenant's session. The per-tenant pass
        // sees one tenant's fleet at a time, so a session id from the other partition never enters the pick
        // list, and the down-channel send could not have reached the wrong Director anyway.
        Assert.DoesNotContain(_seenByA, c => c.SessionId == SessB);
        Assert.DoesNotContain(_seenByB, c => c.SessionId == SessA);
    }

    /// <summary>
    /// Poll for the kill verb rather than sleeping a fixed time: the sweep is fire-and-forget onto the thread
    /// pool and the tunnel round-trip is asynchronous, so a fixed wait would be either flaky or slow.
    /// </summary>
    private static async Task<DirectorCommand?> WaitForKill(ConcurrentQueue<DirectorCommand> seen, string sessionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var hit = seen.FirstOrDefault(c => c.Verb == "kill" && c.SessionId == sessionId);
            if (hit is not null) return hit;
            await Task.Delay(50);
        }
        return null;
    }

    private static DirectorCommand Prompt(string marker) => new()
    {
        CommandId = Guid.NewGuid().ToString("N"),
        Verb = "prompt",
        PayloadJson = $"{{\"text\":\"{marker}\"}}",
    };

    private static SessionDto Sample(string sid, bool autoDismiss) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        // Settled at a turn end, which is what auto-dismiss requires before it will close anything.
        ActivityState = "WaitingForInput",
        Status = "Running",
        StatusColor = "red",
        AutoDismiss = autoDismiss,
        DismissVerdict = autoDismiss ? "done" : null,
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
