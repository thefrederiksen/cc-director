using System.Reflection;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The statistics READ path cannot return another tenant's row - and it cannot because no method WOULD
/// return one, not because every call site remembers to filter.
///
/// The distinction is the whole point of these tests, so it is worth stating plainly. Before the port,
/// <c>SessionCounts</c> ran <c>SELECT column, COUNT(*) FROM repo_session GROUP BY column</c> with no tenant
/// filter at all, and both <c>LoadMirror</c> membership reads did the same. Nothing leaked, because the two
/// call sites only ever looked up an id they had already obtained from a tenant-filtered source. That is
/// safety by every call site remembering, which is exactly the shape that produced the missions leak
/// (devthrottle_internal#1039) - a fourth call site that looked an id up from anywhere else, or simply
/// enumerated the returned dictionary, would have leaked, and nothing in the code would have stopped it.
///
/// So these assertions are deliberately made against the ACCESSOR, not against the rendered page. The page
/// output is identical either way; that is what made the defect invisible. Every test below turns red
/// against the unfiltered accessor and green against the tenant-scoped one.
///
/// <c>repo_session</c> and <c>agent_session</c> still carry no tenant column - that is schema version 5's
/// shape and the port carries it forward unchanged. The scope comes from an explicit join to the identity
/// table, which does carry the tenant.
/// </summary>
public sealed class GatewayStatsReadTenantScopeTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly TenantId TenantWithNothing = new("tenant-c");

    private readonly string _dir;
    private readonly string _path;

    public GatewayStatsReadTenantScopeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "gateway-stats.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private static SessionDto Session(string id, string repoName, string agent, long turns, long chars)
    {
        var dto = new SessionDto
        {
            SessionId = id,
            RepoName = repoName,
            RepoPath = "D:\\Repos\\" + repoName,
            Agent = agent,
            InputStats = new InputStatsDto(),
        };
        dto.InputStats!.Buckets.Add(new InputStatBucketDto
        {
            Modality = "typed",
            Surface = "desktop",
            Turns = turns,
            Characters = chars,
        });
        return dto;
    }

    // Two tenants, each driving its own repository and its own agent, and each using the SAME bare session
    // ids - the collision the tenant key exists to survive.
    private GatewayInputStatsAggregator SeedTwoTenants()
    {
        var agg = new GatewayInputStatsAggregator(_path);

        agg.Observe(Session("s1", "owner/alpha", "ClaudeCode", 3, 30), tenant: TenantA);
        agg.Observe(Session("s2", "owner/alpha", "ClaudeCode", 4, 40), tenant: TenantA);

        agg.Observe(Session("s1", "owner/beta", "Codex", 5, 50), tenant: TenantB);
        agg.Observe(Session("s2", "owner/beta", "Codex", 6, 60), tenant: TenantB);
        agg.Observe(Session("s3", "owner/beta", "Codex", 7, 70), tenant: TenantB);

        return agg;
    }

    [Fact]
    public void RepoSessionCounts_ReturnsOnlyTheAskedTenantsRepositories()
    {
        using var agg = SeedTwoTenants();

        var forA = agg.RepoSessionCounts(TenantA);
        var forB = agg.RepoSessionCounts(TenantB);

        // One repository each. The unfiltered accessor returned BOTH tenants' repositories to both callers.
        Assert.Single(forA);
        Assert.Single(forB);

        // And the surrogate ids do not overlap, which is what makes the counts unmistakably each tenant's own.
        Assert.Empty(forA.Keys.Intersect(forB.Keys));

        // Tenant A drove two distinct sessions into its repository; tenant B drove three into its own.
        Assert.Equal(2, forA.Values.Single());
        Assert.Equal(3, forB.Values.Single());
    }

    [Fact]
    public void AgentSessionCounts_ReturnsOnlyTheAskedTenantsAgents()
    {
        using var agg = SeedTwoTenants();

        var forA = agg.AgentSessionCounts(TenantA);
        var forB = agg.AgentSessionCounts(TenantB);

        Assert.Single(forA);
        Assert.Single(forB);
        Assert.Empty(forA.Keys.Intersect(forB.Keys));
        Assert.Equal(2, forA.Values.Single());
        Assert.Equal(3, forB.Values.Single());
    }

    [Fact]
    public void SessionCounts_ForATenantWithNoRowsAtAll_ReturnNothing()
    {
        using var agg = SeedTwoTenants();

        // The sharpest form of the property: a tenant that has never folded anything must be told about
        // nothing. The unfiltered accessor handed this caller every repository and every agent on the store.
        Assert.Empty(agg.RepoSessionCounts(TenantWithNothing));
        Assert.Empty(agg.AgentSessionCounts(TenantWithNothing));
    }

    [Fact]
    public void SessionCounts_SurviveARestart_StillScopedToTheAskedTenant()
    {
        using (var seeding = SeedTwoTenants()) { }

        // A restart rebuilds the mirror from the store. The counts are read from the store either way, so
        // this is the same property asserted against a process that never saw the writes happen.
        using var reopened = new GatewayInputStatsAggregator(_path);

        Assert.Single(reopened.RepoSessionCounts(TenantA));
        Assert.Single(reopened.AgentSessionCounts(TenantA));
        Assert.Empty(reopened.RepoSessionCounts(TenantWithNothing));
        Assert.Empty(reopened.AgentSessionCounts(TenantWithNothing));
    }

    /// <summary>
    /// The membership mirror is keyed by (tenant, surrogate id, session id) after a restart, and the tenant on
    /// each entry is the one the identity row names.
    ///
    /// Read by reflection deliberately. This is an INTERNAL invariant with no behavioural shadow: the surrogate
    /// ids really are tenant-specific, so a mirror keyed without the tenant produces identical output, which is
    /// precisely why the weaker key survived. There is no page to look at, so the field is what gets asserted.
    /// It turns red if the join reads the wrong identity table, or drops the tenant, or attributes an entry to
    /// the wrong owner.
    /// </summary>
    [Fact]
    public void LoadMirror_KeysMembershipByTenant_AndAttributesEachEntryToItsOwnTenant()
    {
        using (var seeding = SeedTwoTenants()) { }

        using var reopened = new GatewayInputStatsAggregator(_path);

        var repoSessions = MirrorSet(reopened, "_repoSessions");
        var agentSessions = MirrorSet(reopened, "_agentSessions");

        // Tenant A folded two sessions, tenant B three - against one repository and one agent each.
        Assert.Equal(2, repoSessions.Count(e => e.Tenant == TenantA));
        Assert.Equal(3, repoSessions.Count(e => e.Tenant == TenantB));
        Assert.Equal(2, agentSessions.Count(e => e.Tenant == TenantA));
        Assert.Equal(3, agentSessions.Count(e => e.Tenant == TenantB));

        // No entry is attributed to a tenant that never folded, and the two tenants share no surrogate id even
        // though they share the bare session ids "s1" and "s2".
        Assert.DoesNotContain(repoSessions, e => e.Tenant == TenantWithNothing);
        Assert.Empty(repoSessions.Where(e => e.Tenant == TenantA).Select(e => e.Id)
            .Intersect(repoSessions.Where(e => e.Tenant == TenantB).Select(e => e.Id)));
        Assert.Contains(repoSessions, e => e.Tenant == TenantA && e.SessionId == "s1");
        Assert.Contains(repoSessions, e => e.Tenant == TenantB && e.SessionId == "s1");
    }

    private static List<(TenantId Tenant, long Id, string SessionId)> MirrorSet(
        GatewayInputStatsAggregator aggregator, string fieldName)
    {
        var field = typeof(GatewayInputStatsAggregator)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        // A rename would otherwise leave this reading null and asserting nothing - an absent field must fail
        // loudly, never pass as "no entries".
        var value = field!.GetValue(aggregator);
        Assert.NotNull(value);

        return ((IEnumerable<(TenantId Tenant, long Id, string SessionId)>)value!).ToList();
    }
}
