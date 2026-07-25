using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Cockpit "Interrupted sessions" aggregator (issue #212 W3): GET /interrupted fans out
/// to every Director for the crash journals left on its machine, flattens them to one row per
/// recoverable session, dedupes journals reported by sibling Directors that share a machine
/// dir, and routes a dismiss/restore to the reporting Director.
///
/// Gateway Cleanup mission (the cut): the fan-out and the director-level dismiss/remove/restore
/// verbs ride THE TUNNEL now - the Gateway never HTTP-dials a Director. Each Director registers
/// UNREACHABLE and answers its verbs over the stream (interrupted-list / interrupted-dismiss /
/// interrupted-remove for the journal work, and create + patch for the restore continuation), so
/// a working result proves the tunnel by construction. The routing + dedupe assertions are
/// preserved: they are exercised by pushing/answering from real tunnel-connected Directors.
/// </summary>
public sealed class GatewayInterruptedTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private readonly List<TunnelFake> _fakes = new();
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        foreach (var f in _fakes) await f.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    [Fact]
    public async Task Aggregates_journals_into_one_row_per_session()
    {
        var journal = new CrashJournalDto
        {
            DirectorId = "dead-1", Pid = 9001, MachineName = "MACHINE_A", User = "alice",
            LastUpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            Sessions =
            {
                new CrashJournalSessionDto { SessionId = "s1", Name = "alpha", RepoPath = "/repo/a" },
                new CrashJournalSessionDto { SessionId = "s2", Name = "beta", RepoPath = "/repo/b" },
            },
        };
        await StartFake("MACHINE_A", new[] { journal });

        var rows = await GetInterrupted();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.SessionId == "s1" && r.Name == "alpha" && r.DeadDirectorId == "dead-1");
        Assert.Contains(rows, r => r.SessionId == "s2" && r.MachineName == "MACHINE_A");
        Assert.All(rows, r => Assert.Equal(9001, r.DeadPid));
    }

    [Fact]
    public async Task Dedupes_a_journal_reported_by_two_sibling_directors()
    {
        // Two live Directors on the same machine share the crash-journal dir, so both report
        // the same dead journal over the tunnel. The aggregator must list its sessions once.
        var journal = new CrashJournalDto
        {
            DirectorId = "dead-1", Pid = 9001, MachineName = "MACHINE_A",
            Sessions = { new CrashJournalSessionDto { SessionId = "s1", Name = "alpha", RepoPath = "/r" } },
        };
        await StartFake("MACHINE_A", new[] { journal });
        await StartFake("MACHINE_A", new[] { journal });

        var rows = await GetInterrupted();

        Assert.Single(rows);
    }

    [Fact]
    public async Task Empty_when_no_director_has_a_dirty_journal()
    {
        await StartFake("MACHINE_A", Array.Empty<CrashJournalDto>());
        Assert.Empty(await GetInterrupted());
    }

    [Fact]
    public async Task Dismiss_routes_to_the_reporting_director()
    {
        var journal = new CrashJournalDto
        {
            DirectorId = "dead-1", Pid = 9001, MachineName = "MACHINE_A",
            Sessions = { new CrashJournalSessionDto { SessionId = "s1", Name = "alpha", RepoPath = "/r" } },
        };
        var fake = await StartFake("MACHINE_A", new[] { journal });
        var rows = await GetInterrupted();
        var reportedBy = rows[0].ReportedByDirectorId;

        var resp = await _http.DeleteAsync($"interrupted/dead-1/9001?via={reportedBy}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // The interrupted-dismiss verb rode the tunnel to the reporting Director with the journal key.
        Assert.Equal(("dead-1", 9001), fake.LastDismiss);
    }

    [Fact]
    public async Task Dismiss_without_via_is_rejected()
    {
        await StartFake("MACHINE_A", Array.Empty<CrashJournalDto>());
        var resp = await _http.DeleteAsync("interrupted/dead-1/9001");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- restore (issue #212 W4) ----

    [Fact]
    public async Task Restore_creates_a_named_continuation_and_cleans_the_journal()
    {
        var journal = new CrashJournalDto
        {
            DirectorId = "dead-1", Pid = 9001, MachineName = "MACHINE_A",
            LastUpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            Sessions =
            {
                new CrashJournalSessionDto
                {
                    SessionId = "s1", Name = "alpha work", RepoPath = "/repo/a",
                    ClaudeSessionId = "claude-abc",
                },
            },
        };
        var fake = await StartFake("MACHINE_A", new[] { journal });

        var resp = await _http.PostAsJsonAsync("interrupted/dead-1/9001/restore",
            new RestoreInterruptedRequest { SessionId = "s1", Via = fake.DirectorId });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<RestoreInterruptedResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Restored);
        Assert.Equal(fake.CreatedSessionId, body.TargetSession?.SessionId);
        Assert.True(body.JournalCleaned);

        // The continuation was created over the tunnel in the dead session's repo, seeded with a
        // context that names the predecessor, its repo, and the prior Claude transcript.
        Assert.NotNull(fake.LastCreate);
        Assert.Equal("/repo/a", fake.LastCreate!.RepoPath);
        Assert.Contains("RESTORED", fake.LastCreate.PrePrompt);
        Assert.Contains("alpha work", fake.LastCreate.PrePrompt);
        Assert.Contains("claude-abc", fake.LastCreate.PrePrompt!);

        // Renamed to the dead session's name; the restored row left the journal.
        Assert.Equal("alpha work", fake.LastPatchName);
        Assert.Equal(("dead-1", 9001, "s1"), fake.LastSessionRemoval);
    }

    [Fact]
    public async Task Restore_targets_an_explicit_director_but_cleans_via_the_reporter()
    {
        var journal = new CrashJournalDto
        {
            DirectorId = "dead-1", Pid = 9001, MachineName = "MACHINE_A",
            Sessions = { new CrashJournalSessionDto { SessionId = "s1", Name = "alpha", RepoPath = "/r" } },
        };
        var reporter = await StartFake("MACHINE_A", new[] { journal });
        var target = await StartFake("MACHINE_A", Array.Empty<CrashJournalDto>());

        var resp = await _http.PostAsJsonAsync("interrupted/dead-1/9001/restore",
            new RestoreInterruptedRequest { SessionId = "s1", Via = reporter.DirectorId, ToDirectorId = target.DirectorId });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.NotNull(target.LastCreate);                          // session created on the target...
        Assert.Null(reporter.LastCreate);
        Assert.Equal(("dead-1", 9001, "s1"), reporter.LastSessionRemoval); // ...journal cleaned via the reporter
    }

    [Fact]
    public async Task Restore_unknown_session_is_404()
    {
        var journal = new CrashJournalDto
        {
            DirectorId = "dead-1", Pid = 9001, MachineName = "MACHINE_A",
            Sessions = { new CrashJournalSessionDto { SessionId = "s1", Name = "alpha", RepoPath = "/r" } },
        };
        var fake = await StartFake("MACHINE_A", new[] { journal });

        var resp = await _http.PostAsJsonAsync("interrupted/dead-1/9001/restore",
            new RestoreInterruptedRequest { SessionId = "ghost", Via = fake.DirectorId });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Null(fake.LastCreate);
    }

    [Fact]
    public async Task DismissSession_routes_to_the_reporting_director()
    {
        var journal = new CrashJournalDto
        {
            DirectorId = "dead-1", Pid = 9001, MachineName = "MACHINE_A",
            Sessions = { new CrashJournalSessionDto { SessionId = "s1", Name = "alpha", RepoPath = "/r" } },
        };
        var fake = await StartFake("MACHINE_A", new[] { journal });

        var resp = await _http.DeleteAsync($"interrupted/dead-1/9001/sessions/s1?via={fake.DirectorId}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(("dead-1", 9001, "s1"), fake.LastSessionRemoval);
    }

    [Fact]
    public async Task Restore_without_via_is_rejected()
    {
        await StartFake("MACHINE_A", Array.Empty<CrashJournalDto>());
        var resp = await _http.PostAsJsonAsync("interrupted/dead-1/9001/restore",
            new RestoreInterruptedRequest { SessionId = "s1" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- helpers ----

    private async Task<List<InterruptedSessionDto>> GetInterrupted()
    {
        var list = await _http.GetFromJsonAsync<List<InterruptedSessionDto>>("interrupted");
        // Restrict to journals our fakes serve (dead-journal ids are fixed - filter by those).
        return (list ?? new()).Where(r => r.DeadDirectorId.StartsWith("dead-")).ToList();
    }

    private async Task<TunnelFake> StartFake(string machine, CrashJournalDto[] journals)
    {
        var fake = new TunnelFake(machine, journals);
        fake.Director = await FakeTunnelDirector.StartAsync(_gateway, Token, fake.DirectorId, machine, fake.Dispatch);
        _fakes.Add(fake);
        return fake;
    }

    /// <summary>
    /// A tunnel-connected stand-in Director: it registers UNREACHABLE and answers the interrupted-*
    /// verbs plus the restore continuation verbs (create + patch) over the stream, capturing what the
    /// Gateway sent so the routing/dedupe/restore assertions hold exactly as before - now over the tunnel.
    /// </summary>
    private sealed class TunnelFake : IAsyncDisposable
    {
        public FakeTunnelDirector Director = null!;
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string MachineName { get; }

        public (string, int)? LastDismiss { get; private set; }
        public NewSessionRequest? LastCreate { get; private set; }
        public string? LastPatchName { get; private set; }
        public (string Dir, int Pid, string Sid)? LastSessionRemoval { get; private set; }
        public string CreatedSessionId { get; } = "new-" + Guid.NewGuid().ToString("N")[..8];

        private readonly CrashJournalDto[] _journals;

        public TunnelFake(string machine, CrashJournalDto[] journals)
        {
            MachineName = machine;
            _journals = journals;
        }

        public DirectorCommandResult Dispatch(DirectorCommand cmd)
        {
            switch (cmd.Verb)
            {
                case "interrupted-list":
                    return FakeTunnelDirector.Ok(_journals);
                case "interrupted-dismiss":
                {
                    var p = JsonNode.Parse(cmd.PayloadJson)!.AsObject();
                    LastDismiss = ((string)p["deadDirectorId"]!, (int)p["deadPid"]!);
                    return FakeTunnelDirector.Ok(new { dismissed = true });
                }
                case "interrupted-remove":
                {
                    var p = JsonNode.Parse(cmd.PayloadJson)!.AsObject();
                    LastSessionRemoval = ((string)p["deadDirectorId"]!, (int)p["deadPid"]!, (string)p["sessionId"]!);
                    return FakeTunnelDirector.Ok(new { removed = true });
                }
                case "create":
                {
                    LastCreate = JsonSerializer.Deserialize<NewSessionRequest>(cmd.PayloadJson, FakeTunnelDirector.WebJson);
                    return FakeTunnelDirector.Ok(new SessionDto { SessionId = CreatedSessionId, RepoPath = LastCreate?.RepoPath ?? "" });
                }
                case "patch":
                {
                    var req = JsonSerializer.Deserialize<SessionUpdateRequest>(cmd.PayloadJson, FakeTunnelDirector.WebJson);
                    LastPatchName = req?.Name;
                    return FakeTunnelDirector.Ok(new SessionDto { SessionId = cmd.SessionId, Name = req?.Name });
                }
                default:
                    return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
            }
        }

        public ValueTask DisposeAsync() => Director is null ? ValueTask.CompletedTask : Director.DisposeAsync();
    }
}
