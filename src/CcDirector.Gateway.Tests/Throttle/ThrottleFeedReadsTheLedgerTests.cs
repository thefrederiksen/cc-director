using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using SessionHistoryStore = CcDirector.Gateway.History.SessionHistoryStore;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Throttle;
using Xunit;

namespace CcDirector.Gateway.Tests.Throttle;

/// <summary>
/// THE FEED SERVES THE LEDGER FIGURE, END TO END, ON A HOSTED GATEWAY (mission "Clean up Your Throttle",
/// rulings R1, R7, R9 and R17). A real <see cref="GatewayHost"/> in hosted mode over a throwaway storage
/// root, two enrolled accounts, submissions pushed through the production ledger ingress
/// (<c>POST /activity-events/batch</c>), and <c>GET /stats/data</c> read through the real auth gate.
///
/// What is asserted is the NUMBERS, shaped exactly as the ledger records them: a terminal-typed turn
/// (null send source, present origin) is in; agent traffic is out by record and reported beside the figure;
/// a submission with no origin is out and disclosed as a count; one account's rows are invisible to the
/// other; the window is stated and honoured; and the repository split and the session-origin block read
/// the caller's own session history. A body that merely has the right shape would satisfy none of it.
///
/// The assembly runs sequentially (TestParallelization), so toggling CC_GATEWAY_HOSTED and the storage root
/// here is safe; both are restored in DisposeAsync.
/// </summary>
public sealed class ThrottleFeedReadsTheLedgerTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token-throttle-feed";
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _keyA = "";
    private string _keyB = "";
    private string _keyUnbound = "";
    private TenantId _tenantA;
    private string? _priorHosted;
    private string? _priorRoot;
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-throttle-feed-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-throttle-feed-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            promptLogPath: Path.Combine(_instancesDir, "prompt-log"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        _keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;
        _tenantA = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        var tenantB = _gateway.TenantRegistry.MintOrLookupBySubject("sub-bob", "bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", _tenantA.Value);
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", tenantB.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* best-effort */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best-effort */ }
    }

    // ---- the ledger ingress, exactly as a Director uses it -----------------------------------------

    private static ActivityEventRecord Submission(DateTime at, string session, string? origin, string? source,
        string agent = "ClaudeCode") => new()
    {
        EventId = Guid.NewGuid(),
        DirectorSequence = 1,
        OccurredUtc = at,
        DirectorId = "dir-1",
        SessionId = session,
        Machine = "SOREN_NORTH",
        AgentKind = agent,
        EventType = ActivityEventTypes.TurnSubmitted,
        Cause = source == "Agent" ? ActivityCauses.AgentSubmit : ActivityCauses.OwnerSubmit,
        InputOrigin = origin,
        SendSource = source,
    };

    private async Task Post(string deviceKey, params ActivityEventRecord[] events)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "activity-events/batch")
        {
            Content = JsonContent.Create(new ActivityEventIngestRequest { Events = events.ToList() }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var ack = await resp.Content.ReadFromJsonAsync<ActivityEventIngestResponse>();
        Assert.Equal(events.Length, ack!.Written);
    }

    private async Task<HttpResponseMessage> Feed(string deviceKey, string query = "")
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "stats/data" + query);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return await _http.SendAsync(req);
    }

    private async Task<(JsonElement Root, string Body)> FeedBody(string deviceKey, string query = "")
    {
        var resp = await Feed(deviceKey, query);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        return (JsonDocument.Parse(body).RootElement.Clone(), body);
    }

    /// <summary>Tenant A's session history, written through the real store over the Gateway's own database
    /// file (the history recorder itself only runs on the SignalR push path, which this HTTP-level test does
    /// not drive). The store is opened under tenant A explicitly, so every row lands in A's partition.</summary>
    private SessionHistoryStore HistoryForA()
        => new(new GatewayDatabase(new FixedTenantContext(_tenantA)));

    private static SessionDto Born(string id, DateTime startedAt, string? kind = null, string? surface = null,
        string? parent = null, string? repoName = null, string? repoPath = null) => new()
    {
        SessionId = id,
        RepoPath = repoPath ?? @"D:\repos\devthrottle",
        RepoName = repoName ?? "",
        Agent = "ClaudeCode",
        MachineName = "SOREN_NORTH",
        CreatedAt = startedAt,
        ActivityState = "Working",
        Status = "Running",
        OriginKind = kind,
        OriginSurface = surface,
        ParentSessionId = parent,
    };

    // ---- the facts --------------------------------------------------------------------------------

    [Fact]
    public async Task The_feed_serves_the_ledger_figure_for_the_callers_own_tenant_and_nobody_elses()
    {
        var t = DateTime.UtcNow.AddHours(-2);
        await Post(_keyA,
            // Three turns typed at the desktop terminal: null send source, present origin. IN (R17, 1).
            Submission(t, "s-a", "typed/desktop", null),
            Submission(t.AddMinutes(1), "s-a", "typed/desktop", null),
            Submission(t.AddMinutes(2), "s-a", "typed/desktop", null),
            // The durable phone dictation and a desktop voice turn. IN.
            Submission(t.AddMinutes(3), "s-a", "voice/phone", "Delivery"),
            Submission(t.AddMinutes(4), "s-a", "voice/phone", "Delivery"),
            Submission(t.AddMinutes(5), "s-a", "voice/desktop", "UserInput"),
            // Four fleet messages: stamped Agent, no origin. OUT by record, reported beside (R17, 2).
            Submission(t.AddMinutes(6), "s-a", null, "Agent"),
            Submission(t.AddMinutes(7), "s-a", null, "Agent"),
            Submission(t.AddMinutes(8), "s-a", null, "Agent"),
            Submission(t.AddMinutes(9), "s-a", null, "Agent"),
            // Five of the owner's submissions the product could not place. OUT and DISCLOSED (R17, 3).
            Submission(t.AddMinutes(10), "s-a", null, "UserInput"),
            Submission(t.AddMinutes(11), "s-a", null, "UserInput"),
            Submission(t.AddMinutes(12), "s-a", null, "UserInput"),
            Submission(t.AddMinutes(13), "s-a", null, "UserInput"),
            Submission(t.AddMinutes(14), "s-a", null, "UserInput"),
            // The seed prompt. Nobody's turn.
            Submission(t.AddMinutes(15), "s-a", null, "Framework"));
        // A state transition carrying an origin-shaped token: NOT a submission, must not be counted.
        await Post(_keyA, new ActivityEventRecord
        {
            EventId = Guid.NewGuid(), DirectorSequence = 1, OccurredUtc = t.AddMinutes(16), DirectorId = "dir-1",
            SessionId = "s-a", AgentKind = "ClaudeCode", EventType = ActivityEventTypes.ActivityTransition,
            PreviousState = "WaitingForInput", NewState = "Working", Cause = ActivityCauses.OwnerSubmit,
            InputOrigin = "voice/desktop", SendSource = "UserInput",
        });
        // Tenant B: seven phone dictations into a session with a distinctive id.
        var bRows = Enumerable.Range(0, 7)
            .Select(i => Submission(t.AddMinutes(i), "bravo-only-session", "voice/phone", "Delivery", agent: "Codex")).ToArray();
        await Post(_keyB, bRows);

        var (a, bodyA) = await FeedBody(_keyA);

        Assert.True(a.GetProperty("available").GetBoolean());
        var figure = a.GetProperty("throttle");
        Assert.Equal(ThrottleDefinition.Predicate, figure.GetProperty("definition").GetString());
        Assert.Equal("submitted turns", figure.GetProperty("unit").GetString());
        Assert.Equal(6, figure.GetProperty("turns").GetInt64());
        Assert.Equal(3, figure.GetProperty("voiceTurns").GetInt64());
        Assert.Equal(3, figure.GetProperty("typedTurns").GetInt64());
        Assert.Equal(1, figure.GetProperty("sessions").GetInt32());

        var buckets = figure.GetProperty("buckets").EnumerateArray()
            .ToDictionary(b => b.GetProperty("modality").GetString() + "/" + b.GetProperty("surface").GetString(),
                          b => b.GetProperty("turns").GetInt64());
        Assert.Equal(3, buckets.Count);
        Assert.Equal(3, buckets["typed/desktop"]);
        Assert.Equal(2, buckets["voice/phone"]);
        Assert.Equal(1, buckets["voice/desktop"]);

        var excluded = figure.GetProperty("excluded");
        Assert.Equal(10, excluded.GetProperty("noInputOrigin").GetInt64());
        Assert.Equal(4, excluded.GetProperty("agentDriven").GetInt64());
        Assert.Equal(1, excluded.GetProperty("framework").GetInt64());
        Assert.Equal(5, excluded.GetProperty("unresolved").GetInt64());
        Assert.Equal(4, figure.GetProperty("agentDrivenTurns").GetInt64());

        var agent = Assert.Single(figure.GetProperty("agents").EnumerateArray());
        Assert.Equal("ClaudeCode", agent.GetProperty("agent").GetString());
        Assert.Equal("Claude Code", agent.GetProperty("agentName").GetString());
        Assert.Equal(6, agent.GetProperty("turns").GetInt64());
        Assert.Equal(4, agent.GetProperty("agentDrivenTurns").GetInt64());

        var window = figure.GetProperty("window");
        Assert.True(window.GetProperty("isDefault").GetBoolean());
        Assert.Equal("Last 30 days", window.GetProperty("label").GetString());
        Assert.Equal(30, figure.GetProperty("ledger").GetProperty("retentionDays").GetInt32());
        Assert.True(figure.GetProperty("ledger").GetProperty("earliestUtc").GetDateTime() <= t.AddSeconds(1));

        // The hourly series is the counted turns only, keyed by UTC clock hour.
        Assert.Equal(6, figure.GetProperty("hourlyTurns").EnumerateArray().Sum(h => h.GetProperty("turns").GetInt64()));

        Assert.True(a.GetProperty("notCaptured").GetArrayLength() > 0);
        // No character volume anywhere on the feed (R16), and no tally-only block.
        Assert.DoesNotContain("\"characters\"", bodyA, StringComparison.Ordinal);
        Assert.DoesNotContain("\"wingman\"", bodyA, StringComparison.Ordinal);
        Assert.DoesNotContain("agentsSinceUtc", bodyA, StringComparison.Ordinal);
        // No message text ever leaves the machine for this page - only counts.
        Assert.DoesNotContain("\"text\"", bodyA, StringComparison.OrdinalIgnoreCase);
        // And nothing of tenant B's.
        Assert.DoesNotContain("bravo-only-session", bodyA, StringComparison.Ordinal);
        Assert.DoesNotContain("Codex", bodyA, StringComparison.Ordinal);

        // Tenant B, on the same feed, sees exactly its own.
        var (b, _) = await FeedBody(_keyB);
        var figureB = b.GetProperty("throttle");
        Assert.Equal(7, figureB.GetProperty("turns").GetInt64());
        var bucketB = Assert.Single(figureB.GetProperty("buckets").EnumerateArray());
        Assert.Equal("phone", bucketB.GetProperty("surface").GetString());
        Assert.Equal(0, figureB.GetProperty("excluded").GetProperty("noInputOrigin").GetInt64());
    }

    [Fact]
    public async Task An_explicit_window_is_honoured_and_one_the_ledger_cannot_answer_is_refused()
    {
        var now = DateTime.UtcNow;
        await Post(_keyA,
            Submission(now.AddDays(-2), "s-w", "typed/desktop", null),
            Submission(now.AddDays(-1), "s-w", "voice/desktop", "UserInput"));

        static string Q(DateTime from, DateTime to) => $"?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

        // A window holding only the older row.
        var (a, _) = await FeedBody(_keyA, Q(now.AddDays(-3), now.AddDays(-1).AddHours(-1)));
        var figure = a.GetProperty("throttle");
        Assert.Equal(1, figure.GetProperty("turns").GetInt64());
        Assert.Equal(1, figure.GetProperty("typedTurns").GetInt64());
        Assert.False(figure.GetProperty("window").GetProperty("isDefault").GetBoolean());
        Assert.EndsWith("UTC", figure.GetProperty("window").GetProperty("label").GetString());

        // Longer than the ledger keeps: refused with the reason, never served with silent zeroes in front.
        var tooLong = await Feed(_keyA, Q(now.AddDays(-40), now));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("submission ledger keeps", await tooLong.Content.ReadAsStringAsync());

        // Half a window is not a window.
        var half = await Feed(_keyA, "?from=" + Uri.EscapeDataString(now.AddDays(-3).ToString("O")));
        Assert.Equal(HttpStatusCode.BadRequest, half.StatusCode);
    }

    [Fact]
    public async Task The_repository_split_joins_the_callers_own_session_history_and_discloses_the_rest()
    {
        var now = DateTime.UtcNow;
        var history = HistoryForA();
        history.UpsertLive("dir-1", Born("named", now.AddHours(-3), repoName: "thefrederiksen/devthrottle",
            repoPath: @"D:\ReposFred\devthrottle-throttle"), now);
        history.UpsertLive("dir-1", Born("path-only", now.AddHours(-3), repoPath: @"D:\ReposFred\mindzieWeb"), now);

        await Post(_keyA,
            Submission(now.AddHours(-1), "named", "typed/desktop", null),
            Submission(now.AddHours(-1), "named", "voice/desktop", "UserInput"),
            Submission(now.AddHours(-1), "path-only", "typed/desktop", null),
            Submission(now.AddHours(-1), "nowhere-in-history", "typed/desktop", null));

        var (a, _) = await FeedBody(_keyA);
        var figure = a.GetProperty("throttle");
        Assert.Equal(4, figure.GetProperty("turns").GetInt64());

        var repos = figure.GetProperty("repos").EnumerateArray().ToList();
        Assert.Equal(2, repos.Count);
        Assert.Equal("thefrederiksen/devthrottle", repos[0].GetProperty("repo").GetString());
        Assert.Equal("devthrottle", repos[0].GetProperty("repoName").GetString());
        Assert.Equal(2, repos[0].GetProperty("turns").GetInt64());
        Assert.Equal(1, repos[0].GetProperty("voiceTurns").GetInt64());
        Assert.Equal(@"D:\ReposFred\devthrottle-throttle", Assert.Single(repos[0].GetProperty("checkouts").EnumerateArray()).GetString());
        Assert.Equal("mindzieWeb", repos[1].GetProperty("repoName").GetString());
        // The session history holds nothing for: disclosed, never guessed into a row (R7).
        Assert.Equal(1, figure.GetProperty("reposUnattributedTurns").GetInt64());
    }

    /// <summary>
    /// The session-origin block (devthrottle_internal issue #982) rides the same feed and reads the caller's
    /// own session history: how the fleet's sessions CAME TO EXIST. A row that predates the origin fields is
    /// kept out of the real buckets under the not-recorded key, and the feed says in words what that means.
    /// </summary>
    [Fact]
    public async Task The_session_origin_block_reads_the_callers_history_and_keeps_unrecorded_rows_apart()
    {
        var now = DateTime.UtcNow;
        var history = HistoryForA();
        history.UpsertLive("dir-1", Born("o-a", now.AddHours(-2), SessionOriginKinds.Agent, SessionOriginSurfaces.Cli, Guid.NewGuid().ToString()), now);
        history.UpsertLive("dir-1", Born("o-b", now.AddHours(-2), SessionOriginKinds.Agent, SessionOriginSurfaces.Cli, Guid.NewGuid().ToString()), now);
        history.UpsertLive("dir-1", Born("o-c", now.AddHours(-2), SessionOriginKinds.Human, SessionOriginSurfaces.Desktop), now);
        history.UpsertLive("dir-1", Born("o-d", now.AddHours(-2), SessionOriginKinds.Schedule, SessionOriginSurfaces.Cron), now);
        history.UpsertLive("dir-1", Born("legacy", now.AddHours(-2)), now);

        var (a, _) = await FeedBody(_keyA);
        var origins = a.GetProperty("sessionOrigins");
        var week = origins.GetProperty("last7Days");
        Assert.Equal(5, week.GetProperty("sessions").GetInt32());
        Assert.Equal(2, week.GetProperty("byKind").GetProperty(SessionOriginKinds.Agent).GetInt32());
        Assert.Equal(1, week.GetProperty("byKind").GetProperty(SessionOriginKinds.Human).GetInt32());
        Assert.Equal(1, week.GetProperty("byKind").GetProperty(SessionOriginKinds.Schedule).GetInt32());
        Assert.Equal(1, week.GetProperty("byKind").GetProperty(SessionHistoryStore.NotRecorded).GetInt32());
        Assert.Equal(2, week.GetProperty("withParentSession").GetInt32());
        Assert.Equal(2, week.GetProperty("bySurface").GetProperty(SessionOriginSurfaces.Cli).GetInt32());

        var allTime = origins.GetProperty("allTime");
        Assert.Equal(5, allTime.GetProperty("sessions").GetInt32());
        Assert.True(allTime.GetProperty("recordBeginsUtc").GetDateTime() > DateTime.UtcNow.AddHours(-3),
            "the record begins at the oldest stored birth, not at an epoch");
        Assert.False(allTime.TryGetProperty("sinceUtc", out _));
        Assert.False(week.TryGetProperty("agentShare", out _));
        Assert.Contains("predates the origin fields", origins.GetProperty("notRecordedMeans").GetString());

        // And none of it reaches tenant B.
        var (b, _) = await FeedBody(_keyB);
        Assert.Equal(0, b.GetProperty("sessionOrigins").GetProperty("last7Days").GetProperty("sessions").GetInt32());
    }

    /// <summary>On this hosted Gateway there is no statistics store (no CC_GATEWAY_STATS_CONNECTION), which
    /// used to answer the whole feed with a 503. Nothing that counts a turn depends on it any more, so the
    /// figure is served and the store-fed blocks come back null with the reason beside them.</summary>
    [Fact]
    public async Task An_absent_statistics_store_no_longer_takes_the_figure_down()
    {
        await Post(_keyA, Submission(DateTime.UtcNow.AddHours(-1), "s-x", "typed/desktop", null));

        var (a, _) = await FeedBody(_keyA);
        Assert.Equal(1, a.GetProperty("throttle").GetProperty("turns").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(a.GetProperty("statisticsUnavailableReason").GetString()));
        Assert.Equal(JsonValueKind.Null, a.GetProperty("concurrency").ValueKind);
        Assert.Equal(JsonValueKind.Null, a.GetProperty("tokenSpend").ValueKind);
    }

    [Fact]
    public async Task A_device_with_no_bound_tenant_is_refused_and_the_page_still_redirects()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Feed(_keyUnbound)).StatusCode);

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var raw = new HttpClient(handler) { BaseAddress = _http.BaseAddress };
        raw.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _keyA);
        var resp = await raw.GetAsync("stats");
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal("/your-throttle", resp.Headers.Location!.ToString());
    }
}
