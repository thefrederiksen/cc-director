using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Inspection 1, findings 1 and 5: the eviction cascade must not destroy a machine that is already back,
/// and the horizon the owner configures must be the one the SHIPPED Gateway uses.
///
/// WHY THESE ARE ONE FILE. Both are about the same gap between a decision and its consequences. The sweep
/// decides a machine is gone; three destructive subscribers then act on that decision - releasing session
/// numbers, forgetting the pushed cache, deleting snoozes. Everything here is about that interval, and about
/// whether the thing being decided is even the thing the owner configured.
///
/// WHAT THE SEAM TEST DOES AND DOES NOT PROVE. <see cref="DirectorRegistry.OnSweepJudgedForTest"/> fires
/// inside the exact window finding 1 describes, and a reconnect driven from it proves the ORDERING window
/// exists and is closed. It proves NOTHING about the concurrent race under real thread scheduling - no test
/// here starts a second thread or attempts to interleave one. Said plainly because the difference is easy to
/// lose: a passing seam test is evidence about sequence, not about concurrency.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class EvictionRaceAndCompositionTests : IDisposable
{
    private const string DirectorId = "dir-north";
    private const string Machine = "SOREN_NORTH";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-evictrace-" + Guid.NewGuid().ToString("N"));

    public EvictionRaceAndCompositionTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-evictrace-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static SessionDto Session(string id) => new()
    {
        SessionId = id,
        Name = id,
        ActivityState = "Waiting",
        StatusColor = "red",
        LastActivityAt = DateTime.UtcNow,
    };

    private static DirectorRegistry RegistryWith(TimeSpan horizon, string instancesDir)
    {
        var registry = new DirectorRegistry(instancesDir) { EvictionHorizon = horizon };
        registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        return registry;
    }

    /// <summary>Wind the entry's last-seen back so the NEXT sweep judges it stale on its snapshot.</summary>
    private static void AgePastHorizon(DirectorRegistry registry)
    {
        var entry = registry.Get(TenantId.Local, DirectorId);
        Assert.NotNull(entry);
        entry!.LastSeen = DateTime.UtcNow - registry.EvictionHorizon - TimeSpan.FromMinutes(1);
    }

    // ---------------------------------------------------------------------------------------------------
    // Finding 1a: the sweep judges a SNAPSHOT and used to remove by key.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SweepJudgedItStale_ThenItReconnects_TheDirectorSurvivesAndNoRemovalFires()
    {
        using var registry = RegistryWith(TimeSpan.FromMinutes(30), _instancesDir);
        var removals = new List<string>();
        registry.OnDirectorRemoved += r => removals.Add(r.DirectorId);

        AgePastHorizon(registry);

        // The reconnect lands in the window: after the sweep has judged the captured entry stale, before it
        // removes. This is the machine coming back at the worst possible moment.
        var reconnected = 0;
        registry.OnSweepJudgedForTest = _ =>
        {
            if (reconnected++ > 0) return;
            registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234,
                startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        };

        registry.SweepStale();

        Assert.Equal(1, reconnected);                                    // the window really was entered
        Assert.NotNull(registry.Get(TenantId.Local, DirectorId));        // the machine is still here
        Assert.Empty(removals);                                          // and nothing downstream was told to destroy it
    }

    /// <summary>
    /// The control, and it is not optional. Without it, code that simply never removes anything passes the
    /// test above - and "never forget a machine" is the opposite bug, the one the eviction horizon exists to
    /// prevent. This proves removal is still reachable on the same path with the same setup.
    /// </summary>
    [Fact]
    public void SweepJudgedItStale_AndNothingReconnects_TheDirectorIsRemovedAndTheRemovalFires()
    {
        using var registry = RegistryWith(TimeSpan.FromMinutes(30), _instancesDir);
        var removals = new List<string>();
        registry.OnDirectorRemoved += r => removals.Add(r.DirectorId);

        AgePastHorizon(registry);
        registry.SweepStale();

        Assert.Null(registry.Get(TenantId.Local, DirectorId));
        Assert.Equal(new[] { DirectorId }, removals.ToArray());
    }

    /// <summary>
    /// The heartbeat is the trap finding 1 names: it mutates LastSeen IN PLACE on the live object, so a
    /// compare-and-remove against the captured VALUE still matches the same instance and still removes. Only
    /// re-reading and re-judging survives this.
    /// </summary>
    [Fact]
    public void SweepJudgedItStale_ThenAHeartbeatRefreshesTheSameInstance_TheDirectorSurvives()
    {
        using var registry = RegistryWith(TimeSpan.FromMinutes(30), _instancesDir);
        var removals = new List<string>();
        registry.OnDirectorRemoved += r => removals.Add(r.DirectorId);

        AgePastHorizon(registry);

        var beat = 0;
        registry.OnSweepJudgedForTest = _ =>
        {
            if (beat++ > 0) return;
            Assert.True(registry.Heartbeat(DirectorId));
        };

        registry.SweepStale();

        Assert.Equal(1, beat);
        Assert.NotNull(registry.Get(TenantId.Local, DirectorId));
        Assert.Empty(removals);
    }

    // ---------------------------------------------------------------------------------------------------
    // Finding 1b: the window between the removal DECISION and the destructive subscribers running.
    // These drive the REAL GatewayHost, so the subscribers under test are the shipped ones. A hand-wired
    // harness would have proved only that a guard works when a test remembers to wire it.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EvictionCascade_WhenTheMachineIsBack_FiresButSkipsTheDestructivePart()
    {
        await using var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345",
            authEnabled: true, instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));

        // The horizon is deliberately left at whatever the SHIPPED composition chose - it is init-only on the
        // registry and cannot be reached around, which is the point. AgePastHorizon winds the entry back
        // relative to that value, so this drives the real eviction whatever the configured horizon is.
        gateway.Registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);

        // The tunnel is UP and the machine has pushed - this is a live Director by the only measure the
        // guard consults.
        gateway.PushedSessions.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(gateway.PushedSessions.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, new[] { Session("s-1") }));
        Assert.True(gateway.PushedSessions.IsStreamConnected(TenantId.Local, DirectorId));

        // A NON-destructive observer, so this test can tell "the guard worked" from "the cascade never ran".
        // Asserting only that the sessions survived would also pass if the event never fired, if a subscriber
        // were never wired, or if something threw upstream.
        var cascadeFired = false;
        gateway.Registry.OnDirectorRemoved += _ => cascadeFired = true;

        AgePastHorizon(gateway.Registry);
        gateway.Registry.SweepStale();

        Assert.True(cascadeFired);   // the cascade DID run - the guard is what stopped the damage
        // ...and the destructive part abandoned itself: the machine keeps its pushed sessions.
        Assert.NotEmpty(gateway.PushedSessions.GetLastKnown(TenantId.Local, DirectorId).Sessions);
    }

    /// <summary>
    /// The control that makes the test above mean something: with the tunnel DOWN, the same cascade on the
    /// same path DOES destroy. Without this, a guard that always skipped - or a Forget that never worked -
    /// would look identical.
    /// </summary>
    [Fact]
    public async Task EvictionCascade_WhenTheMachineIsGenuinelyGone_DestroysAsItAlwaysDid()
    {
        await using var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345",
            authEnabled: true, instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));

        // The horizon is deliberately left at whatever the SHIPPED composition chose - it is init-only on the
        // registry and cannot be reached around, which is the point. AgePastHorizon winds the entry back
        // relative to that value, so this drives the real eviction whatever the configured horizon is.
        gateway.Registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);

        gateway.PushedSessions.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(gateway.PushedSessions.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, new[] { Session("s-1") }));
        // The tunnel closes and stays closed - the machine really is gone.
        Assert.True(gateway.PushedSessions.UnregisterConnection(TenantId.Local, DirectorId, "conn-1"));
        Assert.False(gateway.PushedSessions.IsStreamConnected(TenantId.Local, DirectorId));

        var cascadeFired = false;
        gateway.Registry.OnDirectorRemoved += _ => cascadeFired = true;

        AgePastHorizon(gateway.Registry);
        gateway.Registry.SweepStale();

        Assert.True(cascadeFired);
        Assert.Empty(gateway.PushedSessions.GetLastKnown(TenantId.Local, DirectorId).Sessions);
    }

    // ---------------------------------------------------------------------------------------------------
    // Finding 5: the SHIPPED composition. The horizon was advertised as configurable and no test built a
    // real GatewayHost from a configured value - so replacing the one line that reads configuration with
    // the default constant left every existing test green while the setting did nothing at all.
    // ---------------------------------------------------------------------------------------------------

    private static void SeedGatewayConfig(string json)
    {
        var path = CcStorage.ConfigJson();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public async Task AConfiguredEvictionHorizon_ReachesTheProductionRegistry()
    {
        SeedGatewayConfig("""{ "gateway": { "directorEvictionHorizonHours": 6 } }""");

        await using var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345",
            authEnabled: true, instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));

        // Not the loader's answer - the REGISTRY THE GATEWAY ACTUALLY SWEEPS WITH. That is the whole point:
        // the configuration tests already proved GatewayConfig.Load reads the key, and passed happily while
        // nothing carried the value onward.
        Assert.Equal(TimeSpan.FromHours(6), gateway.Registry.EvictionHorizon);
        Assert.NotEqual(DirectorRegistry.DefaultEvictionHorizon, gateway.Registry.EvictionHorizon);
    }

    [Fact]
    public async Task WithNoConfiguredHorizon_TheProductionRegistryUsesTheDayLongDefault()
    {
        SeedGatewayConfig("""{ "gateway": { } }""");

        await using var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345",
            authEnabled: true, instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));

        Assert.Equal(DirectorRegistry.DefaultEvictionHorizon, gateway.Registry.EvictionHorizon);
        Assert.Equal(TimeSpan.FromHours(24), gateway.Registry.EvictionHorizon);
    }
}
