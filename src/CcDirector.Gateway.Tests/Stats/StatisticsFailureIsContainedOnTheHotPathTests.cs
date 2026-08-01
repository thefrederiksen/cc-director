using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// A RUNTIME statistics write failure does not fault the roster (failure review finding M2).
///
/// WHAT WAS WRONG. The startup boundary already contained store selection, opening and migration - a
/// statistics database that could not be reached could not stop the Gateway starting. Nothing contained what
/// happened AFTER startup. The writer opens a context and a transaction with no catch; both aggregator
/// observation methods call it with no catch; and <c>GET /sessions</c> and the Director hub call those
/// observation methods INLINE, on the request thread, with no catch. So a lock timeout, a lost connection or
/// an unwritable file turned a background concern into HTTP 500 on the path every client polls - the exact
/// shape of the incident the startup containment was written for, one layer further in.
///
/// WHY THIS FIXTURE CAN SHOW THE FAILURE RATHER THAN MERELY PASS. Three things, and without all three a
/// green here would be worth nothing:
///
///  1. THE FOLD MUST ACTUALLY RUN. An empty roster folds nothing and writes nothing, so a Gateway with no
///     sessions would answer 200 whether or not anything were contained. A session with a real tally is
///     pushed, and the first roster read is asserted to have folded it successfully - so the second read is
///     known to be doing the work that fails.
///  2. THE FAILURE MUST ACTUALLY BE COUNTED. A 200 alone is also what a fold that silently never ran looks
///     like. The aggregator's failure count is asserted to move from zero to one, and its last error to be
///     recorded - that is the containment SHOUTING, and it is what distinguishes "contained" from "skipped".
///  3. THE FAULT MUST BE REAL. <see cref="TheSameFault_IsFatal_WhenItIsNotContained"/> calls the SAME broken
///     aggregator the way the route used to and asserts it throws. Without that control, a fixture in which
///     the write happened to succeed would produce the same green.
/// </summary>
public sealed class StatisticsFailureIsContainedOnTheHotPathTests : IDisposable
{
    private const string Token = "test-token-contained-stats";
    private const string DirectorId = "dir-contained";
    private const string Machine = "MACHINE-CONTAINED";

    private readonly ITestOutputHelper _out;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-stats-contained-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;

