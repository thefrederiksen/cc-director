using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

[Collection("DirectorRoot")]
public sealed class KnownRepositoryEndpointTests : IAsyncLifetime
{
    private const string Token = "known-repository-endpoint-token";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cc-known-repository-" + Guid.NewGuid().ToString("N"));
    private string? _previousRoot;
    // Assigned by xUnit's asynchronous lifecycle before any test runs.
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        var instances = Path.Combine(_root, "instances");
        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            token: Token,
            authEnabled: true,
            instancesDirectory: instances,
            workListsPath: Path.Combine(_root, "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + _gateway.Port + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of a throwaway test root.
        }
    }

    [Fact]
    public async Task GetKnownRepositories_DirectorDisconnects_ReturnsEveryObservedRepository()
    {
        const string directorId = "known-repository-director";
        await using (var director = await FakeTunnelDirector.StartAsync(
                         _gateway, Token, directorId, "SOREN_NORTH"))
        {
            var sessions = Enumerable.Range(1, 8)
                .Select(number => new SessionDto
                {
                    SessionId = "session-" + number,
                    Name = "Session " + number,
                    RepoName = "Repository " + number,
                    RepoPath = @"D:\Repositories\repository-" + number,
                    Agent = "RawCli",
                    CurrentModel = "configured-model",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-number),
                    LastActivityAt = DateTime.UtcNow,
                    ActivityState = "Working",
                    Status = "Running",
                })
                .ToArray();
            await director.PushSnapshotAsync(sessions);
        }

        // The tunnel is disconnected before the read. A successful complete response can only have come
        // from the Gateway's durable catalog rather than from a Director repository-list command.
        using var response = await _http.GetAsync(
            "directors/" + directorId + "/known-repositories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<KnownRepositoryDto>>();
        Assert.NotNull(rows);
        Assert.Equal(8, rows.Count);
        Assert.Contains(rows, row => row.Path.EndsWith("repository-8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetKnownRepositories_UnknownDirector_ReturnsNotFound()
    {
        using var response = await _http.GetAsync("directors/missing/known-repositories");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
