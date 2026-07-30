using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using Xunit;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// OUTPUT PARITY for the twelve statistics read projections, before and after the Entity Framework port.
///
/// The method: write ONE fixture with the real aggregator, then read the SAME PHYSICAL ROWS twice - once
/// through <see cref="FrozenSqliteStatsReader"/> (the pre-port raw SQLite projections, verbatim) and once
/// through the ported implementation - and compare the rendered result of every projection, for every
/// tenant. Same numbers, same ordering, same tie-breaks, or the comparison fails and prints the diff.
///
/// Reading the same rows is what makes a failure mean something: the reader is isolated from the writer, so
/// a mismatch can only be the projection. Two stores written separately would leave "the fixtures differed"
/// as a live explanation for every red.
///
/// WHAT THIS PROVES, AND WHAT IT DOES NOT. This is parity at the DTO level - the twelve accessors return
/// equal values in equal order. It is NOT the rendered <c>/stats/data</c> body: storing the same rows and
/// rendering the same page are different claims, and the body-level check belongs to the provider-
/// parametrised contract suite. Nobody downstream should read this file as having covered it.
///
/// It is also a SQLite-side proof. Provider neutrality is carried by the shared model, the "C" collation
/// pinned on every text column for Postgres, and the deliberate decision to keep every ordinal tie-break in
/// C# rather than in an ORDER BY - an ordinal compare in C# and a collation-dependent sort in the database
/// are different functions. Running the same projections against real Postgres is the contract suite's job.
///
/// THE FIXTURE CARRIES THREE TENANTS ON PURPOSE. A single-tenant fixture would pass trivially: with one
/// tenant there is nothing for a missing tenant filter to wrongly include, so the comparison could not tell
/// a correctly-scoped projection from an unscoped one and would be structurally incapable of catching a
/// broken rendered number. The tenants share bare session ids AND share repository and agent display
/// spellings, because a surrogate-id port goes wrong exactly there - two tenants spelling a repository the
/// same way must mint two ids and never coalesce, while two spellings differing only by case WITHIN one
/// tenant must resolve to one.
/// </summary>
public sealed class GatewayStatsReadParityTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly TenantId[] AllTenants = { TenantId.Local, TenantA, TenantB };

    private static readonly DateTime LongAgo = new(2025, 3, 4, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 7, 30, 14, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly string _path;

    public GatewayStatsReadParityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "gateway-stats.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public void EveryProjection_RendersIdenticallyBeforeAndAfterThePort()
    {
        WriteFixture();

        string ported;
        using (var aggregator = new GatewayInputStatsAggregator(_path))
            ported = RenderPorted(aggregator);

        string frozen;
        using (var reader = new FrozenSqliteStatsReader(_path))
            frozen = RenderFrozen(reader);

        Assert.Equal(frozen, ported);
    }

    /// <summary>
    /// The comparison CAN fail, and it names what differs.
    ///
    /// A parity test that has only ever agreed is not evidence that two readers agree - only that nothing
    /// disagreed loudly enough to be noticed. So one number the FROZEN reader returns is changed, by exactly
    /// the amount a lost turn would change it, and the comparison is required to reject the pair and to say
    /// which field moved. That failure message is the whole value of this test to whoever hits it next year:
    /// a comparison that fails without naming the field sends them reading two thousand lines of JSON.
    ///
    /// This rides the same run as the parity assertion above, so the detector is validated every time rather
    /// than once, by hand, in a session nobody can re-open.
    /// </summary>
    [Fact]
    public void TheComparison_RejectsAOneNumberDifference_AndNamesTheField()
    {
        WriteFixture();

        string ported;
        using (var aggregator = new GatewayInputStatsAggregator(_path))
            ported = RenderPorted(aggregator);

        string frozen;
        using (var reader = new FrozenSqliteStatsReader(_path))
            frozen = RenderFrozen(reader);

        // Sanity: the two really do agree before the change, or "it failed after" would prove nothing.
        Assert.Equal(frozen, ported);

        var marker = "\"Turns\": ";
        var at = frozen.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "the rendered document must contain a Turns field for this to perturb");

        var valueStart = at + marker.Length;
        var valueEnd = frozen.IndexOfAny(new[] { ',', '\r', '\n' }, valueStart);
        var original = long.Parse(frozen[valueStart..valueEnd]);
        var damaged = frozen[..valueStart] + (original - 1) + frozen[valueEnd..];

        Assert.NotEqual(frozen, damaged);

        var failure = Record.Exception(() => Assert.Equal(damaged, ported));
        Assert.NotNull(failure);
        Assert.Contains("Turns", failure!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fixture actually contains the shapes the parity comparison is supposed to be sensitive to.
    ///
    /// Without this, "the two readers agreed" is answerable by an empty database, an archive rule nothing
    /// exercises and a tie-break nothing ties. Each assertion below names a shape the twelve projections
    /// branch on, so a fixture that quietly stopped producing one fails here rather than turning the parity
    /// test into a comparison of two empty lists.
    /// </summary>
    [Fact]
    public void TheFixtureExercisesTheShapesParityIsSupposedToCatch()
    {
        WriteFixture();
        using var agg = new GatewayInputStatsAggregator(_path);

        // More than one tenant, each with rows. This is the property that stops the whole comparison being
        // vacuous with respect to tenant scoping.
        foreach (var tenant in AllTenants)
            Assert.NotEmpty(agg.CurrentTotals(tenant).Buckets);

        // Overlapping display spellings across tenants must NOT have coalesced: tenant A and tenant B both
        // drove "owner/shared", and each must see its own turns only.
        var sharedForA = agg.RepoTotals(TenantA).Single(r => r.Repo == "owner/shared");
        var sharedForB = agg.RepoTotals(TenantB).Single(r => r.Repo == "owner/shared");
        Assert.NotEqual(sharedForA.Turns, sharedForB.Turns);

        // A case-variant spelling WITHIN one tenant resolves to the single first-seen identity, so tenant B
        // has exactly one row for it rather than two.
        Assert.Single(agg.RepoTotals(TenantB),
            r => string.Equals(r.Repo, "owner/shared", StringComparison.OrdinalIgnoreCase));

        // An ARCHIVE row exists for tenant A: its all-time totals include it, and its hourly series must not.
        Assert.DoesNotContain(agg.HourlyTurns(TenantA), h => h.Hour == GatewayStatsDatabase.ArchiveMarker);
        Assert.DoesNotContain(agg.TokenSpendByHour(TenantA), h => h.Hour == GatewayStatsDatabase.ArchiveMarker);
        Assert.True(agg.CurrentTotals(TenantA).Buckets.Sum(b => b.Turns)
                    > agg.HourlyTurns(TenantA).Sum(h => h.Turns),
            "the all-time total must exceed the hourly series, which is what an archived row means");

        // The null-model bucket is present and is a real row, not a gap.
        Assert.Contains(agg.ModelTotals(TenantA), m => m.Model is null);
        Assert.Contains(agg.TokenSpendByModel(TenantA), m => m.Model is null);

        // A genuine tie in the repository ranking, so the ORDINAL name tie-break actually decides an order.
        var ranked = agg.RepoTotals(TenantId.Local);
        Assert.Contains(ranked.Zip(ranked.Skip(1)), pair =>
            pair.First.Turns == pair.Second.Turns && pair.First.Characters == pair.Second.Characters);

        // More than one hour bucket, so the hourly ordering is a real ordering.
        Assert.True(agg.HourlyTurns(TenantA).Count > 1);

        // Both other lanes carry rows: agent-driven turns and token spend.
        Assert.True(agg.AgentDrivenUsage(TenantA).Turns > 0);
        Assert.True(agg.TokenSpend(TenantA).TotalTokens > 0);

        // The wingman lane counts a TYPED turn folded while voice mode was on.
        Assert.True(agg.WingmanUsage(TenantB).Turns > 0);
        Assert.True(agg.WingmanUsage(TenantB).Sessions > 0);
    }

    // ---- The fixture ---------------------------------------------------------------------------------
    //
    // Three tenants sharing bare session ids and sharing repository and agent display spellings; several
    // hours; voice and typed; wingman; agent-driven turns; token spend with and without a recorded model;
    // deliberate ties in turns AND characters; and rows old enough to be archived by the retention prune.

    private void WriteFixture()
    {
        using var agg = new GatewayInputStatsAggregator(_path);

        // Tenant A, long ago - these rows are what the retention prune later folds into an ARCHIVE row.
        agg.Observe(Full("s1", "owner/shared", "D:\\Repos\\shared", "ClaudeCode", "claude-opus-5",
            voiceMode: false, ("typed", "desktop", 4, 400), tokens: (100, 200, 300, 400)), LongAgo, TenantA);
        agg.Observe(Full("s2", "owner/legacy", "D:\\Repos\\legacy", "Codex", model: null,
            voiceMode: false, ("voice", "phone", 2, 120), tokens: (10, 20, 30, 40)), LongAgo, TenantA);

        // Tenant A, recent. Two hours, so the hourly series has a real order. The second fold on s1 grows the
        // counts, so only the increase folds - the high-water discipline the whole store is built on.
        agg.Observe(Full("s1", "owner/shared", "D:\\Repos\\shared", "ClaudeCode", "claude-opus-5",
            voiceMode: false, ("typed", "desktop", 9, 900), tokens: (500, 600, 700, 800)), Now, TenantA);
        agg.Observe(Full("s3", "owner/shared", "D:\\Repos\\shared-worktree", "claudecode", "claude-opus-5",
            voiceMode: false, ("voice", "phone", 5, 250), tokens: (1, 2, 3, 4)), Now.AddHours(1), TenantA);
        agg.Observe(AgentDriven("s3", "claudecode", 7, 700), Now.AddHours(1), TenantA);

        // Tenant B - the SAME bare session ids and the SAME repository and agent spellings as tenant A, plus a
        // case variant of each within the tenant. Different numbers, so a coalesced identity would show up as
        // a wrong figure rather than as a missing row.
        // 12, not 11: at 11 this tenant's shared-repository total came to 14 turns and so did tenant A's, and
        // the assertion that the two tenants' figures DIFFER - the one that would catch two tenants' turns
        // coalescing into one surrogate identity - cannot see a coalesce when both sides are already equal.
        agg.Observe(Full("s1", "owner/shared", "D:\\Repos\\shared", "ClaudeCode", "claude-opus-5",
            voiceMode: true, ("typed", "desktop", 12, 1200), tokens: (7, 8, 9, 10)), Now, TenantB);
        agg.Observe(Full("s2", "OWNER/Shared", "d:\\repos\\SHARED", "CLAUDECODE", "CLAUDE-OPUS-5",
            voiceMode: true, ("voice", "phone", 3, 33), tokens: (1, 1, 1, 1)), Now, TenantB);
        agg.Observe(AgentDriven("s1", "ClaudeCode", 2, 22), Now, TenantB);

        // The local tenant - the self-host shape, and where the deliberate ranking TIE lives: two repositories
        // with identical turns AND identical characters, so only the ordinal leaf-name compare can order them.
        agg.Observe(Full("s1", "zzz/aaa", "D:\\Repos\\aaa", "ClaudeCode", "claude-opus-5",
            voiceMode: false, ("typed", "desktop", 6, 600), tokens: (5, 5, 5, 5)), Now, TenantId.Local);
        agg.Observe(Full("s2", "aaa/zzz", "D:\\Repos\\zzz", "Codex", model: null,
            voiceMode: false, ("typed", "desktop", 6, 600), tokens: (5, 5, 5, 5)), Now, TenantId.Local);
    }

    private static SessionDto Full(
        string sessionId, string repoName, string repoPath, string agent, string? model, bool voiceMode,
        (string Modality, string Surface, long Turns, long Chars) bucket,
        (long In, long Out, long CacheRead, long CacheCreation) tokens)
    {
        var dto = new SessionDto
        {
            SessionId = sessionId,
            RepoName = repoName,
            RepoPath = repoPath,
            Agent = agent,
            CurrentModel = model,
            VoiceMode = voiceMode,
            InputStats = new InputStatsDto(),
            TokenTotals = new TokenTotalsDto
            {
                InputTokens = tokens.In,
                OutputTokens = tokens.Out,
                CacheReadTokens = tokens.CacheRead,
                CacheCreationTokens = tokens.CacheCreation,
            },
        };
        dto.InputStats!.Buckets.Add(new InputStatBucketDto
        {
            Modality = bucket.Modality,
            Surface = bucket.Surface,
            Turns = bucket.Turns,
            Characters = bucket.Chars,
        });
        return dto;
    }

    // A session carrying only turns another agent drove into it - the lane that never enters the human
    // totals and therefore has to be folded from its own table.
    private static SessionDto AgentDriven(string sessionId, string agent, long turns, long chars) => new()
    {
        SessionId = sessionId,
        Agent = agent,
        InputStats = new InputStatsDto { AgentDrivenTurns = turns, AgentDrivenCharacters = chars },
    };

    // ---- Rendering -----------------------------------------------------------------------------------
    //
    // One document per reader, holding every projection for every tenant, in a fixed order. Comparing the
    // whole document rather than field by field means the FIRST difference is reported with its context,
    // and that an accidentally-omitted projection shows up as a missing section rather than as a silent pass.

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static string RenderPorted(GatewayInputStatsAggregator a) =>
        JsonSerializer.Serialize(AllTenants.Select(t => new
        {
            tenant = t.Value,
            currentTotals = a.CurrentTotals(t),
            hourlyTurns = a.HourlyTurns(t),
            repoTotals = a.RepoTotals(t),
            agentTotals = a.AgentTotals(t),
            modelTotals = a.ModelTotals(t),
            tokenSpend = a.TokenSpend(t),
            tokenSpendByHour = a.TokenSpendByHour(t),
            tokenSpendByModel = a.TokenSpendByModel(t),
            wingmanUsage = a.WingmanUsage(t),
            agentDrivenUsage = a.AgentDrivenUsage(t),
            agentsSinceUtc = a.AgentsSinceUtc(t),
            modelsSinceUtc = a.ModelsSinceUtc,
        }).ToList(), Pretty);

    private static string RenderFrozen(FrozenSqliteStatsReader r) =>
        JsonSerializer.Serialize(AllTenants.Select(t => new
        {
            tenant = t.Value,
            currentTotals = r.CurrentTotals(t),
            hourlyTurns = r.HourlyTurns(t),
            repoTotals = r.RepoTotals(t),
            agentTotals = r.AgentTotals(t),
            modelTotals = r.ModelTotals(t),
            tokenSpend = r.TokenSpend(t),
            tokenSpendByHour = r.TokenSpendByHour(t),
            tokenSpendByModel = r.TokenSpendByModel(t),
            wingmanUsage = r.WingmanUsage(t),
            agentDrivenUsage = r.AgentDrivenUsage(t),
            agentsSinceUtc = r.AgentsSinceUtc(t),
            modelsSinceUtc = r.ModelsSinceUtc,
        }).ToList(), Pretty);
}
