using CcDirector.Gateway.MissionNotes;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the Gateway-owned mission-WHY store (Mission Screen mission, Phase 1b, issue #1405):
/// the durable, shared <c>missionKey -&gt; why</c> map behind the Mission Screen. Covers set/get/all,
/// the empty-why-unsets rule, key normalization (the same lower-cased grouping key the Cockpit derives),
/// and the persistence contract (write-through + reload on a fresh store + corrupt quarantine). Every
/// test uses an isolated temp path so it never touches the real mission-notes.json.
/// </summary>
public sealed class MissionNoteStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-missionnotes-" + Guid.NewGuid().ToString("N"));

    private string Path_ => System.IO.Path.Combine(_dir, "mission-notes.json");

    private static readonly DateTime Now = new(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Set_then_Get_returns_the_why_with_the_display_name_and_updated_time()
    {
        var store = new MissionNoteStore(Path_);
        var note = store.Set("Gateway Cleanup", "Kill Director remote REST; all traffic onto the tunnel.", Now);

        Assert.NotNull(note);
        Assert.Equal("gateway cleanup", note!.Key);
        Assert.Equal("Gateway Cleanup", note.Mission);
        Assert.Equal("Kill Director remote REST; all traffic onto the tunnel.", note.Why);
        Assert.Equal(Now, note.UpdatedAtUtc);

        var got = store.Get("Gateway Cleanup");
        Assert.NotNull(got);
        Assert.Equal("Kill Director remote REST; all traffic onto the tunnel.", got!.Why);
    }

    [Fact]
    public void Get_matches_case_insensitively_on_the_normalized_key()
    {
        var store = new MissionNoteStore(Path_);
        store.Set("Car Mode", "Hands-free fleet voice from the phone.", Now);

        // A different display casing resolves to the same note (the groupByMission key is lower-cased).
        Assert.NotNull(store.Get("car mode"));
        Assert.NotNull(store.Get("CAR MODE"));
        Assert.Equal("Car Mode", store.Get("car MODE")!.Mission);
    }

    [Fact]
    public void Set_again_overwrites_the_same_key_and_refreshes_the_display_name_and_time()
    {
        var store = new MissionNoteStore(Path_);
        store.Set("mobile resilience", "first why", Now);
        var later = Now.AddMinutes(30);
        var note = store.Set("Mobile Resilience", "second why", later);

        Assert.NotNull(note);
        Assert.Equal("second why", note!.Why);
        Assert.Equal("Mobile Resilience", note.Mission);   // display refreshed to the latest write
        Assert.Equal(later, note.UpdatedAtUtc);
        Assert.Single(store.All());                        // still one note under the one key, not two
    }

    [Fact]
    public void An_empty_or_whitespace_why_unsets_the_note()
    {
        var store = new MissionNoteStore(Path_);
        store.Set("Snooze Length", "a real why", Now);
        Assert.NotNull(store.Get("Snooze Length"));

        // Empty why clears it (the card shows its "no why set" flag again).
        var cleared = store.Set("Snooze Length", "   ", Now);
        Assert.Null(cleared);
        Assert.Null(store.Get("Snooze Length"));
        Assert.Empty(store.All());

        // Clearing an already-absent note is a no-op that returns null.
        Assert.Null(store.Set("Snooze Length", "", Now));
    }

    [Fact]
    public void Set_trims_outer_whitespace_from_the_stored_why()
    {
        var store = new MissionNoteStore(Path_);
        var note = store.Set("Local Files", "   clickable remote viewer   ", Now);
        Assert.Equal("clickable remote viewer", note!.Why);
    }

    [Fact]
    public void Set_with_a_blank_mission_is_rejected()
    {
        var store = new MissionNoteStore(Path_);
        Assert.Throws<ArgumentException>(() => store.Set("   ", "why", Now));
    }

    [Fact]
    public void All_returns_a_stable_snapshot_detached_from_the_live_store()
    {
        var store = new MissionNoteStore(Path_);
        store.Set("Alpha", "a", Now);
        store.Set("Beta", "b", Now);

        var snapshot = store.All();
        store.Set("Alpha", "", Now);   // clear after snapshotting

        Assert.Equal(2, snapshot.Count);          // the snapshot did not change under us
        Assert.Single(store.All());               // the live store did
        Assert.Equal(new[] { "alpha", "beta" }, snapshot.Select(n => n.Key).ToArray()); // ordered by key
    }

    [Fact]
    public void A_set_why_survives_a_restart_reloaded_from_disk()
    {
        var store1 = new MissionNoteStore(Path_);
        store1.Set("Gateway Cleanup", "all traffic onto the tunnel", Now);

        // A fresh store over the same file = a Gateway restart. The WHY must reload.
        var store2 = new MissionNoteStore(Path_);
        var got = store2.Get("gateway cleanup");
        Assert.NotNull(got);
        Assert.Equal("all traffic onto the tunnel", got!.Why);
        Assert.Equal("Gateway Cleanup", got.Mission);
    }

    [Fact]
    public void A_corrupt_file_is_quarantined_and_the_store_starts_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not valid json ");

        var store = new MissionNoteStore(Path_);   // must not throw - the Gateway still boots
        Assert.Empty(store.All());

        var quarantined = Directory.GetFiles(_dir, "mission-notes.json.corrupt-*");
        Assert.Single(quarantined);                // the bad bytes were preserved, not overwritten
    }
}
