using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE VOICE SWEEP'S PER-CYCLE BUDGET IS SPENT ON WORK, NOT ON ATTEMPTS (issue #2675).
///
/// What happened. The background narration sweep starts at most three generations per 45-second cycle,
/// and the cap is global across accounts on purpose - the wingman brain is one serialized resource. It
/// counted every attempt it DISPATCHED. One account's four sessions were owned by a computer running a
/// build that cannot send its conversation, so each attempt reached the Gateway's own store, found it
/// empty, recorded the honest "update that computer" fact and returned - having called neither the model
/// nor the speech provider. Each still took a slot. On 2026-09-04 that account consumed 4,356 slots and
/// every other account on the hosted Gateway got 5, so the one loop that recovers a session whose
/// narration failed never reached the sessions that needed it.
///
/// This test is the fleet in miniature: an account whose every voice session takes a no-op arm, beside a
/// second account with one session that genuinely has words to narrate, and ONE cycle of the REAL sweep.
///
/// IT DOES NOT DEPEND ON WHICH ACCOUNT IS SWEPT FIRST, which matters because nothing promises an order.
/// It asserts that FIVE sessions were all attempted in the one cycle; the old code could attempt three,
/// so it fails whichever account the pass happens to reach first.
///
/// REVERT-PROOF (against the real production lines in GatewayHost.SweepVoiceSessionsAsync): replace the
/// callback-counted budget with the old unconditional `generated++` after the dispatch and
/// <see cref="One_accounts_no_op_sessions_cannot_starve_another_accounts_session_out_of_the_same_cycle"/>
/// goes RED - three of the five sessions are attempted and the rest of the cycle never happens. Confirmed
/// by running it that way before the fix was written.
/// </summary>
public sealed class VoiceSweepBudgetTests : IAsyncLifetime
{
    private const string Token = "test-token";

    /// <summary>Four, because that is how many the account on the hosted Gateway had, and because it is more
    /// than the three-generation cap - so the starvation is reproduced rather than merely described.</summary>
    private static readonly string[] NoOpSessions =
        { "sweep-noop-1", "sweep-noop-2", "sweep-noop-3", "sweep-noop-4" };

    private const string NarratableSession = "sweep-narratable";

    private TenantId TenantNoOp { get; set; }
    private TenantId TenantNarratable { get; set; }

    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _dirNoOp = null!;
    private FakeTunnelDirector _dirNarratable = null!;

    private readonly ConcurrentQueue<DirectorCommand> _seenByNarratable = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-voice-budget-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var deviceNoOp = HostedTestEnrollment.Enroll(_gateway, "sub-noop", "noop@example.com", "dev-noop", "MN");
        var deviceNarratable = HostedTestEnrollment.Enroll(_gateway, "sub-real", "real@example.com", "dev-real", "MR");
        TenantNoOp = deviceNoOp.Tenant;
        TenantNarratable = deviceNarratable.Tenant;

        _dirNoOp = await FakeTunnelDirector.StartAsync(_gateway, deviceNoOp.DeviceKey, "dir-noop", "MN",
            dispatch: _ => FakeTunnelDirector.Ok(new { ok = true }));
        _dirNarratable = await FakeTunnelDirector.StartAsync(_gateway, deviceNarratable.DeviceKey, "dir-real", "MR",
            dispatch: cmd => { _seenByNarratable.Enqueue(cmd); return FakeTunnelDirector.Ok(new { ok = true }); });

        // Neither fake Director's Hello claims it sends conversations, which is exactly the state of the
        // real computer in the incident - so a session with nothing in the store takes the
        // "that computer cannot send its conversation" arm, the no-op arm this test is about.
        await _dirNoOp.PushSnapshotAsync(NoOpSessions.Select(Sample).ToArray());
        await _dirNarratable.PushSnapshotAsync(Sample(NarratableSession));

