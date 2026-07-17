using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="WorkListStore"/> (issue #273) over the EF data layer (Hosted Gateway mission,
/// Step 1b): create/append/round-trip, mixed-source ordering, reorder, remove-by-source+id, the
/// single-consumer claim/refusal, and the case-insensitive name behaviour (enforced in code via
/// StringComparer.OrdinalIgnoreCase; exact full-Unicode parity is proven in
/// <see cref="WorkListStoreCaseParityTests"/>). Persistence and import are covered by
/// <see cref="WorkListStorePersistenceTests"/>.
/// </summary>
public sealed class WorkListStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();

    private WorkListStore NewStore() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));

    public void Dispose() => _h.Dispose();

    private static WorkListItemRef Ref(string source, string id, string? area = null) =>
        new() { Source = source, Id = id, Area = area };

    [Fact]
    public void Create_ThenGet_ReturnsEmptyList()
    {
        var store = NewStore();

        Assert.True(store.Create("backlog"));

        var list = store.Get("backlog");
        Assert.NotNull(list);
        Assert.Equal("backlog", list.Name);
        Assert.Empty(list.Items);
        Assert.Null(list.Consumer);
    }

    [Fact]
    public void Create_DuplicateName_ReturnsFalse()
    {
        var store = NewStore();
        store.Create("backlog");

        Assert.False(store.Create("backlog"));
    }

    [Fact]
    public void Names_AreCaseInsensitive_Create_Get_Claim_AllAddressTheSameRow()
    {
        var store = NewStore();
        Assert.True(store.Create("MyList"));

        // Get with a different case hits the same row (the stored name keeps its original case).
        var byLower = store.Get("mylist");
        Assert.NotNull(byLower);
        Assert.Equal("MyList", byLower.Name);
        Assert.Equal("MyList", store.Get("MYLIST")!.Name);

        // A create that differs only in case collides (returns false), exactly as the OrdinalIgnoreCase
        // dictionary did - no second row is created.
        Assert.False(store.Create("mylist"));
        Assert.False(store.Create("MYLIST"));
        Assert.Single(store.ListAll());

        // Claim/AppendItem/Release address the same row through any casing.
        Assert.True(store.AppendItem("MYLIST", Ref("github", "1")));
        Assert.Single(store.Get("mylist")!.Items);
        Assert.Equal(WorkListStore.ClaimResult.Granted, store.Claim("MYLIST", "tok"));
        Assert.Equal(WorkListStore.ClaimResult.AlreadyClaimed, store.Claim("mylist", "tok2"));
        Assert.True(store.Release("MyList"));
        Assert.Equal(WorkListStore.ClaimResult.Granted, store.Claim("mylist", "tok3"));
    }

    // Full-Unicode case parity (accented Latin, the long-s U+017F edge, astral) is proven exactly against
    // StringComparer.OrdinalIgnoreCase in WorkListStoreCaseParityTests.

    [Fact]
    public void AppendItem_ThreeItems_PreservesAppendOrder()
    {
        var store = NewStore();
        store.Create("backlog");

        store.AppendItem("backlog", Ref("github", "262", "Gateway"));
        store.AppendItem("backlog", Ref("github", "263"));
        store.AppendItem("backlog", Ref("github", "264"));

        var list = store.Get("backlog");
        Assert.NotNull(list);
        Assert.Equal(new[] { "262", "263", "264" }, list.Items.Select(i => i.Id).ToArray());
        Assert.Equal("Gateway", list.Items[0].Area);
    }

    [Fact]
    public void AppendItem_MixedSources_AllStoredInOrderWithSourcePreserved()
    {
        var store = NewStore();
        store.Create("backlog");

        store.AppendItem("backlog", Ref("github", "262"));
        store.AppendItem("backlog", Ref("devops", "1203"));
        store.AppendItem("backlog", Ref("jira", "CCD-44"));

        var list = store.Get("backlog");
        Assert.NotNull(list);
        Assert.Equal(new[] { "github", "devops", "jira" }, list.Items.Select(i => i.Source).ToArray());
        Assert.Equal(new[] { "262", "1203", "CCD-44" }, list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void AppendItem_NoSuchList_ReturnsFalse()
    {
        var store = NewStore();

        Assert.False(store.AppendItem("ghost", Ref("github", "1")));
    }

    [Fact]
    public void Reorder_ReversedArray_ReflectsNewOrder()
    {
        var store = NewStore();
        store.Create("backlog");
        store.AppendItem("backlog", Ref("github", "1"));
        store.AppendItem("backlog", Ref("github", "2"));
        store.AppendItem("backlog", Ref("github", "3"));

        var reordered = new List<WorkListItemRef> { Ref("github", "3"), Ref("github", "1"), Ref("github", "2") };
        Assert.True(store.Reorder("backlog", reordered));

        var list = store.Get("backlog");
        Assert.NotNull(list);
        Assert.Equal(new[] { "3", "1", "2" }, list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void RemoveItem_BySourceAndId_RemovesOnlyThatItem_KeepsOrder()
    {
        var store = NewStore();
        store.Create("backlog");
        store.AppendItem("backlog", Ref("github", "1"));
        store.AppendItem("backlog", Ref("devops", "2"));
        store.AppendItem("backlog", Ref("github", "3"));

        Assert.True(store.RemoveItem("backlog", "devops", "2"));

        var list = store.Get("backlog");
        Assert.NotNull(list);
        Assert.Equal(new[] { "1", "3" }, list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void RemoveItem_SourceIsCaseInsensitive_IdIsExact()
    {
        var store = NewStore();
        store.Create("backlog");
        store.AppendItem("backlog", Ref("GitHub", "1"));

        // id must match exactly; source folds case.
        Assert.False(store.RemoveItem("backlog", "github", "1x"));
        Assert.True(store.RemoveItem("backlog", "github", "1"));
        Assert.Empty(store.Get("backlog")!.Items);
    }

    [Fact]
    public void Claim_FirstSucceeds_SecondRefused_ReleaseThenReclaimSucceeds()
    {
        var store = NewStore();
        store.Create("backlog");

        Assert.Equal(WorkListStore.ClaimResult.Granted, store.Claim("backlog", "consumer-a"));
        Assert.Equal(WorkListStore.ClaimResult.AlreadyClaimed, store.Claim("backlog", "consumer-b"));

        Assert.True(store.Release("backlog"));
        Assert.Equal(WorkListStore.ClaimResult.Granted, store.Claim("backlog", "consumer-b"));
    }

    [Fact]
    public void Claim_NoSuchList_ReturnsNoSuchList()
    {
        var store = NewStore();

        Assert.Equal(WorkListStore.ClaimResult.NoSuchList, store.Claim("ghost", "consumer-a"));
    }

    [Fact]
    public void Release_NoSuchList_ReturnsFalse_UnclaimedIsNoOpTrue()
    {
        var store = NewStore();
        store.Create("backlog");

        Assert.False(store.Release("ghost"));
        Assert.True(store.Release("backlog")); // already unclaimed -> no-op true
    }

    [Fact]
    public void Constructor_NullDb_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkListStore(null!, _h.LegacyPath("x.json")));
    }

    [Fact]
    public void Constructor_EmptyLegacyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new WorkListStore(Db, " "));
    }

    [Fact]
    public void StoredList_HasNoStatusField()
    {
        // The DTO type itself must carry only name/items/consumer - no status/flow property.
        var props = typeof(WorkListDto).GetProperties().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "Name", "Items", "Consumer" }.OrderBy(n => n), props.OrderBy(n => n));
        Assert.DoesNotContain("Status", props);
        Assert.DoesNotContain("Flow", props);
    }
}
