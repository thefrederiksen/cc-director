using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="MachineSessionSpawner"/> - the single resolve-then-create path shared by
/// the cron firing engine and the interactive POST /machines/{machine}/sessions relay ("start a session
/// on another computer"). A stub resolver stands in for the live registry/launcher and a fake create
/// delegate stands in for the Director's session-create call, so the resolve-then-create DECISION is
/// verified without a live Director. Covers the good path (resolver returns an endpoint -> create ->
/// success) and the fail-loud path (resolver returns an offline Error -> failure, and the create is
/// NEVER attempted, i.e. no local fallback).
/// </summary>
public sealed class MachineSessionSpawnerTests
{
    /// <summary>A resolver that returns a fixed <see cref="DirectorTargetResult"/> and counts its calls.</summary>
    private sealed class StubResolver : IDirectorTargetResolver
    {
        private readonly DirectorTargetResult _result;
        public int ResolveCount { get; private set; }
        public string? LastMachine { get; private set; }

        public StubResolver(DirectorTargetResult result) => _result = result;

        public Task<DirectorTargetResult> ResolveAsync(string machine, CancellationToken ct)
        {
            ResolveCount++;
            LastMachine = machine;
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task Spawn_ResolverReturnsEndpoint_CreatesSession_ReturnsIt()
    {
        var resolver = new StubResolver(new DirectorTargetResult("http://127.0.0.1:7900", "d-new", null));
        string? seenDirectorId = null;
        string? seenEndpoint = null;
        NewSessionRequest? seenReq = null;
        var spawner = new MachineSessionSpawner(resolver, (directorId, endpoint, req, ct) =>
        {
            seenDirectorId = directorId;
            seenEndpoint = endpoint;
            seenReq = req;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "sid-123" }, null));
        });

        var request = new NewSessionRequest { RepoPath = @"C:\repo", Agent = "ClaudeCode" };
        var (ok, dto, error, directorId) = await spawner.SpawnOnMachineAsync("MACHINE_A", request, CancellationToken.None);

        Assert.True(ok);
        Assert.NotNull(dto);
        Assert.Equal("sid-123", dto!.SessionId);
        Assert.Null(error);
        Assert.Equal("d-new", directorId);
        // The create was made against the resolved Director id (the tunnel leg) and endpoint (the fallback leg)
        // with the same request.
        Assert.Equal("d-new", seenDirectorId);
        Assert.Equal("http://127.0.0.1:7900", seenEndpoint);
        Assert.Same(request, seenReq);
        Assert.Equal("MACHINE_A", resolver.LastMachine);
    }

    [Fact]
    public async Task Spawn_ResolverReturnsOfflineError_FailsLoud_DoesNotCreateLocally()
    {
        var resolver = new StubResolver(
            new DirectorTargetResult(null, null, "no Director on 'MACHINE_B' and the launcher could not start one"));
        var createCalled = false;
        var spawner = new MachineSessionSpawner(resolver, (directorId, endpoint, req, ct) =>
        {
            createCalled = true;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = "must-not-happen" }, null));
        });

        var (ok, dto, error, _) = await spawner.SpawnOnMachineAsync(
            "MACHINE_B", new NewSessionRequest { RepoPath = @"C:\repo" }, CancellationToken.None);

        Assert.False(ok);
        Assert.Null(dto);
        Assert.NotNull(error);
        Assert.Contains("could not start one", error!);
        // No local fallback: the create is never attempted when the machine cannot be resolved.
        Assert.False(createCalled);
    }

    [Fact]
    public async Task Spawn_CreateFails_ReportsTheCreateError_KeepsResolvedDirectorId()
    {
        var resolver = new StubResolver(new DirectorTargetResult("http://127.0.0.1:7901", "d-live", null));
        var spawner = new MachineSessionSpawner(resolver, (directorId, endpoint, req, ct) =>
            Task.FromResult<(bool, SessionDto?, string?)>((false, null, "director returned 500: boom")));

        var (ok, dto, error, directorId) = await spawner.SpawnOnMachineAsync(
            "MACHINE_A", new NewSessionRequest { RepoPath = @"C:\repo" }, CancellationToken.None);

        Assert.False(ok);
        Assert.Null(dto);
        Assert.Equal("director returned 500: boom", error);
        Assert.Equal("d-live", directorId);   // still carries the resolved Director for the run record
    }
}
