using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1240: per-session Gateway actions must resolve the owning Director through the
/// session-owner cache instead of scanning the whole fleet on every call. These tests exercise
/// <see cref="GatewayEndpoints.ResolveOwnerAsync"/> - the extracted resolution the real
/// LocateSessionAsync delegates to - by counting how many Directors get probed. The probe stands
/// in for the live Control API call, so the three cache paths are verified with no HTTP and no
/// live Director.
/// </summary>
public sealed class SessionOwnerResolveTests
{
    private static DirectorDto Director(string id) =>
        new() { DirectorId = id, ControlEndpoint = $"http://127.0.0.1:0/{id}" };

    private static SessionDto Session(string sid) => new() { SessionId = sid };

    // A fleet of three Directors, only one of which owns the session. The probe records which
    // Directors it was asked about, so a test can assert "one lookup" versus "one per Director".
    private static (IReadOnlyCollection<DirectorDto> directors, System.Func<string, DirectorDto?> getById)
        BuildFleet(params string[] ids)
    {
        var directors = ids.Select(Director).ToList();
        var byId = directors.ToDictionary(d => d.DirectorId);
        return (directors, id => byId.TryGetValue(id, out var d) ? d : null);
    }

    [Fact]
    public async Task ResolveOwner_CacheHit_ProbesOnlyTheCachedDirector()
    {
        var (directors, getById) = BuildFleet("d-1", "d-2", "d-3");
        const string sid = "session-abc";
        var owners = new SessionOwnerCache();
        owners.Remember(sid, "d-2"); // d-2 is the known owner

        var probed = new List<string>();
        Task<SessionDto?> Probe(DirectorDto d)
        {
            probed.Add(d.DirectorId);
            return Task.FromResult<SessionDto?>(d.DirectorId == "d-2" ? Session(sid) : null);
        }

        var (director, session) = await GatewayEndpoints.ResolveOwnerAsync(directors, getById, owners, sid, Probe);

        Assert.NotNull(director);
        Assert.Equal("d-2", director!.DirectorId);
        Assert.NotNull(session);
        // The whole point of the issue: exactly one Director lookup, not one per Director.
        Assert.Equal(new[] { "d-2" }, probed);
    }

    [Fact]
    public async Task ResolveOwner_StaleCacheEntry_FallsBackToScanAndReRemembersTheNewOwner()
    {
        var (directors, getById) = BuildFleet("d-1", "d-2", "d-3");
        const string sid = "session-moved";
        var owners = new SessionOwnerCache();
        owners.Remember(sid, "d-2"); // stale: the session actually moved to d-3

        var probed = new List<string>();
        Task<SessionDto?> Probe(DirectorDto d)
        {
            probed.Add(d.DirectorId);
            // d-2 (the cached owner) no longer knows the session; d-3 now owns it.
            return Task.FromResult<SessionDto?>(d.DirectorId == "d-3" ? Session(sid) : null);
        }

        var (director, session) = await GatewayEndpoints.ResolveOwnerAsync(directors, getById, owners, sid, Probe);

        Assert.NotNull(director);
        Assert.Equal("d-3", director!.DirectorId);
        Assert.NotNull(session);
        // The cached owner was tried first, then the full scan located the real owner.
        Assert.Contains("d-2", probed);
        Assert.Contains("d-3", probed);
        // The cache self-corrected: a subsequent action now hits d-3 directly.
        Assert.Equal("d-3", owners.OwnerOf(sid));
    }

    [Fact]
    public async Task ResolveOwner_ColdCache_ScansTheWholeFleetAndRemembersTheOwner()
    {
        var (directors, getById) = BuildFleet("d-1", "d-2", "d-3");
        const string sid = "session-cold";
        var owners = new SessionOwnerCache(); // nothing cached

        var probed = new List<string>();
        Task<SessionDto?> Probe(DirectorDto d)
        {
            probed.Add(d.DirectorId);
            return Task.FromResult<SessionDto?>(d.DirectorId == "d-1" ? Session(sid) : null);
        }

        var (director, session) = await GatewayEndpoints.ResolveOwnerAsync(directors, getById, owners, sid, Probe);

        Assert.NotNull(director);
        Assert.Equal("d-1", director!.DirectorId);
        Assert.NotNull(session);
        // Cold cache behaves exactly as before the issue: every Director is probed.
        Assert.Equal(3, probed.Count);
        // And the owner is now remembered so the next action takes the one-lookup fast path.
        Assert.Equal("d-1", owners.OwnerOf(sid));
    }

    [Fact]
    public async Task ResolveOwner_NoCacheSupplied_ScansTheWholeFleet()
    {
        var (directors, getById) = BuildFleet("d-1", "d-2");
        const string sid = "session-nocache";

        var probed = new List<string>();
        Task<SessionDto?> Probe(DirectorDto d)
        {
            probed.Add(d.DirectorId);
            return Task.FromResult<SessionDto?>(d.DirectorId == "d-2" ? Session(sid) : null);
        }

        var (director, _) = await GatewayEndpoints.ResolveOwnerAsync(directors, getById, owners: null, sid, Probe);

        Assert.NotNull(director);
        Assert.Equal("d-2", director!.DirectorId);
        Assert.Equal(2, probed.Count);
    }

    [Fact]
    public async Task ResolveOwner_UnknownSession_ReturnsNullAndCachesNothing()
    {
        var (directors, getById) = BuildFleet("d-1", "d-2");
        const string sid = "session-nowhere";
        var owners = new SessionOwnerCache();

        var (director, session) = await GatewayEndpoints.ResolveOwnerAsync(
            directors, getById, owners, sid, _ => Task.FromResult<SessionDto?>(null));

        Assert.Null(director);
        Assert.Null(session);
        Assert.Null(owners.OwnerOf(sid));
    }
}