    public StatisticsFailureIsContainedOnTheHotPathTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        // Self-host, stated rather than inherited: this fixture needs a WORKING statistics store to break,
        // and the only one a test can stand up without a database server is the local SQLite file - which a
        // hosted Gateway refuses to open. A run that silently inherited hosted mode would find no aggregator
        // at all and would pass having exercised nothing.
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);
        Assert.False(GatewayHostedMode.IsHosted);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>A session carrying a real tally, so the fold has something to write. A session with no
    /// buckets would produce an empty batch, which the writer returns from without opening a transaction -
    /// and a write that never happens cannot fail.</summary>
    private static SessionDto SessionWithATally(string id, long turns) => new()
    {
        SessionId = id,
        Name = id,
        ActivityState = "Working",
        StatusColor = "blue",
        LastActivityAt = DateTime.UtcNow,
        RepoPath = @"D:\ReposFred\devthrottle",
        InputStats = new InputStatsDto
        {
            Buckets = { new InputStatBucketDto { Modality = "typed", Surface = "desktop", Turns = turns, Characters = turns * 100 } },
        },
    };

    private GatewayHost NewGateway() => new(
        port: GatewayHost.OperatingSystemAssignedPort,
        token: Token,
        authEnabled: true,
        instancesDirectory: Path.Combine(_root, "instances"),
        workListsPath: Path.Combine(_root, "worklists", "worklists.json"),
        snoozePath: Path.Combine(_root, "snooze", "snooze.json"),
        inputStatsPath: Path.Combine(_root, "gateway-stats.db"));

    /// <summary>Put one live, freshly-served session on the roster, so the statistics fold has a subject.
    /// Registering the connection AND applying the snapshot is what makes the serve FRESH - the roster only
    /// folds sessions from a serve it confirmed current, so a session without a live connection would be
    /// excluded and the fold would run over nothing.</summary>
    private static void PushOneLiveSession(GatewayHost gateway, string sessionId, long turns)
    {
        gateway.Registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        gateway.PushedSessions.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(gateway.PushedSessions.ApplySnapshot(
            TenantId.Local, DirectorId, "conn-1", 1, new[] { SessionWithATally(sessionId, turns) }));
    }

    /// <summary>
    /// Break the statistics store the way a runtime failure breaks it: the store was fine at startup and is
    /// not fine now. Disposing the aggregator closes the SQLite connection it owns, so every subsequent
    /// context, transaction and statement raises - which is the same class of fault as a lock timeout, a
    /// dropped connection or a full disk, and the one the failure review says escapes.
    /// </summary>
    private static void BreakTheStatisticsStoreAfterStartup(GatewayHost gateway)
    {
        Assert.NotNull(gateway.InputStats);
        gateway.InputStats!.Dispose();
    }

    [Fact]
    public async Task AStatisticsWriteFailure_DoesNotFaultTheRoster_AndIsCountedAndLogged()
    {
        await using var gateway = NewGateway();
        await gateway.StartAsync();

        // The store really is there and really is usable - otherwise there is nothing to break.
        Assert.NotNull(gateway.InputStats);
        Assert.Equal(0, gateway.InputStats!.Health.FailureCount);

        PushOneLiveSession(gateway, "s-contained", turns: 7);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // ---- FIRST READ: the healthy path. This is what proves the fold RUNS in this fixture.
        var healthy = await http.GetAsync("sessions");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        using (var parsed = JsonDocument.Parse(await healthy.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Array, parsed.RootElement.ValueKind);
            Assert.NotEmpty(parsed.RootElement.EnumerateArray());
        }
        // Folded, and folded WITHOUT failing: the numbers reached the store.
        Assert.Equal(0, gateway.InputStats.Health.FailureCount);
        var totalsBefore = gateway.InputStats.CurrentTotals(TenantId.Local);
        Assert.Contains(totalsBefore.Buckets, b => b.Turns == 7);

        // ---- BREAK IT, then drive the same path again with MORE to fold.
        BreakTheStatisticsStoreAfterStartup(gateway);
        Assert.True(gateway.PushedSessions.ApplySnapshot(
            TenantId.Local, DirectorId, "conn-1", 2, new[] { SessionWithATally("s-contained", turns: 19) }));

        var broken = await http.GetAsync("sessions");
        var body = await broken.Content.ReadAsStringAsync();
        _out.WriteLine($"GET /sessions with the statistics store broken -> {(int)broken.StatusCode} {broken.StatusCode}");

        // THE CLAIM: the roster is unaffected by a statistics failure.
        Assert.Equal(HttpStatusCode.OK, broken.StatusCode);
        Assert.Equal("application/json", broken.Content.Headers.ContentType?.MediaType);
        using (var parsed = JsonDocument.Parse(body))
        {
            Assert.Equal(JsonValueKind.Array, parsed.RootElement.ValueKind);
            Assert.NotEmpty(parsed.RootElement.EnumerateArray());
        }

        // AND IT SHOUTED. Without this the test would also pass against a Gateway that had quietly stopped
        // folding altogether, which is the failure mode a silent catch produces and the one that took
        // thirty-two minutes to see the last time it happened.
        Assert.Equal(1, gateway.InputStats.Health.FailureCount);
        Assert.False(string.IsNullOrWhiteSpace(gateway.InputStats.Health.LastError));
        _out.WriteLine($"CONTAINED: observer={gateway.InputStats.Health.Observer} " +
                       $"failures={gateway.InputStats.Health.FailureCount} error={gateway.InputStats.Health.LastError}");
    }

    /// <summary>
    /// THE CONTROL. The same broken aggregator, called the way the roster route called it BEFORE the
    /// containment, throws. So the 200 above is the containment working, not a fixture in which the write
    /// would have succeeded anyway.
    /// </summary>
    [Fact]
    public async Task TheSameFault_IsFatal_WhenItIsNotContained()
    {
        await using var gateway = NewGateway();
        await gateway.StartAsync();
        Assert.NotNull(gateway.InputStats);

        BreakTheStatisticsStoreAfterStartup(gateway);

        var thrown = Record.Exception(() =>
            gateway.InputStats!.ObserveSnapshot(
                new[] { SessionWithATally("s-uncontained", turns: 5) }, DateTime.UtcNow, TenantId.Local));

        Assert.NotNull(thrown);
        _out.WriteLine($"UNCONTAINED: {thrown!.GetType().Name}: {thrown.Message}");

        // The aggregator does NOT contain its own failures, deliberately: containment belongs to the caller
        // whose fate is being protected, and the write-path tests rely on a broken store throwing.
        Assert.Equal(0, gateway.InputStats!.Health.FailureCount);
    }
}
