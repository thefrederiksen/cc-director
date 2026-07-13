using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission (the cut): the Director's self-service REST slice is deleted from the loopback
/// Control API. The saved-document/registry READS survive as tunnel verbs whose cores live in
/// <see cref="CatalogReadExecutor"/> (claude-sessions repo filter, handovers repo filter), so this asserts
/// those cores directly against a seeded CC_DIRECTOR_ROOT / CC_VAULT_PATH - the same real read logic the old
/// REST routes ran, now over the surviving code. It also keeps the Control API info handoff to the
/// SessionManager (CC_DIRECTOR_API / CC_DIRECTOR_ID injection source), which is host lifecycle, not a route.
/// Runs a real ControlApiHost on an ephemeral port with CC_DIRECTOR_ROOT + CC_VAULT_PATH redirected to a temp
/// dir. In the "DirectorRoot" collection (serializes root-touching tests).
///
/// DELETED at the cut (no surviving surface - see the report/escalation): the self-service WRITE + overview
/// operations POST /repos (repo-add), PATCH /repos (repo-rename), POST /handovers (handover-create),
/// DELETE /handovers (handover-delete), and GET /repos/overview (repos-overview). Post-cut there is no
/// Director REST route, no tunnel verb (none of the executors declare these verbs), no Gateway route, and no
/// client caller for any of them, so the tests that exercised them are removed as deleted machinery.
/// </summary>
[Collection("DirectorRoot")]
public sealed class RestApiSelfServiceEndpointsTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _prevVault;
    private string _repoA = null!;            // Initialized in InitializeAsync
    private string _repoB = null!;            // Initialized in InitializeAsync
    private ControlApiHost _host = null!;     // Initialized in InitializeAsync
    private SessionManager _sm = null!;       // Initialized in InitializeAsync
    private RepositoryRegistry _registry = null!; // Initialized in InitializeAsync
    private HttpClient _client = null!;       // Initialized in InitializeAsync

    public RestApiSelfServiceEndpointsTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _prevVault = Environment.GetEnvironmentVariable("CC_VAULT_PATH");
        _root = Path.Combine(Path.GetTempPath(), "ccd-selfsvc-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Environment.SetEnvironmentVariable("CC_VAULT_PATH", Path.Combine(_root, "vault"));
    }

    public async Task InitializeAsync()
    {
        _repoA = Path.Combine(_root, "repos", "repoA");
        _repoB = Path.Combine(_root, "repos", "repoB");
        Directory.CreateDirectory(_repoA);
        Directory.CreateDirectory(_repoB);

        _registry = new RepositoryRegistry();
        _registry.Load();
        _registry.TryAdd(_repoA);

        // One pre-existing handover referencing repoA (for the ?repo= filter + overview count).
        var handoverDir = CcStorage.VaultHandovers();
        Directory.CreateDirectory(handoverDir);
        File.WriteAllText(Path.Combine(handoverDir, "20260601_0900_seeded-handover.md"),
            "---\n" +
            "session_name: Seeded Session\n" +
            "repositories:\n" +
            $"  - path: {_repoA}\n" +
            "---\n\n" +
            "# Seeded handover body\n");

        // One workspace-history entry linking a (fake) Claude session to repoA, so
        // /claude-sessions and /repos/overview have repo-keyed history to aggregate.
        new SessionHistoryStore().Save(new SessionHistoryEntry
        {
            Id = Guid.NewGuid(),
            RepoPath = _repoA,
            ClaudeSessionId = "selfsvc-test-claude-session",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            LastUsedAt = DateTimeOffset.UtcNow.AddHours(-1),
            FirstPromptSnippet = "selfsvc seeded prompt",
        });

        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            useEphemeralPort: true, repositoryRegistry: _registry);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        Environment.SetEnvironmentVariable("CC_VAULT_PATH", _prevVault);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ===== Control API info handoff (CC_DIRECTOR_API / CC_DIRECTOR_ID source) =====

    [Fact]
    public void Host_start_publishes_control_api_info_to_session_manager()
    {
        Assert.Equal($"http://127.0.0.1:{_host.Port}", _sm.ControlApiBaseUrl);
        Assert.Equal(_host.DirectorId, _sm.DirectorId);
    }

    // ===== GET /handovers?repo= (handovers-list verb core) =====

    // Gateway Cleanup (the cut): GET /handovers is deleted from the Director's Control API; the saved-document
    // read is now the handovers-list tunnel verb, whose core is CatalogReadExecutor.HandoversList. That core
    // still applies the ?repo= frontmatter filter (normalized, case- and trailing-slash-insensitive). Asserted
    // directly against the real core over the seeded vault, so the filter's strength is fully preserved.
    // (Note: the Gateway's /directors/{id}/handovers route does not forward a repo filter today, so the filter
    // is proven where it lives - in the verb core - not through that route.)
    [Fact]
    public void Handovers_repo_filter_matches_frontmatter_repositories()
    {
        var forA = ReadHandovers(_repoA);
        Assert.Contains(forA, h => h.Title == "Seeded handover");

        // Trailing slash + different casing must still match.
        var forASlash = ReadHandovers(_repoA.ToUpperInvariant() + "\\");
        Assert.Contains(forASlash, h => h.Title == "Seeded handover");

        var forB = ReadHandovers(_repoB);
        Assert.DoesNotContain(forB, h => h.Title == "Seeded handover");
    }

    // ===== GET /claude-sessions?repo= (claude-sessions verb core) =====

    // Gateway Cleanup (the cut): GET /claude-sessions is deleted from the Director's Control API; the resumable-
    // session read is now the claude-sessions tunnel verb, whose core is CatalogReadExecutor.ClaudeSessions.
    // That core still applies the ?repo= filter over the merged workspace-history + Claude-metadata list.
    // Asserted directly against the real core over the seeded history, so the filter's strength is preserved.
    [Fact]
    public void Claude_sessions_repo_filter_includes_only_matching_repo()
    {
        var forA = ReadClaudeSessions(_repoA);
        var entry = Assert.Single(forA);
        Assert.Equal("selfsvc-test-claude-session", entry.ClaudeSessionId);
        Assert.Equal("selfsvc seeded prompt", entry.Summary);

        var forB = ReadClaudeSessions(_repoB);
        Assert.Empty(forB);
    }

    // ===== helpers =====

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DirectorCommand Command(string verb, object? payload) => new()
    {
        CommandId = "cmd-selfsvc",
        Verb = verb,
        SessionId = "",
        PayloadJson = payload is null ? "" : JsonSerializer.Serialize(payload, Json),
    };

    private static List<T> ReadBody<T>(DirectorCommandResult result)
    {
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        return JsonSerializer.Deserialize<List<T>>(result.BodyJson ?? "[]", Json) ?? new List<T>();
    }

    // Invoke the real handovers-list verb core with an optional repo filter (the payload the tunnel carries).
    private static List<HandoverDto> ReadHandovers(string? repo) =>
        ReadBody<HandoverDto>(CatalogReadExecutor.HandoversList(Command("handovers-list", new HandoversListRequest { Repo = repo })));

    // Invoke the real claude-sessions verb core with an optional repo filter.
    private static List<ClaudeSessionDto> ReadClaudeSessions(string? repo) =>
        ReadBody<ClaudeSessionDto>(CatalogReadExecutor.ClaudeSessions(Command("claude-sessions", new ClaudeSessionsRequest { Repo = repo })));
}
