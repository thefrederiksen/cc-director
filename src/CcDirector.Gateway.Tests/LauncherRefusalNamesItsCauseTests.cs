using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A REFUSAL MUST SAY WHICH CONDITION IT IS, BECAUSE THE TWO HAVE DIFFERENT FIXES.
///
/// Inspection 3, finding 2. Phase 6 deleted the launcher's listener and the Gateway's HTTP relay to it,
/// leaving the stream the launcher opens as the only path. A launcher built BEFORE that cut opens no
/// stream - it expected to be dialed - so it registers, heartbeats happily, and can never receive a
/// command. The hosted Gateway deploys independently of the desktop application and normally moves
/// first, which makes this the ordinary shape of an upgrade rather than an edge case.
///
/// The Architect REFUSED a compatibility arm: an arm that dials an old launcher's port is precisely the
/// second door this mission exists to delete, and it would have to be deleted again later. What was
/// required instead is this - the refusal must NAME the cause, because a launcher too old to accept
/// commands is not the same condition as one that crashed, and the 502 said the same thing for both.
///
/// The distinction is drawn from OBSERVED FACTS rather than asserted: a launcher that heartbeated
/// seconds ago is reaching this Gateway, and one holding no stream cannot be reached by it. Both facts
/// travel on the answer - the version and the seconds since the last heartbeat - so a reader can check
/// the inference instead of taking it.
/// </summary>
public sealed class LauncherRefusalNamesItsCauseTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private const string Machine = "WORKSTATION-A";

    /// <summary>No stream hook at all: nothing can be delivered, which is the state both refusals share.</summary>
    private static async Task<LauncherLifecycleRelay.LauncherRelayOutcome> RefuseAsync(LauncherRegistry registry) =>
        await LauncherLifecycleRelay.SendDirectorVerbAsync(
            Tenant, Machine, "start", exePath: null, confirmProtected: false,
            registry, sendLauncherCommand: null, CancellationToken.None);

    private static void Register(LauncherRegistry registry, string version = "1.9.7") =>
        registry.Upsert(Tenant, new LauncherRegistrationRequest
        {
            MachineName = Machine,
            Pid = 99,
            Version = version,
        });

    /// <summary>
    /// THE PRE-PHASE-6 LAUNCHER. Freshly registered - so its heartbeat is seconds old - and holding no
    /// stream. It is talking to this Gateway and this Gateway cannot talk to it, which is exactly what a
    /// launcher too old to stream looks like from here.
    /// </summary>
    [Fact]
    public async Task A_heartbeating_launcher_with_no_stream_is_refused_as_TOO_OLD_not_as_disconnected()
    {
        var registry = new LauncherRegistry();
        Register(registry, version: "1.9.7");

        var outcome = await RefuseAsync(registry);

        Assert.Equal(LauncherLifecycleRelay.RelayOutcomeKind.NotStreamCapable, outcome.Kind);
        Assert.NotEqual(LauncherLifecycleRelay.RelayOutcomeKind.NotConnected, outcome.Kind);

        // The evidence travels with the verdict, so the refusal can show its working rather than assert a
        // cause. The version is what tells an operator WHICH build on that machine is not accepting
        // commands.
        Assert.Equal("1.9.7", outcome.LauncherVersion);
        Assert.True(outcome.QuietForSeconds < (int)LauncherRegistry.HeartbeatTimeout.TotalSeconds,
            "a freshly registered launcher must read as recently heard from");
    }

    /// <summary>
    /// THE CRASHED OR CUT-OFF LAUNCHER. Registered, but silent for longer than the heartbeat timeout, and
    /// holding no stream. Same undeliverable command, genuinely different situation: here the network or
    /// the process IS the problem, and telling this user to update their launcher would be as wrong as
    /// telling the other one to check their network.
    /// </summary>
    [Fact]
    public async Task A_launcher_that_has_gone_quiet_is_refused_as_NOT_CONNECTED()
    {
        var registry = new LauncherRegistry();
        Register(registry);
        MakeHeartbeatStale(registry);

        var outcome = await RefuseAsync(registry);

        Assert.Equal(LauncherLifecycleRelay.RelayOutcomeKind.NotConnected, outcome.Kind);
        Assert.True(outcome.QuietForSeconds >= (int)LauncherRegistry.HeartbeatTimeout.TotalSeconds,
            "a launcher past the heartbeat timeout must read as silent");
    }

    /// <summary>Never registered stays its own answer - unchanged, and pinned so the new split did not
    /// quietly absorb it.</summary>
    [Fact]
    public async Task An_unregistered_machine_is_still_its_own_answer()
    {
        var outcome = await RefuseAsync(new LauncherRegistry());

        Assert.Equal(LauncherLifecycleRelay.RelayOutcomeKind.NoLauncher, outcome.Kind);
    }

    /// <summary>
    /// Age the registered launcher's heartbeat past the timeout by rewriting the timestamp the registry
    /// hands out. Done through the public registration DTO rather than by waiting ninety seconds, and it
    /// is the same field the relay reads - so what is aged here is exactly what is judged there.
    /// </summary>
    private static void MakeHeartbeatStale(LauncherRegistry registry)
    {
        var dto = registry.Get(Tenant, Machine);
        Assert.NotNull(dto);
        dto!.LastSeenAt = DateTime.UtcNow - LauncherRegistry.HeartbeatTimeout - TimeSpan.FromSeconds(30);
    }
}
