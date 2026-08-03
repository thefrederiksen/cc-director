using System.Text.Json;
using CcDirector.Gateway.MissionNotes;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the Gateway-owned mission-WHY store (Mission Screen mission, Phase 1b, issue #1405):
/// the durable, shared <c>missionKey -&gt; why</c> map behind the Mission Screen, now over the EF data layer
/// (Hosted Gateway mission, Step 1b). Covers set/get/all, the empty-why-unsets rule, key normalization (the
/// same lower-cased grouping key the Cockpit derives), the persistence contract (write-through + reload on a
/// fresh store over the same database), a lossless legacy-JSON import, and the deliberate corrupt-file
/// QUARANTINE (a cosmetic store must not block boot). Every test runs over an isolated on-disk SQLite database.
/// </summary>
public sealed class MissionNoteStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    private static readonly DateTime Now = new(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private string LegacyPath() => _h.LegacyPath("mission-notes-" + Guid.NewGuid().ToString("N") + ".json");
    private MissionNoteStore NewStore() => new(_h.Open(), LegacyPath());

    [Fact]
    public void Set_then_Get_returns_the_why_with_the_display_name_and_updated_time()
    {
        var store = NewStore();
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
        var store = NewStore();
        store.Set("Car Mode", "Hands-free fleet voice from the phone.", Now);

        // A different display casing resolves to the same note (the groupByMission key is lower-cased).
        Assert.NotNull(store.Get("car mode"));
        Assert.NotNull(store.Get("CAR MODE"));
        Assert.Equal("Car Mode", store.Get("car MODE")!.Mission);
    }

    [Fact]
    public void Set_again_overwrites_the_same_key_and_refreshes_the_display_name_and_time()
    {
        var store = NewStore();
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
        var store = NewStore();
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
        var store = NewStore();
        var note = store.Set("Local Files", "   clickable remote viewer   ", Now);
        Assert.Equal("clickable remote viewer", note!.Why);
    }

    [Fact]
    public void Set_with_a_blank_mission_is_rejected()
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(() => store.Set("   ", "why", Now));
    }

    [Fact]
    public void All_returns_a_stable_snapshot_detached_from_the_live_store()
    {
        var store = NewStore();
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
        var store1 = NewStore();
        store1.Set("Gateway Cleanup", "all traffic onto the tunnel", Now);

        // A fresh store over the same database = a Gateway restart. The WHY must reload.
        var store2 = new MissionNoteStore(_h.Open(), LegacyPath());
        var got = store2.Get("gateway cleanup");
        Assert.NotNull(got);
        Assert.Equal("all traffic onto the tunnel", got!.Why);
        Assert.Equal("Gateway Cleanup", got.Mission);
    }

    [Fact]
    public void LegacyJson_ImportedOnce_Lossless_ThenRenamedAside()
    {
        // A legacy mission-notes.json written by the old store (a document with a notes array, camelCase).
        var legacy = LegacyPath();
        WriteLegacyFile(legacy,
            new MissionNoteStore.MissionNote("gateway cleanup", "Gateway Cleanup", "onto the tunnel", Now),
            new MissionNoteStore.MissionNote("car mode", "Car Mode", "hands-free voice", Now.AddMinutes(5)));

        var store = new MissionNoteStore(_h.Open(), legacy);

        // Every field survived, keyed by the normalized name, ordered by key.
        var all = store.All();
        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { "car mode", "gateway cleanup" }, all.Select(n => n.Key).ToArray());
        var gc = store.Get("Gateway Cleanup");
        Assert.NotNull(gc);
        Assert.Equal("Gateway Cleanup", gc!.Mission);
        Assert.Equal("onto the tunnel", gc.Why);
        Assert.Equal(Now, gc.UpdatedAtUtc);

        // The legacy file is renamed aside (kept as a backup), never left to re-import.
        Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(legacy)!, Path.GetFileName(legacy) + ".migrated-*"));

        // A fresh store over the same database does NOT re-import (the file is gone) and still has both.
        Assert.Equal(2, new MissionNoteStore(_h.Open(), legacy).All().Count);
    }

    [Fact]
    public void A_corrupt_file_is_quarantined_and_the_store_starts_empty()
    {
        var legacy = LegacyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "{ this is not valid json ");

        var store = new MissionNoteStore(_h.Open(), legacy);   // must not throw - the Gateway still boots
        Assert.Empty(store.All());

        // The bad bytes were preserved (renamed aside as .corrupt-*), not overwritten, and NOT left in place
        // to re-trip next boot.
        Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(legacy)!, Path.GetFileName(legacy) + ".corrupt-*"));
    }

    private static void WriteLegacyFile(string path, params MissionNoteStore.MissionNote[] notes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(new { Notes = notes }, options));
    }
}
