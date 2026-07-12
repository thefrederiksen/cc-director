using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (Worker R2): parity tests for the CATALOG / director-level READ verbs
/// moved onto the tunnel command surface (<see cref="CatalogReadExecutor"/>). Each verb is exercised through
/// the real <see cref="SessionCommandExecutor.DispatchAsync"/> path (verb map -&gt; area -&gt; core), the same
/// way the re-pointed REST route and the Gateway stream down-channel reach it, so the core is asserted exactly
/// once for both callers. Covers the per-session guards for <c>git-status</c> (invalid id -&gt; BadRequest,
/// missing session -&gt; NotFound, existing session -&gt; a 200 snapshot), the always-200 director-level reads
/// (<c>coaching-categories</c>, <c>claude-sessions</c>, <c>interrupted-list</c>, <c>fs-list</c> at the drive
/// roots), and the one preserved try/catch (<c>fs-list</c> on a bad path -&gt; BadRequest). Reuses the
/// buffer-only embedded-session harness from the sibling SessionCommandExecutor tests.
/// </summary>
[Collection("DirectorRoot")]
public sealed class CatalogReadExecutorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (SessionManager sm, Session session, ExecuteActionTestBackend backend) NewSession()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        return (sm, session, backend);
    }

    private static DirectorCommand Cmd(string verb, string sid = "", string payloadJson = "") =>
        new() { CommandId = "r2", Verb = verb, SessionId = sid, PayloadJson = payloadJson };

    // ---------- git-status ----------

    [Fact]
    public async Task DispatchAsync_GitStatus_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("git-status", "not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_GitStatus_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("git-status", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_GitStatus_ExistingSession_ReturnsOkWithSnapshot()
    {
        // The temp path is not a git repo, so GitSnapshotAsync owns the "not a repo" domain state and returns
        // a snapshot - still a 200 (the source route had no guard/try-catch of its own). The result is Ok with
        // a deserializable GitSnapshot body.
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("git-status", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("r2", result.CommandId);
            var snap = JsonSerializer.Deserialize<GitSnapshot>(result.BodyJson ?? "", Json);
            Assert.NotNull(snap);
            Assert.False(string.IsNullOrEmpty(snap!.Status));
        }
        finally { sm.Dispose(); }
    }

    // ---------- coaching-categories ----------

    [Fact]
    public async Task DispatchAsync_CoachingCategories_ReturnsOkWithAssistantAndCoach()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("coaching-categories"));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var cats = JsonSerializer.Deserialize<List<CoachingCategoryDto>>(result.BodyJson ?? "", Json);
            Assert.NotNull(cats);
            Assert.Equal(2, cats!.Count);
            Assert.Contains(cats, c => c.Key == "assistant");
            Assert.Contains(cats, c => c.Key == "coach");
        }
        finally { sm.Dispose(); }
    }

    // ---------- claude-sessions ----------

    [Fact]
    public async Task DispatchAsync_ClaudeSessions_NoFilter_ReturnsOkWithList()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("claude-sessions"));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dtos = JsonSerializer.Deserialize<List<ClaudeSessionDto>>(result.BodyJson ?? "", Json);
            Assert.NotNull(dtos);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_ClaudeSessions_RepoFilter_ReturnsOnlyMatchingRepo()
    {
        // A repo path that matches nothing on this machine yields an empty (but valid) list - the ?repo=
        // filter rides in the payload, exactly as the route's query-string argument did.
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var payload = JsonSerializer.Serialize(new ClaudeSessionsRequest { Repo = @"Z:\no\such\repo\r2-parity" }, Json);
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("claude-sessions", payloadJson: payload));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dtos = JsonSerializer.Deserialize<List<ClaudeSessionDto>>(result.BodyJson ?? "", Json);
            Assert.NotNull(dtos);
            Assert.Empty(dtos!);
        }
        finally { sm.Dispose(); }
    }

    // ---------- interrupted-list ----------

    [Fact]
    public async Task DispatchAsync_InterruptedList_ReturnsOkWithList()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("interrupted-list"));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var pending = JsonSerializer.Deserialize<List<DirectorCrashJournalData>>(result.BodyJson ?? "", Json);
            Assert.NotNull(pending);
        }
        finally { sm.Dispose(); }
    }

    // ---------- fs-list ----------

    [Fact]
    public async Task DispatchAsync_FsList_NullPath_ReturnsDriveRoots()
    {
        // A null/absent path lists the drive roots: CurrentPath is null and every entry is a drive.
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("fs-list"));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var listing = JsonSerializer.Deserialize<DirectoryListingDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(listing);
            Assert.Null(listing!.CurrentPath);
            Assert.NotEmpty(listing.Entries);
            Assert.All(listing.Entries, e => Assert.True(e.IsDrive));
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_FsList_MissingDirectory_ReturnsBadRequest()
    {
        // The source route wrapped ListDirectory in a try/catch that turned a missing directory into a 400;
        // that preserved try/catch surfaces here as a BadRequest with the message.
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var payload = JsonSerializer.Serialize(new FsListRequest { Path = @"Z:\no\such\directory\r2-parity" }, Json);
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("fs-list", payloadJson: payload));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.False(string.IsNullOrEmpty(result.Error));
        }
        finally { sm.Dispose(); }
    }

    // ---------- facts (Gateway Cleanup Phase 0 wave 3: needs the Director version via SessionCommandServices) ----------

    [Fact]
    public async Task DispatchAsync_Facts_ReturnsOkWithDirectorIdAndVersionFromServices()
    {
        // facts is director-level (no session), always a 200. The Director version rides in the services -
        // the one dependency the tunnel command surface did not carry before wave 3 - and is stamped into the
        // DTO exactly as the REST route stamped ControlApiHost._version.
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-facts", Cmd("facts"),
                new SessionCommandServices { DirectorVersion = "9.9.9-test" });

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("r2", result.CommandId);
            var dto = JsonSerializer.Deserialize<DirectorFactsDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal("dir-facts", dto!.DirectorId);
            Assert.Equal("9.9.9-test", dto.Version);
            Assert.NotNull(dto.Launcher);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Facts_NoVersionInServices_ReturnsOkWithEmptyVersion()
    {
        // No services (or no version) is not an error: facts still lists tools/launcher, version just falls back
        // to empty - the same additive-null tolerance the other services fields have.
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-facts", Cmd("facts"));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<DirectorFactsDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(string.Empty, dto!.Version);
        }
        finally { sm.Dispose(); }
    }

    // ---------- repos-list (Gateway Cleanup Phase 0 wave 3: needs the live registry via SessionCommandServices) ----------

    [Fact]
    public async Task DispatchAsync_ReposList_NoRegistryInServices_ReturnsOkWithEmptyArray()
    {
        // repos-list is director-level (no session), always a 200. With no registry wired (no services) the
        // core lists nothing - an empty array - exactly as the REST route returned when no registry was set.
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("repos-list"));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("r2", result.CommandId);
            var repos = JsonSerializer.Deserialize<List<RepositoryDto>>(result.BodyJson ?? "", Json);
            Assert.NotNull(repos);
            Assert.Empty(repos!);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_ReposList_WithRegistry_ReturnsRegisteredRepos()
    {
        // The live registry rides in the services - the one dependency the tunnel command surface did not carry
        // before wave 3 - and the core reads the same instance the REST route read at Map time.
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var registryFile = Path.Combine(Path.GetTempPath(), "ccd-repos-" + Guid.NewGuid().ToString("N") + ".json");
        var repoDir = Path.Combine(Path.GetTempPath(), "ccd-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDir);
        try
        {
            var registry = new Core.Configuration.RepositoryRegistry(registryFile);
            Assert.True(registry.TryAdd(repoDir));

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("repos-list"),
                new SessionCommandServices { Repositories = registry });

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var repos = JsonSerializer.Deserialize<List<RepositoryDto>>(result.BodyJson ?? "", Json);
            Assert.NotNull(repos);
            Assert.Contains(repos!, r => string.Equals(
                Path.GetFullPath(r.Path).TrimEnd('\\', '/'),
                Path.GetFullPath(repoDir).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            sm.Dispose();
            try { if (File.Exists(registryFile)) File.Delete(registryFile); } catch { /* best effort */ }
            try { if (Directory.Exists(repoDir)) Directory.Delete(repoDir, true); } catch { /* best effort */ }
        }
    }

    // ---------- handovers-list / handovers-content (Gateway Cleanup Phase 0 Wave 4a: the saved-handover
    // DOCUMENT reads, DISTINCT from the per-session "handover" info verb). CC_DIRECTOR_ROOT is pinned into a
    // temp dir so the vault handover folder these verbs scan is a controlled, empty-then-seeded folder that
    // never touches the user's real vault. In the "DirectorRoot" collection (serializes root-touching tests).

    [Fact]
    public async Task DispatchAsync_HandoversList_EmptyFolder_ReturnsOkWithNoDocuments()
    {
        var (root, prev) = PinRoot();
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("handovers-list"));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("r2", result.CommandId);
            var dtos = JsonSerializer.Deserialize<List<HandoverDto>>(result.BodyJson ?? "", Json);
            Assert.NotNull(dtos);
            Assert.Empty(dtos!);
        }
        finally { sm.Dispose(); RestoreRoot(root, prev); }
    }

    [Fact]
    public async Task DispatchAsync_HandoversList_SeededDocument_ReturnsItAndHonorsRepoFilter()
    {
        var (root, prev) = PinRoot();
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        // The frontmatter repo path only round-trips through the scanner when the directory actually exists
        // (HandoverScanner keeps only on-disk repo paths), so the seeded repo is a real temp directory.
        var repoDir = Path.Combine(Path.GetTempPath(), "ccd-hv-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDir);
        try
        {
            HandoverScanner.WriteNew("Wave 4a handover", "the saved body", new[] { repoDir }, "Wave 4a - Worker");

            // No filter: the seeded document is listed.
            var all = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("handovers-list"));
            Assert.Equal(DirectorCommandStatus.Ok, all.Status);
            var allDtos = JsonSerializer.Deserialize<List<HandoverDto>>(all.BodyJson ?? "", Json);
            Assert.NotNull(allDtos);
            Assert.Contains(allDtos!, h => h.Title == "Wave 4a handover");

            // Matching repo filter: still listed.
            var matchPayload = JsonSerializer.Serialize(new HandoversListRequest { Repo = repoDir }, Json);
            var match = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("handovers-list", payloadJson: matchPayload));
            var matchDtos = JsonSerializer.Deserialize<List<HandoverDto>>(match.BodyJson ?? "", Json);
            Assert.NotNull(matchDtos);
            Assert.Contains(matchDtos!, h => h.Title == "Wave 4a handover");

            // Non-matching repo filter: excluded (the ?repo= filter rides in the payload, as before).
            var missPayload = JsonSerializer.Serialize(new HandoversListRequest { Repo = @"Z:\no\such\repo\wave4a" }, Json);
            var miss = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("handovers-list", payloadJson: missPayload));
            var missDtos = JsonSerializer.Deserialize<List<HandoverDto>>(miss.BodyJson ?? "", Json);
            Assert.NotNull(missDtos);
            Assert.DoesNotContain(missDtos!, h => h.Title == "Wave 4a handover");
        }
        finally
        {
            sm.Dispose();
            RestoreRoot(root, prev);
            try { if (Directory.Exists(repoDir)) Directory.Delete(repoDir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task DispatchAsync_HandoversContent_BlankPath_ReturnsBadRequest()
    {
        // The route's own guard: a null / blank path is a 400 before any file access.
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var payload = JsonSerializer.Serialize(new HandoverContentRequest { Path = "  " }, Json);
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("handovers-content", payloadJson: payload));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.False(string.IsNullOrEmpty(result.Error));
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_HandoversContent_MissingFileInsideFolder_ReturnsNotFound()
    {
        // A path INSIDE the handover folder that does not exist: the source route wrapped ReadContent in a
        // try/catch that turned a FileNotFoundException into a 404; that preserved try/catch surfaces here as a
        // NotFound. (A path OUTSIDE the folder is instead the UnauthorizedAccessException -> BadRequest branch.)
        var (root, prev) = PinRoot();
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var missing = Path.Combine(CcStorage.VaultHandovers(), "no-such-handover-wave4a.md");
            var payload = JsonSerializer.Serialize(new HandoverContentRequest { Path = missing }, Json);
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("handovers-content", payloadJson: payload));

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); RestoreRoot(root, prev); }
    }

    [Fact]
    public async Task DispatchAsync_HandoversContent_PathOutsideFolder_ReturnsBadRequest()
    {
        // A path outside the handover folder is the route's UnauthorizedAccessException -> BadRequest branch,
        // preserved verbatim by the lifted core.
        var (root, prev) = PinRoot();
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var payload = JsonSerializer.Serialize(new HandoverContentRequest { Path = @"Z:\outside\handover-wave4a.md" }, Json);
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("handovers-content", payloadJson: payload));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); RestoreRoot(root, prev); }
    }

    [Fact]
    public async Task DispatchAsync_HandoversContent_SeededDocument_ReturnsContent()
    {
        var (root, prev) = PinRoot();
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var path = HandoverScanner.WriteNew("Wave 4a content", "the exact saved body", null, null);

            var payload = JsonSerializer.Serialize(new HandoverContentRequest { Path = path }, Json);
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Cmd("handovers-content", payloadJson: payload));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<HandoverContentDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(path, dto!.Path);
            Assert.Contains("the exact saved body", dto.Content);
        }
        finally { sm.Dispose(); RestoreRoot(root, prev); }
    }

    // Pin the vault into a fresh temp dir so the vault handover folder these verbs scan is a controlled,
    // empty-then-seeded folder that never touches the user's real vault. The vault path is resolved from
    // CC_VAULT_PATH first (this machine has it set), so that is the variable to override - CC_DIRECTOR_ROOT
    // alone would not redirect the vault. Returns the pinned root and the previous value so RestoreRoot can
    // put it back and delete the temp tree.
    private static (string root, string? prev) PinRoot()
    {
        var prev = Environment.GetEnvironmentVariable("CC_VAULT_PATH");
        var root = Path.Combine(Path.GetTempPath(), "ccd-handover-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_VAULT_PATH", Path.Combine(root, "vault"));
        return (root, prev);
    }

    private static void RestoreRoot(string root, string? prev)
    {
        Environment.SetEnvironmentVariable("CC_VAULT_PATH", prev);
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }
}
