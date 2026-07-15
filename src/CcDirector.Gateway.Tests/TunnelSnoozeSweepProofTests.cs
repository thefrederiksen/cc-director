using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR E-B3): the snooze watchdog's Director I/O now rides the tunnel
/// through <see cref="SnoozeSweepDirectorClient"/>. These tests prove the tunnel-vs-HTTP decision and the
/// per-verb marshaling directly, without a live Director:
///  - the RAW hold read rides the "snapshot" read verb and maps the whole SessionDto.HoldState tri-state;
///  - the expiry nudge rides the "hold" write verb with OnHold=false;
///  - a failed tunnel read maps to null (keep the entry), exactly as the HTTP dial returned null on a non-200;
///  - reachability is true over a live stream even with no HTTP endpoint (the post-cut case) and false for a
///    Director the Gateway does not know at all (the dead-man's-switch).
/// The HTTP fallback path (null sendCommand) is the pre-existing behavior, covered by SnoozeExpirySweepTests /
/// SnoozeEndToEndTests; here we assert the tunnel branch, so no real endpoint is dialed.
/// </summary>
public sealed class TunnelSnoozeSweepProofTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-snoozetunnel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private sealed class RecordingHub
    {
        public DirectorCommand? Last;
        public string? LastDirectorId;
        public DirectorCommandResult? Next;

        public DirectorCommandRouter.SendDirectorCommandAsync Send => (directorId, command, ct) =>
        {
            LastDirectorId = directorId;
            Last = command;
            return Task.FromResult<DirectorCommandResult?>(Next);
        };
    }

    // A tunnel-enabled client whose Director is otherwise unknown to the registry, so any SUCCESS proves the
    // tunnel branch ran (the HTTP fallback resolves no endpoint and returns null).
    private SnoozeSweepDirectorClient TunnelClient(RecordingHub hub, PushedSessionStore? push = null) =>
        new(new DirectorRegistry(_dir), push, hub.Send);

    // Defect 20: the read carries ALL THREE hold states, not a boolean. DeferredHold is the one that
    // mattered - it used to arrive as OnHold=false, indistinguishable from None, and the sweep deleted the
    // snooze on it. If this Theory ever loses its DeferredHold row, the defect is unguarded again.
    [Theory]
    [InlineData(HoldStates.None, HoldState.None)]
    [InlineData(HoldStates.Held, HoldState.Held)]
    [InlineData(HoldStates.DeferredHold, HoldState.DeferredHold)]
    public async Task ReadHoldState_ridesTheSnapshotVerb_andMapsTheWholeTriState(string wire, HoldState expected)
    {
        var hub = new RecordingHub
        {
            Next = DirectorCommandResult.Success(JsonSerializer.Serialize(new SessionDto { HoldState = wire }, Web)),
        };

        var holdState = await TunnelClient(hub).ReadHoldStateAsync("dir-1", "sid-1", CancellationToken.None);

        Assert.Equal(expected, holdState);
        Assert.Equal("snapshot", hub.Last!.Verb);
        Assert.Equal("sid-1", hub.Last.SessionId);
        Assert.Equal("dir-1", hub.LastDirectorId);
    }

    [Fact]
    public async Task ReadHoldState_failedTunnelResult_returnsNull_soTheEntryIsKept()
    {
        var hub = new RecordingHub { Next = DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "no such session") };

        var holdState = await TunnelClient(hub).ReadHoldStateAsync("dir-1", "sid-1", CancellationToken.None);

        Assert.Null(holdState);
    }

    [Fact]
    public async Task ReadHoldState_unknownHoldStateString_returnsNull_ratherThanGuessing()
    {
        // A Director speaking a hold state this Gateway does not know is "I do not know", never a guess.
        // The sweep treats null as a missed read and changes nothing - the only safe answer, because both
        // wrong guesses lose something: "None" deletes a live snooze, "Held" expires one that never landed.
        var hub = new RecordingHub
        {
            Next = DirectorCommandResult.Success(JsonSerializer.Serialize(new SessionDto { HoldState = "Parked" }, Web)),
        };

        var holdState = await TunnelClient(hub).ReadHoldStateAsync("dir-1", "sid-1", CancellationToken.None);

        Assert.Null(holdState);
    }

    [Fact]
    public async Task NudgeUnhold_ridesTheHoldVerb_withOnHoldFalse()
    {
        var hub = new RecordingHub { Next = DirectorCommandResult.Success() };

        await TunnelClient(hub).NudgeUnholdAsync("dir-1", "sid-1", CancellationToken.None);

        Assert.Equal("hold", hub.Last!.Verb);
        Assert.Equal("sid-1", hub.Last.SessionId);
        Assert.Equal("dir-1", hub.LastDirectorId);
        Assert.False((bool?)JsonNode.Parse(hub.Last.PayloadJson)!.AsObject()["onHold"]);
    }

    [Fact]
    public void IsReachable_trueOverAStreamWithNoHttpEndpoint_falseForAnUnknownDirector()
    {
        var registry = new DirectorRegistry(_dir);
        var push = new PushedSessionStore();
        var client = new SnoozeSweepDirectorClient(registry, push, (_, _, _) => Task.FromResult<DirectorCommandResult?>(null));

        // Unknown to the Gateway entirely -> dead-man's-switch (leave the entry alone).
        Assert.False(client.IsReachable("dir-unknown"));

        // Registered but with NO reachable HTTP endpoint, yet stream-connected -> reachable over the tunnel
        // (the post-cut, stream-only case).
        registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = "dir-stream",
            MachineName = Environment.MachineName,
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });
        Assert.False(client.IsReachable("dir-stream")); // registered, no endpoint, no stream -> not reachable yet
        push.RegisterConnection("dir-stream", "conn-1");
        Assert.True(client.IsReachable("dir-stream"));  // now stream-connected -> reachable via the tunnel
    }
}
