using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Audit H2: one tenant's session numbers must be invisible to another tenant's operations.
///
/// WHAT THIS PINS. The allocator was a single process-global pool keyed by BARE session/director ids. A
/// director id and a session id are unique only WITHIN a tenant, not across the fleet, so two tenants
/// sharing an id collided: tenant A's allocate could read tenant B's assignment for that id, A's release
/// could free B's number, A's same-id Director removal freed B's assignments (a duplicated rail number for
/// the innocent tenant), and the one 900-number pool meant one busy tenant starved every other.
///
/// The fix partitions every piece of state by <see cref="TenantId"/>. Each test below has a
/// DESTRUCTIBILITY CONTROL beside its isolation assertion: the owner's OWN operation really does the thing
/// (frees / hands out / exhausts), so "the other tenant survived" cannot be satisfied by an operation that
/// simply did nothing.
///
/// REVERT-PROOF. Collapse the allocator back to one global pool (drop the per-tenant partition so every
/// method shares one BySession/InUse) and every isolation assertion here goes RED while its destructibility
/// control stays GREEN. Separately, restore the removal subscriber to
/// <c>ReleaseForDirector(removal.DirectorId)</c> without the tenant and
/// <see cref="Removal_in_one_tenant_keeps_the_other_tenants_numbers"/> reddens.
/// </summary>
public sealed class FleetSessionNumberAllocatorTenancyTests
{
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    /// <summary>An id held in both tenants at once - legitimate, since an id is unique only within a tenant.</summary>
    private const string SharedDirector = "dir-shared";
    private const string SharedSession = "sess-shared";

    [Fact]
    public void Removal_in_one_tenant_keeps_the_other_tenants_numbers()
    {
        var a = new FleetSessionNumberAllocator();
        // Both tenants own a Director under the SAME id, each with a session.
        a.Allocate(TenantA, "alice-s1", SharedDirector);
        var bobsNumber = a.Allocate(TenantB, "bob-s1", SharedDirector);
        Assert.NotNull(bobsNumber);

        // Tenant A's Director leaves the fleet.
        a.ReleaseForDirector(TenantA, SharedDirector);

        // DESTRUCTIBILITY CONTROL - A's own number really is gone (the removal did fire and did free).
        Assert.Null(a.NumberFor(TenantA, "alice-s1"));
        Assert.Equal(0, a.InUseCount(TenantA));

        // THE PROPERTY - Bob keeps the number the shared-id removal used to free out from under him.
        Assert.Equal(bobsNumber, a.NumberFor(TenantB, "bob-s1"));
        Assert.Equal(1, a.InUseCount(TenantB));
    }

    [Fact]
    public void Release_in_one_tenant_keeps_the_other_tenants_number_for_the_same_session_id()
    {
        var a = new FleetSessionNumberAllocator();
        // The same session-id string used by both tenants (they cannot see each other's, so this is legal).
        var aNum = a.Allocate(TenantA, SharedSession, "dir-a");
        var bNum = a.Allocate(TenantB, SharedSession, "dir-b");
        Assert.NotNull(aNum);
        Assert.NotNull(bNum);

        // Tenant A releases its session.
        a.Release(TenantA, SharedSession);

        // DESTRUCTIBILITY CONTROL - A's own number really was freed.
        Assert.Null(a.NumberFor(TenantA, SharedSession));

        // THE PROPERTY - B's identically-named session still holds its own number.
        Assert.Equal(bNum, a.NumberFor(TenantB, SharedSession));
    }

    [Fact]
    public void Allocate_in_one_tenant_never_reads_the_other_tenants_assignment()
    {
        var a = new FleetSessionNumberAllocator();
        // Advance tenant A's pool first, so A's assignment for the shared id is NOT the lowest number - that
        // way "B read A's" and "B drew its own lowest" produce different values the assertion can tell apart.
        Assert.Equal(FleetSessionNumberAllocator.MinNumber, a.Allocate(TenantA, "alice-filler", "dir-a"));
        var aShared = a.Allocate(TenantA, SharedSession, "dir-a");
        Assert.Equal(FleetSessionNumberAllocator.MinNumber + 1, aShared);

        // Tenant B allocating the SAME id must NOT idempotently read A's assignment across a shared map (the
        // old bug handed A's number back to B). B draws from its OWN empty pool, so it gets the lowest number.
        var bShared = a.Allocate(TenantB, SharedSession, "dir-b");
        Assert.Equal(FleetSessionNumberAllocator.MinNumber, bShared);
        Assert.NotEqual(aShared, bShared);
        Assert.Equal(2, a.InUseCount(TenantA));
        Assert.Equal(1, a.InUseCount(TenantB));
    }

    [Fact]
    public void One_tenant_exhausting_its_pool_leaves_the_other_tenant_a_full_pool()
    {
        var a = new FleetSessionNumberAllocator();
        var total = FleetSessionNumberAllocator.MaxNumber - FleetSessionNumberAllocator.MinNumber + 1;

        // Tenant A allocates every number in its own pool.
        for (int i = 0; i < total; i++)
            Assert.NotNull(a.Allocate(TenantA, $"alice-{i}", "dir-a"));

        // DESTRUCTIBILITY CONTROL - A's pool really is exhausted.
        Assert.Null(a.Allocate(TenantA, "alice-overflow", "dir-a"));

        // THE PROPERTY - tenant B still has a completely fresh pool; A's flood never touched it.
        var bFirst = a.Allocate(TenantB, "bob-1", "dir-b");
        Assert.Equal(FleetSessionNumberAllocator.MinNumber, bFirst);
    }
}
