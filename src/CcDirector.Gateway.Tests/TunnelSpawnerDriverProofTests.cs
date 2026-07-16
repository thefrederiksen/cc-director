using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR E-B2): the LAST Group B callers - the machine session spawner
/// (cron / interactive "start a session on another computer") and the work-list drain driver (#274) -
/// now create sessions and read their buffers DOWN the tunnel, via the director-level
/// <see cref="SessionVerbClient.CreateSessionAsync"/> and the session-level buffer verb.
///
/// Proof by CONSTRUCTION: these callers have NO HTTP dial path at all - they are bound to a Director by
/// id and deliver through the stream hub, so a call that SUCCEEDS could only have gone down the tunnel.
/// The recording hook stands in for the Director stream and asserts the exact verb + payload marshaling.
/// This is the same trick the other Phase 2 proofs use (TunnelDirectorReadProofTests / SessionVerbClientTests).
/// </summary>
public sealed class TunnelSpawnerDriverProofTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

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

    private sealed class StubResolver : IDirectorTargetResolver
    {
        private readonly DirectorTargetResult _result;
        public StubResolver(DirectorTargetResult result) => _result = result;
        public Task<DirectorTargetResult> ResolveAsync(string machine, CancellationToken ct) => Task.FromResult(_result);
    }

    [Fact]
    public async Task MachineSpawner_createsOverTheTunnel_byConstruction()
    {
        var hub = new RecordingHub
        {
            Next = DirectorCommandResult.Success(JsonSerializer.Serialize(new SessionDto { SessionId = "sid-42" }, Web)),
        };
        var resolver = new StubResolver(new DirectorTargetResult("dir-7", null));
        var spawner = new MachineSessionSpawner(resolver, hub.Send);

        var (ok, dto, error, directorId) = await spawner.SpawnOnMachineAsync(
            "MACHINE_A", new NewSessionRequest { RepoPath = @"C:\repo", Agent = "ClaudeCode" }, CancellationToken.None);

        Assert.True(ok);            // the spawner has no HTTP path; the create verb went down the tunnel hub
        Assert.Equal("sid-42", dto?.SessionId);
        Assert.Null(error);
        Assert.Equal("dir-7", directorId);
        Assert.Equal("create", hub.Last!.Verb);
        Assert.Equal("", hub.Last.SessionId);    // director-level create carries no target session id
        Assert.Equal("dir-7", hub.LastDirectorId);
    }

    [Fact]
    public async Task ImplSessionDriver_startsAndReadsOverTheTunnel_byConstruction()
    {
        var hub = new RecordingHub();
        var driver = new DirectorImplSessionDriver("dir-9", @"C:\repo", hub.Send);

        // Start: rides the director-level "create" verb, returns the new session id.
        hub.Next = DirectorCommandResult.Success(JsonSerializer.Serialize(new SessionDto { SessionId = "sid-start" }, Web));
        var (sid, startError) = await driver.StartImplementationSessionAsync("item-1", "/implementation-loop 262", CancellationToken.None);
        Assert.Null(startError);
        Assert.Equal("sid-start", sid);
        Assert.Equal("create", hub.Last!.Verb);
        Assert.Equal("dir-9", hub.LastDirectorId);
        var sent = JsonSerializer.Deserialize<NewSessionRequest>(hub.Last.PayloadJson, Web);
        Assert.Equal("/implementation-loop 262", sent?.PrePrompt);

        // Read transcript: rides the session-level "buffer" verb for the started session.
        hub.Next = DirectorCommandResult.Success(JsonSerializer.Serialize(new BufferResponse { Text = "IMPL-LOOP-TERMINAL" }, Web));
        var transcript = await driver.ReadTranscriptAsync("sid-start", CancellationToken.None);
        Assert.Equal("IMPL-LOOP-TERMINAL", transcript);
        Assert.Equal("buffer", hub.Last!.Verb);
        Assert.Equal("sid-start", hub.Last.SessionId);
    }

    [Fact]
    public async Task ImplSessionDriver_tunnelCreateFailure_reportsTheError_noSessionId()
    {
        var hub = new RecordingHub { Next = DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "busy") };
        var driver = new DirectorImplSessionDriver("dir-9", @"C:\repo", hub.Send);

        var (sid, error) = await driver.StartImplementationSessionAsync("item-1", "seed", CancellationToken.None);

        Assert.Null(sid);
        Assert.Contains("Conflict", error);
    }
}