        foreach (var sid in NoOpSessions)
            _gateway.VoiceService!.Mark(TenantNoOp, sid);
        _gateway.VoiceService!.Mark(TenantNarratable, NarratableSession);
    }

    public async Task DisposeAsync()
    {
        await _dirNoOp.DisposeAsync();
        await _dirNarratable.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task One_accounts_no_op_sessions_cannot_starve_another_accounts_session_out_of_the_same_cycle()
    {
        // Only the second account has words to narrate. The first account's four sessions have nothing in
        // the store and a computer that cannot send one - they cost a dictionary lookup and a store read,
        // and nothing else.
        _gateway.SeedStoredConversationForTest(TenantNarratable, "dir-real", NarratableSession,
            ("User", "do the thing"), ("Assistant", "it is done"));

        // ONE cycle of the REAL production sweep - the timer callback itself, not a re-implementation.
        await _gateway.SweepVoiceSessionsAsync();

        // THE NO-OP ACCOUNT WAS FULLY SWEPT. The marker is recorded only by an ACTUAL attempt that read the
        // store and found it empty, so its presence on all four is the evidence that all four were tried -
        // and, under the old budget, at most three of these five facts could hold.
        foreach (var sid in NoOpSessions)
            Assert.True(_gateway.VoiceService!.DirectorCannotSendConversationFor(TenantNoOp, sid),
                $"session {sid} was never attempted, so the cycle ran out of budget on sessions that spend nothing");

        // AND THE OTHER ACCOUNT'S SESSION WAS REACHED IN THE SAME CYCLE. Its narration is the only attempt
        // here that costs the shared model and speech legs, and it is the one the budget exists to ration -
        // so it is the one that must survive a cycle full of attempts that cost neither. The live-screen
        // read arriving at ITS OWN Director is the proof the generation actually started.
        Assert.NotNull(await WaitForVerb(_seenByNarratable, "screen-grid", NarratableSession));
    }

    [Fact]
    public async Task A_no_op_session_stays_in_the_sweep_so_its_recovery_needs_nobody()
    {
        // The fix must NOT be "drop these sessions from the sweep". Keeping them is what makes the recovery
        // free: the moment that computer is updated it starts pushing, the next pass finds words, and
        // narration resumes with nobody touching the Gateway. Two cycles, and the sweep comes back to every
        // one of them both times.
        await _gateway.SweepVoiceSessionsAsync();
        foreach (var sid in NoOpSessions)
            Assert.True(_gateway.VoiceService!.DirectorCannotSendConversationFor(TenantNoOp, sid));

        // That computer is updated and pushes its conversation for one of them.
        _gateway.SeedStoredConversationForTest(TenantNoOp, "dir-noop", NoOpSessions[0],
            ("User", "ask"), ("Assistant", "answered"));

        await _gateway.SweepVoiceSessionsAsync();

        // The marker comes off by itself on the very next pass - no restart, nothing to clear by hand.
        Assert.False(_gateway.VoiceService!.DirectorCannotSendConversationFor(TenantNoOp, NoOpSessions[0]));
        // ...and the sessions still waiting on that computer are still being visited, not stood down.
        foreach (var sid in NoOpSessions.Skip(1))
            Assert.True(_gateway.VoiceService!.DirectorCannotSendConversationFor(TenantNoOp, sid));
    }

    /// <summary>Poll for a verb+session rather than sleeping a fixed time: the sweep fires generation onto
    /// the thread pool and the tunnel round-trip is asynchronous.</summary>
    private static async Task<DirectorCommand?> WaitForVerb(ConcurrentQueue<DirectorCommand> seen, string verb, string sessionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var hit = seen.FirstOrDefault(c => c.Verb == verb && c.SessionId == sessionId);
            if (hit is not null) return hit;
            await Task.Delay(50);
        }
        return null;
    }

    private static SessionDto Sample(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        // Settled at a turn end - the idle sweep only pre-builds Idle / WaitingForInput / WaitingForPerm.
        ActivityState = "WaitingForInput",
        Status = "Running",
        StatusColor = "red",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };
}
