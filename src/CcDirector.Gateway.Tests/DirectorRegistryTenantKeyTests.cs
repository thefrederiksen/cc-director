using System;
using System.IO;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1847, the WRITE half, at the registry itself.
///
/// The tunnel Hello's director id is chosen by the CLIENT, and the registry used to be keyed by that id
/// alone. So one account, holding its own perfectly valid device key, could say Hello naming ANOTHER
/// account's director id and overwrite that account's entry - machine name, operating system user, process
/// id, client version - and, with the fix for the read half in place but not this one, take ownership of it
/// as well, removing the victim's own Director from the victim's own list.
///
/// The fix is that the registry key is COMPOSITE, (tenantId, directorId). A registration can only ever reach
/// the entry belonging to the tenant doing the registering; naming another tenant's entry is not refused, it
/// is unsayable. That shape was chosen over rejecting the Hello because a machine re-enrolled from one
/// account to another keeps its local director id, and a rejection would abort its every Hello forever with
/// no cure but hand-editing the box - see <see cref="A_director_id_may_move_to_another_tenant"/>, which is
/// that case and must keep working.
///
/// Revert-prove - USE THIS ONE, and read the note under it before substituting your own. In
/// <c>RegisterFromStream</c>, immediately after the key is built, drop any OTHER tenant's entry for the same
/// director id before writing:
/// <code>
/// foreach (var other in _directors.Keys.Where(k => k.DirectorId == directorId &amp;&amp; !k.Tenant.Equals(tenant)).ToArray())
///     _directors.TryRemove(other, out _);
/// </code>
/// That is the pre-fix registry's behaviour - the id alone was the key, so the last writer simply took the
/// entry over. <see cref="One_tenants_registration_cannot_overwrite_anothers"/> then goes RED at the victim
/// losing its own Director, AFTER both of its positive controls have passed, and the other three tests in this
/// class stay GREEN.
///
/// NOT the revert to use: making <c>DirectorKey</c> equality ignore its tenant. It compiles, and it does go
/// red, but it goes red FOR THE WRONG REASON and takes three of the four tests with it. ConcurrentDictionary's
/// indexer replaces a VALUE and keeps the ORIGINAL KEY, so the impostor's write lands on an entry still keyed
/// to the victim's tenant: the impostor's own list comes back EMPTY and the test dies at the impostor's
/// positive control, before it ever reaches the takeover assertion. A revert that reddens a test before the
/// assertion under proof is not evidence about that assertion. Two earlier attempts at this proof failed
/// exactly this way and were discarded rather than written up.
///
/// These drive the registry directly, with no HTTP and no tunnel, so nothing about the result depends on a
/// test harness's own registration side effects. The wired end-to-end proof over the real tunnel and the real
/// mapped endpoint is in <c>SessionServingReadIsolationTests</c>.
/// </summary>
public sealed class DirectorRegistryTenantKeyTests : IDisposable
{
    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-drk-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;

    public DirectorRegistryTenantKeyTests()
    {
        Directory.CreateDirectory(_instancesDir);
        // Constructed but never Start()ed: no file watcher, no sweeper, so the only state under test is what
        // these registrations put there.
        _registry = new DirectorRegistry(_instancesDir);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void One_tenants_registration_cannot_overwrite_anothers()
    {
        Register(Alice, "dir-shared", "ALICE-BOX");

        // Positive control: Alice's entry exists and is truthful BEFORE the attack. Without this, "Alice is
        // untouched" would also hold if Alice had never had an entry at all.
        Assert.Equal("ALICE-BOX", Single(Alice, "dir-shared").MachineName);

        // The attack: Bob, authenticated as himself, registers under Alice's director id.
        Register(Bob, "dir-shared", "BOB-TAKEOVER");

        // Positive control on the attack: Bob's registration really was accepted - under BOB's own tenant.
        // This is what stops the assertions below passing merely because the write silently did nothing.
        Assert.Equal("BOB-TAKEOVER", Single(Bob, "dir-shared").MachineName);

        // The takeover: Alice's entry still carries Alice's facts.
        Assert.Equal("ALICE-BOX", Single(Alice, "dir-shared").MachineName);

        // The denial of service: Alice's Director is still in Alice's own list.
        Assert.Contains("dir-shared", _registry.ListDirectors(Alice).Select(d => d.DirectorId));
    }

    [Fact]
    public void A_director_id_may_move_to_another_tenant()
    {
        // The case that rules OUT rejecting a Hello whose id is already registered elsewhere: a machine
        // re-enrolled from one account to another keeps its local director id. Its registration under the new
        // account must be ACCEPTED, not aborted - a refusal here would brick that machine permanently.
        Register(Alice, "dir-moved", "THE-BOX");
        Register(Bob, "dir-moved", "THE-BOX");

        Assert.Equal("THE-BOX", Single(Bob, "dir-moved").MachineName);
    }

    [Fact]
    public void A_tenant_still_updates_its_own_entry()
    {
        // Control against over-correction: the composite key must not make every re-registration a no-op.
        // A Director re-saying Hello - reconnect, version upgrade, new process id - still refreshes ITS OWN
        // entry in place, and does not accumulate duplicates.
        Register(Alice, "dir-own", "BOX-V1", version: "1.0.0");
        Register(Alice, "dir-own", "BOX-V2", version: "2.0.0");

        var mine = Single(Alice, "dir-own");
        Assert.Equal("BOX-V2", mine.MachineName);
        Assert.Equal("2.0.0", mine.Version);
    }

    [Fact]
    public void The_serving_list_partitions_while_the_internal_view_stays_fleet_global()
    {
        Register(Alice, "dir-a", "ALICE-BOX");
        Register(Bob, "dir-b", "BOB-BOX");

        // What a client is served: its own tenant's Directors, and nothing else.
        Assert.Equal(new[] { "dir-a" }, _registry.ListDirectors(Alice).Select(d => d.DirectorId).ToArray());
        Assert.Equal(new[] { "dir-b" }, _registry.ListDirectors(Bob).Select(d => d.DirectorId).ToArray());

        // Control: the no-tenant overload is the internal aggregation view and DOES see both. Without this,
        // a registry that had simply lost Bob's entry would satisfy the two assertions above.
        var everything = _registry.ListDirectors(CcDirector.Gateway.Tenancy.SystemScope.Grant()).Select(d => d.DirectorId).ToArray();
        Assert.Contains("dir-a", everything);
        Assert.Contains("dir-b", everything);

        // Deny by default on the by-id lookup too: Alice's tenant cannot fetch Bob's Director by naming it.
        Assert.NotNull(_registry.Get(Alice, "dir-a"));
        Assert.Null(_registry.Get(Alice, "dir-b"));
        Assert.NotNull(_registry.Get(Bob, "dir-b"));
        Assert.Null(_registry.Get(Bob, "dir-a"));
    }

    private void Register(TenantId tenant, string directorId, string machineName, string version = "test") =>
        _registry.RegisterFromStream(directorId, machineName, "u", version, 1, DateTime.UtcNow, tenant);

    private Gateway.Contracts.DirectorDto Single(TenantId tenant, string directorId) =>
        Assert.Single(_registry.ListDirectors(tenant), d => d.DirectorId == directorId);
}
