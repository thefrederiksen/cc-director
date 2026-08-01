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
/// CUT RESTORATION (SB-4a): the self-service WRITE + overview operations were briefly over-deleted, then
/// restored as tunnel verbs (Architect ruling). Their cores now live in the executors - repo-add / repo-rename /
/// handover-create / handover-delete in <see cref="SessionWriteExecutor"/> and repos-overview in
/// <see cref="CatalogReadExecutor"/> - reached over the tunnel via /directors/{id}/repos|repos/overview|handovers.
/// The Gateway ROUTE wiring + faithful status mapping (201/200/400/404/502) is proven in
/// <c>TunnelReposHandoverMgmtProofTests</c>; here the real core LOGIC (directory-existence, registry mutation,
/// added-flag, handover write/delete, overview aggregation) is asserted directly against a seeded
/// CC_DIRECTOR_ROOT / CC_VAULT_PATH - the same logic the old REST lambdas ran, now over the surviving cores.
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
        _client = DirectorTestClient.Admin(port);
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

    // ===== repo-add verb core (POST /repos) =====

    [Fact]
    public void RepoAdd_newRepo_added_true_andReturnsTheEntry()
    {
        var result = SessionWriteExecutor.RepoAdd(Command("repo-add", new RepoAddRequest { Path = _repoB, Name = "Repo B" }), _registry);
        var body = ReadOne<RepoAddResponse>(result);
        Assert.True(body.Added);
        Assert.NotNull(body.Repo);
        Assert.Equal("Repo B", body.Repo!.Name);
        Assert.Contains(_registry.Repositories, r => string.Equals(r.Path, _repoB, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RepoAdd_alreadyPresent_added_false()
    {
        // _repoA was added in InitializeAsync.
        var result = SessionWriteExecutor.RepoAdd(Command("repo-add", new RepoAddRequest { Path = _repoA }), _registry);
        var body = ReadOne<RepoAddResponse>(result);
        Assert.False(body.Added);
    }

    [Fact]
    public void RepoAdd_directoryDoesNotExist_isBadRequest()
    {
        var result = SessionWriteExecutor.RepoAdd(Command("repo-add", new RepoAddRequest { Path = Path.Combine(_root, "does-not-exist") }), _registry);
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    [Fact]
    public void RepoAdd_blankPath_isBadRequest()
    {
        var result = SessionWriteExecutor.RepoAdd(Command("repo-add", new RepoAddRequest { Path = "" }), _registry);
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    // ===== repo-rename verb core (PATCH /repos) =====

    [Fact]
    public void RepoRename_registeredRepo_renamesAndReturnsTheEntry()
    {
        var result = SessionWriteExecutor.RepoRename(Command("repo-rename", new RepoRenameRequest { Path = _repoA, Name = "Renamed A" }), _registry);
        var body = ReadOne<RepositoryDto>(result);
        Assert.Equal("Renamed A", body.Name);
    }

    [Fact]
    public void RepoRename_unregisteredPath_isNotFound()
    {
        var result = SessionWriteExecutor.RepoRename(Command("repo-rename", new RepoRenameRequest { Path = _repoB, Name = "x" }), _registry);
        Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
    }

    [Fact]
    public void RepoRename_blankName_isBadRequest()
    {
        var result = SessionWriteExecutor.RepoRename(Command("repo-rename", new RepoRenameRequest { Path = _repoA, Name = "" }), _registry);
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    // ===== repos-overview verb core (GET /repos/overview) =====

    [Fact]
    public void ReposOverview_aggregatesHandoverAndHistoryForTheRepo()
    {
        var result = CatalogReadExecutor.ReposOverview(_sm, _registry);
        var overview = ReadList<RepoOverviewDto>(result);
        var a = Assert.Single(overview, o => string.Equals(o.Path, _repoA, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, a.HandoverCount);           // one seeded handover references repoA
        Assert.Equal(1, a.HistorySessionCount);      // one seeded workspace-history entry
        Assert.True(a.PathExists);                    // the directory was created
    }

    // ===== handover-create verb core (POST /handovers) =====

    [Fact]
    public void HandoverCreate_writesTheDocument_andItAppearsInTheList()
    {
        var result = SessionWriteExecutor.HandoverCreate(Command("handover-create", new HandoverCreateRequest
        {
            Title = "Created In Test",
            Content = "# created body",
            RepoPaths = new List<string> { _repoA },
        }));
        // HandoverScanner derives the title from the filename slug (sentence-cased), so the round-tripped title
        // is matched case-insensitively - exactly as the old REST route returned it from Parse.
        var body = ReadOne<HandoverDto>(result);
        Assert.Equal("Created In Test", body.Title, ignoreCase: true);

        // It is now a real saved document the list read finds.
        var listed = ReadHandovers(_repoA);
        Assert.Contains(listed, h => string.Equals(h.Title, "Created In Test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HandoverCreate_blankTitle_isBadRequest()
    {
        var result = SessionWriteExecutor.HandoverCreate(Command("handover-create", new HandoverCreateRequest { Title = "", Content = "x" }));
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    [Fact]
    public void HandoverCreate_blankContent_isBadRequest()
    {
        var result = SessionWriteExecutor.HandoverCreate(Command("handover-create", new HandoverCreateRequest { Title = "t", Content = "" }));
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    // ===== handover-delete verb core (DELETE /handovers) =====

    [Fact]
    public void HandoverDelete_existingDocument_removesIt()
    {
        // Create one, then delete it by its real path.
        var created = ReadOne<HandoverDto>(SessionWriteExecutor.HandoverCreate(Command("handover-create", new HandoverCreateRequest
        {
            Title = "To Delete",
            Content = "# body",
            RepoPaths = new List<string> { _repoA },
        })));

        var result = SessionWriteExecutor.HandoverDelete(Command("handover-delete", new RepoDeleteRequest { Path = created.Path }));
        var body = ReadOne<HandoverDeleteResponse>(result);
        Assert.True(body.Removed);
        Assert.DoesNotContain(ReadHandovers(_repoA), h => h.Title == "To Delete");
    }

    [Fact]
    public void HandoverDelete_missingFile_isNotFound()
    {
        var result = SessionWriteExecutor.HandoverDelete(Command("handover-delete",
            new RepoDeleteRequest { Path = Path.Combine(CcStorage.VaultHandovers(), "20260101_0000_never-existed.md") }));
        Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
    }

    [Fact]
    public void HandoverDelete_blankPath_isBadRequest()
    {
        var result = SessionWriteExecutor.HandoverDelete(Command("handover-delete", new RepoDeleteRequest { Path = "" }));
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    // ===== helpers =====

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static T ReadOne<T>(DirectorCommandResult result)
    {
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        return JsonSerializer.Deserialize<T>(result.BodyJson ?? "{}", Json)!;
    }

    private static List<T> ReadList<T>(DirectorCommandResult result)
    {
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        return JsonSerializer.Deserialize<List<T>>(result.BodyJson ?? "[]", Json) ?? new List<T>();
    }

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
