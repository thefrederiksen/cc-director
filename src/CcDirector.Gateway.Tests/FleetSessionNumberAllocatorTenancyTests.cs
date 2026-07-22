using System;
using System.Collections;
using System.Reflection;
using System.Threading;
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

    /// <summary>
    /// CROSS-TENANT CONCURRENCY (audit H2, Codex residual). Correctness partitioning is not enough: the
    /// allocator must also not SERIALIZE tenants on a shared lock. With one process-global lock, tenant A
    /// holding it (mid-allocation, or just contending) stalls every other tenant's allocation. The fix gives
    /// each tenant's partition its OWN lock, so a caller only ever takes ITS tenant's lock.
    ///
    /// This test holds tenant A's partition lock and proves tenant B allocates concurrently WITHOUT waiting,
    /// while a same-tenant A allocation DOES wait (the destructibility control - it proves the held lock is
    /// the one A's own operations take, so "B proceeded" is real independence, not a lock we grabbed that
    /// nobody uses). Revert to a single shared lock and the tenant-B property assertion reddens: B would then
    /// block on the very lock this test holds.
    /// </summary>
    [Fact]
    public void One_tenant_holding_its_pool_lock_does_not_block_another_tenants_allocation()
    {
        var a = new FleetSessionNumberAllocator();
        // Materialize both partitions so each has a lock object to reach.
        a.Allocate(TenantA, "alice-seed", "dir-a");
        a.Allocate(TenantB, "bob-seed", "dir-b");

        var tenantALock = PoolLockFor(a, TenantA);
        var aBlockedAllocationDone = new ManualResetEventSlim(false);

        lock (tenantALock)
        {
            // Tenant A's partition lock is HELD by this thread; any operation on A's partition must wait.

            // THE PROPERTY - tenant B allocates on ANOTHER thread and completes without waiting on A's lock.
            int? bNumber = null;
            var bDone = new ManualResetEventSlim(false);
            var bThread = new Thread(() =>
            {
                bNumber = a.Allocate(TenantB, "bob-concurrent", "dir-b");
                bDone.Set();
            }) { IsBackground = true };
            bThread.Start();

            Assert.True(bDone.Wait(TimeSpan.FromSeconds(5)),
                "Tenant B's allocation blocked while tenant A held ITS OWN pool lock - the allocator is serializing tenants on a shared lock.");
            Assert.NotNull(bNumber);

            // DESTRUCTIBILITY CONTROL - a same-tenant (A) allocation really DOES block on the held lock.
            var aThread = new Thread(() =>
            {
                a.Allocate(TenantA, "alice-concurrent", "dir-a");
                aBlockedAllocationDone.Set();
            }) { IsBackground = true };
            aThread.Start();

            Assert.False(aBlockedAllocationDone.Wait(TimeSpan.FromSeconds(1)),
                "Tenant A's own allocation completed while A's pool lock was held - the lock held is not the one A's partition uses, so the property assertion above proves nothing.");
        }

        // Once A's lock is released, the previously-blocked A allocation completes - closing the control.
        Assert.True(aBlockedAllocationDone.Wait(TimeSpan.FromSeconds(5)),
            "Tenant A's allocation never completed after its pool lock was released.");
    }

    /// <summary>
    /// Reach the per-tenant partition's own lock object by reflection. This is a concurrency proof, so it must
    /// hold the EXACT lock a caller for that tenant takes - there is no public seam for that by design.
    /// </summary>
    private static object PoolLockFor(FleetSessionNumberAllocator allocator, TenantId tenant)
    {
        var mapField = typeof(FleetSessionNumberAllocator).GetField("_byTenant", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(mapField);
        var map = (IDictionary)mapField!.GetValue(allocator)!;
        var pool = map[tenant];
        Assert.NotNull(pool);
        var lockField = pool!.GetType().GetField("Lock", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(lockField);
        return lockField!.GetValue(pool)!;
    }
}
