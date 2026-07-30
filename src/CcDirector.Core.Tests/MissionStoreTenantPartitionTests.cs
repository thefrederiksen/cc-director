using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The tenant partition of <see cref="MissionStore"/> (devthrottle_internal issue #1039).
///
/// One hosted Gateway process serves every account out of ONE missions.json. Before this partition,
/// <c>List()</c> took no tenant and <c>Get()</c> resolved by bare id, so two accounts that shared nothing
/// were served byte-identical mission lists on the deployed service - and a mission name is free text a
/// person typed, so that disclosed what other customers were working on in their own words.
///
/// Every case here is written as an INVARIANT - the rows a tenant is served all belong to that tenant -
/// rather than as "this particular id is absent". An absence test passes for the wrong reason as soon as
/// the id changes shape; the invariant does not. And each refusal case is paired with a PERMITTED case in
/// the same store, because a store that returned nothing to everybody would satisfy every refusal
/// assertion here while being entirely broken.
/// </summary>
public class MissionStoreTenantPartitionTests
{
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    /// <summary>A shared, multi-tenant store: unattributed rows belong to nobody (the hosted shape).</summary>
    private static MissionStore Shared(string path) => new(path, adoptUnattributedAs: null);

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"test_missions_tenant_{Guid.NewGuid()}.json");

    private static void WithTempFile(Action<string> body)
    {
        var path = TempPath();
        try { body(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void List_ServesOnlyTheCallersOwnRows_AndTheOtherTenantStillSeesItsOwn()
    {
        WithTempFile(path =>
        {
            var store = Shared(path);
            store.Create(TenantA, "Payments cutover for Northwind");
            store.Create(TenantA, "Second A mission");
            var bMission = store.Create(TenantB, "B's own mission");

            var servedToB = store.List(TenantB);

            // The invariant: every row B is served is B's. Not "A's id is absent" - this holds however the
            // records are keyed, which is the check that would have caught this leak the first time.
            Assert.All(servedToB, m => Assert.Equal(TenantB.Value, m.TenantId));

            // The permitted half, in the same store and the same breath: B is genuinely served its own row.
            // Without this, a store that returned an empty list to everyone would pass the assertion above.
            Assert.Equal(new[] { bMission.MissionId }, servedToB.Select(m => m.MissionId));

            // And symmetrically for A, so the partition is not just "B sees nothing".
            var servedToA = store.List(TenantA);
            Assert.All(servedToA, m => Assert.Equal(TenantA.Value, m.TenantId));
            Assert.Equal(2, servedToA.Count);
        });
    }

    [Fact]
    public void Get_ByAnotherTenantsId_ResolvesToNothing_ButTheOwnerStillResolvesIt()
    {
        WithTempFile(path =>
        {
            var store = Shared(path);
            var aMission = store.Create(TenantA, "Kickoff with Ana and Ravi");

            // The refusal: naming the id is not a way to read the record.
            Assert.Null(store.Get(TenantB, aMission.MissionId));

            // The control: that same id, asked for by its OWNER, resolves. So the null above is a refusal
            // and not a store that cannot find anything.
            var owned = store.Get(TenantA, aMission.MissionId);
            Assert.NotNull(owned);
            Assert.Equal("Kickoff with Ana and Ravi", owned.MissionName);
        });
    }

    [Fact]
    public void Delete_CannotRemoveAnotherTenantsMission_ButTheOwnerCan()
    {
        WithTempFile(path =>
        {
            var store = Shared(path);
            var aMission = store.Create(TenantA, "A's mission");

            Assert.False(store.Delete(TenantB, aMission.MissionId));
            Assert.NotNull(store.Get(TenantA, aMission.MissionId));   // still there - the write was refused

            Assert.True(store.Delete(TenantA, aMission.MissionId));   // the permitted half
            Assert.Null(store.Get(TenantA, aMission.MissionId));
        });
    }

    [Fact]
    public void Create_StampsTheCallersTenantOnTheRecord_AtWriteTime()
    {
        WithTempFile(path =>
        {
            var store = Shared(path);

            var mission = store.Create(TenantA, "Stamped at write time");

            Assert.Equal(TenantA.Value, mission.TenantId);

            // Read the file rather than the returned object: a read-side filter over unattributed rows is a
            // deferred leak, so the OWNER has to be on disk, in the persisted record.
            var persisted = JsonSerializer.Deserialize<List<Mission>>(
                File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.Equal(TenantA.Value, Assert.Single(persisted).TenantId);
        });
    }

    [Fact]
    public void SharedStore_UnattributedRows_AreServedToNobody_AndAreNotDestroyed()
    {
        WithTempFile(path =>
        {
            // A row exactly as the deployed hosted Gateway holds it today: no owner, because it predates
            // the stamp. It cannot be attributed after the fact, so the store must not guess.
            File.WriteAllText(path, JsonSerializer.Serialize(new[]
            {
                new Mission
                {
                    MissionId = Guid.NewGuid(),
                    MissionName = "legacy unattributed mission",
                    CreatedAt = DateTimeOffset.UtcNow,
                    TenantId = null,
                }
            }));

            var store = Shared(path);
            var legacyId = JsonSerializer.Deserialize<List<Mission>>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })![0].MissionId;

            Assert.Empty(store.List(TenantA));
            Assert.Empty(store.List(TenantB));
            Assert.Null(store.Get(TenantA, legacyId));
            Assert.False(store.Delete(TenantA, legacyId));

            // Quarantined, not deleted: the row is still on disk for whoever holds the deployment to
            // inspect. Reading it back proves the store refused it rather than silently dropping it - which
            // would look identical from the API and destroy the evidence.
            var stillOnDisk = JsonSerializer.Deserialize<List<Mission>>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.Equal("legacy unattributed mission", Assert.Single(stillOnDisk).MissionName);
        });
    }

    [Fact]
    public void SingleTenantStore_AdoptsUnattributedRows_SoSelfHostIsUnchanged()
    {
        WithTempFile(path =>
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new[]
            {
                new Mission
                {
                    MissionId = Guid.NewGuid(),
                    MissionName = "mission from before the stamp",
                    CreatedAt = DateTimeOffset.UtcNow,
                    TenantId = null,
                }
            }));

            // Self-host and the Director: exactly one tenant by construction, so an unattributed row is
            // that tenant's. This is a fact about the deployment, not an inference about the row - and it
            // is why an existing self-host install keeps listing the missions it already had.
            var store = new MissionStore(path, adoptUnattributedAs: TenantId.Local);

            var listed = store.List(TenantId.Local);
            Assert.Equal("mission from before the stamp", Assert.Single(listed).MissionName);
        });
    }

    [Fact]
    public void SingleTenantAdoption_DoesNotLeakToAnotherTenant()
    {
        WithTempFile(path =>
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new[]
            {
                new Mission
                {
                    MissionId = Guid.NewGuid(),
                    MissionName = "adopted by Local only",
                    CreatedAt = DateTimeOffset.UtcNow,
                    TenantId = null,
                }
            }));

            var store = new MissionStore(path, adoptUnattributedAs: TenantId.Local);

            // Adoption names ONE tenant. It is not "anyone may read unattributed rows".
            Assert.Empty(store.List(TenantA));
            Assert.Single(store.List(TenantId.Local));
        });
    }

    [Fact]
    public void EveryAccessor_RefusesAnInvalidTenant()
    {
        WithTempFile(path =>
        {
            var store = Shared(path);
            var unset = default(TenantId);   // bypasses the validating constructor - Value is null

            // A null Value must never reach the ownership comparison, where it would be compared against a
            // row's owner and quietly decide something. Fail loud on every leg instead.
            Assert.Throws<ArgumentException>(() => store.List(unset));
            Assert.Throws<ArgumentException>(() => store.Get(unset, Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => store.Delete(unset, Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => store.Create(unset, "name"));
        });
    }
}
