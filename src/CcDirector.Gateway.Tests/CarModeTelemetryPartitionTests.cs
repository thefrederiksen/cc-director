using System.Text.Json;
using CcDirector.Gateway.CarMode;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Car Mode telemetry is PARTITIONED BY DEVICE CREDENTIAL (hosted-Gateway collection census row 40). These
/// tests hold the store to the two facts that make the partition real, and they are deliberately kept as
/// TWO SEPARATE FACTS rather than one end-to-end assertion:
///
///   1. THE WRITE RECORDS THE PARTITION. The stored record's device hash is the trusted, credential-derived
///      one the caller was given, never anything the caller put in the body. A read-only filter would be a
///      deferred leak - unpartitioned records would keep piling up behind it - so the write is proven on
///      its own, by reading back what was actually stored.
///   2. THE READS FILTER BY THE PARTITION. Neither the record list nor the count ever returns another
///      device's data.
///
/// Every cross-device assertion carries a POSITIVE CONTROL in the same test: the device reads its OWN
/// record back on the same call. Without it, a failed seed would produce an empty answer that looks exactly
/// like isolation.
/// </summary>
public sealed class CarModeTelemetryPartitionTests : IDisposable
{
    private const string DeviceA = "aaaa11112222";
    private const string DeviceB = "bbbb33334444";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"carmode-telemetry-part-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".corrupt-*"))
            File.Delete(f);
    }

    private static CarModeTelemetryRecord Record(string turnId, string? claimedDeviceHash = null) => new()
    {
        TurnId = turnId,
        ReceivedAtUtc = DateTime.UtcNow.ToString("o"),
        DeviceHash = claimedDeviceHash ?? "",
        GatewayVersion = "1.0.0+test",
        TotalTurnMs = 2500,
    };

    // ---- Fact 1: the WRITE records the partition ----

    [Fact]
    public void Add_StoresTheTrustedPartition_NotTheOneTheRecordCarried()
    {
        // The caller's record claims to belong to device B. The write must file it under the trusted
        // partition it was called with (device A) and nowhere else.
        var store = new CarModeTelemetryStore(_path, _ => { });

        store.Add(DeviceA, Record("claims-b", claimedDeviceHash: DeviceB));

        var mine = store.Recent(DeviceA, 10);
        Assert.Single(mine);                              // positive control: A really did seed a record
        Assert.Equal("claims-b", mine[0].TurnId);
        Assert.Equal(DeviceA, mine[0].DeviceHash);        // the STORED partition is the trusted one
        Assert.Empty(store.Recent(DeviceB, 10));          // and the claimed device got nothing
    }

    [Fact]
    public void Add_PersistsTheTrustedPartitionToDisk_SoItSurvivesARestart()
    {
        // The partition must be RECORDED, not merely applied in memory: a reload of the file must still
        // attribute the record to the writing device only.
        new CarModeTelemetryStore(_path, _ => { }).Add(DeviceA, Record("persisted", claimedDeviceHash: DeviceB));

        var reopened = new CarModeTelemetryStore(_path, _ => { });

        var mine = reopened.Recent(DeviceA, 10);
        Assert.Single(mine);                              // positive control after the reload
        Assert.Equal(DeviceA, mine[0].DeviceHash);
        Assert.Empty(reopened.Recent(DeviceB, 10));
    }

    [Fact]
    public void Add_RawJsonOnDisk_CarriesTheWritingDevice()
    {
        // Read the stored document directly, not through the store's own reader, so the write is proven by
        // the artifact rather than by the same code path that filters.
        new CarModeTelemetryStore(_path, _ => { }).Add(DeviceA, Record("on-disk", claimedDeviceHash: DeviceB));

        using var doc = JsonDocument.Parse(File.ReadAllText(_path));

        // The store persists a versioned envelope: an object carrying a Version and the Records array.
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        var records = doc.RootElement.GetProperty("Records");
        Assert.Equal(1, records.GetArrayLength());
        Assert.Equal(DeviceA, records[0].GetProperty("DeviceHash").GetString());
    }

    [Fact]
    public void Add_BlankPartition_Throws_RatherThanWritingAnUnpartitionedRecord()
    {
        var store = new CarModeTelemetryStore(_path, _ => { });

        Assert.Throws<ArgumentException>(() => store.Add("", Record("no-owner")));
        Assert.Throws<ArgumentException>(() => store.Add("   ", Record("no-owner")));
    }

    // ---- Fact 2: the READS filter by the partition ----

    [Fact]
    public void Recent_ReturnsOnlyTheCallingDevicesRecords()
    {
        var store = new CarModeTelemetryStore(_path, _ => { });
        store.Add(DeviceA, Record("a-one"));
        store.Add(DeviceB, Record("b-one"));
        store.Add(DeviceA, Record("a-two"));

        var a = store.Recent(DeviceA, 100);
        var b = store.Recent(DeviceB, 100);

        // Positive control on each side: each device sees its OWN turns, so an empty answer could not
        // masquerade as isolation.
        Assert.Equal(new[] { "a-two", "a-one" }, a.Select(r => r.TurnId).ToArray());
        Assert.Equal(new[] { "b-one" }, b.Select(r => r.TurnId).ToArray());
        // And neither side sees the other's.
        Assert.DoesNotContain(a, r => r.TurnId.StartsWith("b-", StringComparison.Ordinal));
        Assert.DoesNotContain(b, r => r.TurnId.StartsWith("a-", StringComparison.Ordinal));
    }

    [Fact]
    public void Count_IsThisDevicesCount_NotTheProcessWideTotal()
    {
        var store = new CarModeTelemetryStore(_path, _ => { });
        store.Add(DeviceA, Record("a-one"));
        store.Add(DeviceB, Record("b-one"));
        store.Add(DeviceB, Record("b-two"));

        Assert.Equal(1, store.Count(DeviceA)); // positive control: A's own count is visible
        Assert.Equal(2, store.Count(DeviceB)); // positive control: B's own count is visible
    }

    [Fact]
    public void Add_ReturnsThisDevicesHeldCount_NotTheProcessWideTotal()
    {
        var store = new CarModeTelemetryStore(_path, _ => { });
        store.Add(DeviceB, Record("b-one"));
        store.Add(DeviceB, Record("b-two"));

        var heldForA = store.Add(DeviceA, Record("a-one"));

        Assert.Equal(1, heldForA); // A holds one, even though the store holds three
    }

    [Fact]
    public void Recent_ALimitNeverBorrowsFromAnotherDevice()
    {
        // A limit must be applied AFTER the partition, never before it: filling the slice from the global
        // tail would hand a quiet device its neighbour's turns.
        var store = new CarModeTelemetryStore(_path, _ => { });
        store.Add(DeviceA, Record("a-one"));
        for (var i = 0; i < 5; i++) store.Add(DeviceB, Record($"b-{i}"));

        var a = store.Recent(DeviceA, 3);

        Assert.Single(a);                    // positive control: A's own record is returned
        Assert.Equal("a-one", a[0].TurnId);
    }

    [Fact]
    public void Recent_BlankPartition_Throws_RatherThanReturningEveryDevice()
    {
        var store = new CarModeTelemetryStore(_path, _ => { });
        store.Add(DeviceA, Record("a-one"));

        Assert.Throws<ArgumentException>(() => store.Recent("", 100));
        Assert.Throws<ArgumentException>(() => store.Count(" "));
    }

    // ---- Records already on disk ----

    /// <summary>Build a VERSIONED store document on disk - the current envelope shape - from raw record JSON
    ///  fragments, so a load test exercises trusted (v2+) records rather than a quarantined legacy array.</summary>
    private void WriteVersionedFile(int version, params string[] recordJson)
        => File.WriteAllText(_path, $"{{\"Version\":{version},\"Records\":[{string.Join(",", recordJson)}]}}");

    private static string RecordJson(string turnId, string deviceHash, DateTime receivedAtUtc)
        => $"{{\"TurnId\":\"{turnId}\",\"ReceivedAtUtc\":\"{receivedAtUtc:o}\",\"DeviceHash\":\"{deviceHash}\"}}";

    [Fact]
    public void Load_VersionedRecordsKeepTheirRecordedDevice_SoNoAttributionIsInvented()
    {
        // Records persisted by this store (a versioned envelope) carry the device hash the AUTH GATE accepted,
        // so partitioning the reads attributes nothing that was not already recorded from trusted context.
        WriteVersionedFile(2,
            RecordJson("v2-a", DeviceA, DateTime.UtcNow),
            RecordJson("v2-b", DeviceB, DateTime.UtcNow));

        var store = new CarModeTelemetryStore(_path, _ => { });

        Assert.Equal(new[] { "v2-a" }, store.Recent(DeviceA, 10).Select(r => r.TurnId).ToArray());
        Assert.Equal(new[] { "v2-b" }, store.Recent(DeviceB, 10).Select(r => r.TurnId).ToArray());
    }

    [Fact]
    public void Load_LegacyUnversionedDocument_IsQuarantined_NotServedAcrossCredentials()
    {
        // A pre-version document is a BARE ARRAY of records. Its DeviceHash values were stamped before the
        // store partitioned on the authenticated credential - by an endpoint that reparsed raw request
        // credentials and could disagree with the gate - so a nonblank legacy hash is NOT a trustworthy
        // partition key. The whole document must be quarantined and NOTHING served, or one credential's turns
        // could be handed to another. Blank-only cleanup would leave exactly these nonblank rows exposed.
        var legacy = $"[{RecordJson("legacy-a", DeviceA, DateTime.UtcNow)},{RecordJson("legacy-b", DeviceB, DateTime.UtcNow)}]";
        File.WriteAllText(_path, legacy);

        var store = new CarModeTelemetryStore(_path, _ => { });

        // Not one legacy record is exposed under any partition.
        Assert.Empty(store.Recent(DeviceA, 10));
        Assert.Empty(store.Recent(DeviceB, 10));
        // The untrusted document was moved aside (quarantined), not deleted or trusted.
        var corrupt = Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".corrupt-*");
        Assert.Single(corrupt);
    }

    [Fact]
    public void Load_OlderVersionedDocument_IsQuarantined_NotTrusted()
    {
        // An envelope from a version BELOW the current one is untrusted for the same reason: its records
        // predate the trust boundary. It is quarantined whole, not partially trusted.
        WriteVersionedFile(1, RecordJson("v1-a", DeviceA, DateTime.UtcNow));

        var store = new CarModeTelemetryStore(_path, _ => { });

        Assert.Empty(store.Recent(DeviceA, 10));
        var corrupt = Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".corrupt-*");
        Assert.Single(corrupt);
    }

    [Fact]
    public void Load_PurgesRecordsThatCarryNoDevice_RatherThanGuessingAnOwner()
    {
        // Even inside a trusted (versioned) document, a record with no recorded device has no attribution at
        // all. Inventing one would be the forbidden half-partition, so it is purged; the attributed record
        // beside it is untouched.
        WriteVersionedFile(2,
            RecordJson("orphan", "", DateTime.UtcNow),
            RecordJson("owned", DeviceA, DateTime.UtcNow));

        var store = new CarModeTelemetryStore(_path, _ => { });

        // The orphan is GONE FROM THE DURABLE FILE, and nothing in this test wrote to the store to make that
        // happen: the file is inspected IMMEDIATELY after construction. A test that added a record first
        // would be performing the flush itself and then crediting the purge with it - it could not fail, and
        // it would pass just as happily against a purge that only ever removed the record from memory and
        // left it in the file until some future turn arrived.
        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        var storedTurnIds = doc.RootElement.GetProperty("Records").EnumerateArray().Select(r => r.GetProperty("TurnId").GetString()).ToArray();
        Assert.Equal(new[] { "owned" }, storedTurnIds);

        // And a second store reading that same file agrees, with the surviving record still attributed.
        var reopened = new CarModeTelemetryStore(_path, _ => { });
        Assert.Equal(new[] { "owned" }, reopened.Recent(DeviceA, 10).Select(r => r.TurnId).ToArray()); // positive control
        Assert.Empty(reopened.Recent(DeviceB, 10));
        Assert.Equal(new[] { "owned" }, store.Recent(DeviceA, 10).Select(r => r.TurnId).ToArray());
    }

    [Fact]
    public void Load_PersistsTheRetentionPrune_WithoutWaitingForTheNextWrite()
    {
        // Same guarantee for the age sweep: an aged-out record must leave the FILE at load, not linger there
        // until a later turn happens to flush it. Nothing in this test writes to the store.
        WriteVersionedFile(2,
            RecordJson("ancient", DeviceA, DateTime.UtcNow.AddDays(-120)),
            RecordJson("recent", DeviceA, DateTime.UtcNow));

        var store = new CarModeTelemetryStore(_path, _ => { });

        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        var storedTurnIds = doc.RootElement.GetProperty("Records").EnumerateArray().Select(r => r.GetProperty("TurnId").GetString()).ToArray();
        Assert.Equal(new[] { "recent" }, storedTurnIds);
        Assert.Equal(new[] { "recent" }, store.Recent(DeviceA, 10).Select(r => r.TurnId).ToArray()); // positive control
    }

    [Fact]
    public void Load_WithNothingToRemove_LeavesTheFileAlone()
    {
        // The control for the two tests above: load only rewrites the file when it actually removed
        // something, so a plain restart is not a write.
        WriteVersionedFile(2, RecordJson("owned", DeviceA, DateTime.UtcNow));
        var before = File.ReadAllText(_path);

        var store = new CarModeTelemetryStore(_path, _ => { });

        Assert.Equal(before, File.ReadAllText(_path));
        Assert.Single(store.Recent(DeviceA, 10)); // positive control: it really did load the record
    }

    // ---- Colliding key: the SAME TurnId under two devices must not cross ----

    [Fact]
    public void CollidingTurnId_UnderTwoDevices_StaysInTwoPartitions_WithAMutationRevertProof()
    {
        // The requested colliding-key proof at the store: the SAME car-mode key (an identical TurnId) written
        // under two DIFFERENT trusted partitions must stay separated, with distinguishable payloads so "each
        // side reads its OWN record" is a real assertion. It also carries a MUTATION/REVERT proof - the two
        // assertions that go red the moment the read filter is removed - so it cannot silently rot into a
        // test that passes against an un-partitioned store.
        const string shared = "same-turn-id";
        var store = new CarModeTelemetryStore(_path, _ => { });
        store.Add(DeviceA, Record(shared) with { BrainMs = 111 });
        store.Add(DeviceB, Record(shared) with { BrainMs = 222 });

        var a = store.Recent(DeviceA, 100);
        var b = store.Recent(DeviceB, 100);

        // Positive control: each device sees exactly its OWN record for the colliding key, with its own payload.
        Assert.Single(a);
        Assert.Single(b);
        Assert.Equal(shared, a[0].TurnId);
        Assert.Equal(shared, b[0].TurnId);
        Assert.Equal(111, a[0].BrainMs);
        Assert.Equal(222, b[0].BrainMs);
        // Cross-credential exclusion: the mutation/revert proof. If the read filter were dropped, each side
        // would return BOTH records (count 2) and the neighbour's payload would appear - these go red.
        Assert.Equal(1, store.Count(DeviceA));
        Assert.Equal(1, store.Count(DeviceB));
        Assert.DoesNotContain(a, r => r.BrainMs == 222);
        Assert.DoesNotContain(b, r => r.BrainMs == 111);
    }

    // ---- Contention: one device must not push another device's records out ----

    [Fact]
    public void GrowthGuard_CapsEachPartitionWithinItself_NeverAcrossDevices()
    {
        // The cap is PER DEVICE, so a flooding device only ever evicts its OWN oldest records down to the cap,
        // and a quiet neighbour keeps everything it has.
        var store = new CarModeTelemetryStore(_path, _ => { }, maxRecords: 5);
        store.Add(DeviceA, Record("a-keep"));

        for (var i = 0; i < 20; i++) store.Add(DeviceB, Record($"b-{i}"));

        var a = store.Recent(DeviceA, 100);
        Assert.Single(a);                                  // the quiet device kept its record
        Assert.Equal("a-keep", a[0].TurnId);
        Assert.Equal(5, store.Count(DeviceB));             // the flooder is capped to its OWN per-device cap
    }

    [Fact]
    public void GrowthGuard_AQuietWritesAddCannotEvictALargerNeighboursRows()
    {
        // The blocker this rework fixes: with a PROCESS-WIDE cap, a quiet writer A crossing the global cap by
        // one would trip an eviction that deleted a LARGER neighbour B's record - a cross-partition deletion.
        // With a per-partition cap, B fills to the cap and A's later single write leaves every one of B's
        // records intact, because A only ever prunes its own partition. This assertion is RED on the old
        // process-wide cap (B would drop to 2) and GREEN here.
        var store = new CarModeTelemetryStore(_path, _ => { }, maxRecords: 3);
        store.Add(DeviceB, Record("b-0"));
        store.Add(DeviceB, Record("b-1"));
        store.Add(DeviceB, Record("b-2"));
        Assert.Equal(3, store.Count(DeviceB)); // B is exactly at the cap

        store.Add(DeviceA, Record("a-keep")); // a single write by the QUIET device

        // B lost nothing - not even its oldest - to A's write, and B's oldest record is still present.
        Assert.Equal(3, store.Count(DeviceB));
        Assert.Contains(store.Recent(DeviceB, 100), r => r.TurnId == "b-0");
        Assert.Equal(new[] { "a-keep" }, store.Recent(DeviceA, 100).Select(r => r.TurnId).ToArray()); // positive control
    }

    // ---- Age retention: one device's Add must not age-prune ANOTHER device's expired rows ----

    /// <summary>Place a record straight into the store's backing list, past every prune path. A unit test has
    ///  no clock seam, and both <see cref="CarModeTelemetryStore.Add"/> and startup Load prune expired rows -
    ///  the very behaviour under test - so an already-aged row that is present in memory but has NOT yet been
    ///  swept (a quiet device on a long-lived Gateway that has not restarted) can only be staged this way.</summary>
    private static void SeedRawRecord(CarModeTelemetryStore store, CarModeTelemetryRecord record)
    {
        var field = typeof(CarModeTelemetryStore).GetField("_records",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var list = (List<CarModeTelemetryRecord>)field.GetValue(store)!;
        list.Add(record);
    }

    private static CarModeTelemetryRecord AgedRecord(string turnId, string deviceHash, int daysOld)
        => Record(turnId, claimedDeviceHash: deviceHash) with { ReceivedAtUtc = DateTime.UtcNow.AddDays(-daysOld).ToString("o") };

    [Fact]
    public void Add_ByOneDevice_DoesNotAgePruneAnotherDevicesExpiredRows()
    {
        // THE RESIDUAL THIS REWORK FIXES: the age prune was global, so credential A's write deleted EXPIRED
        // rows belonging to credential B - a caller's Add mutating a partition that is not its own. A caller's
        // Add must NEVER remove another device's rows, not by the growth cap AND not by age. B's rows have
        // aged past the 90-day window but its Gateway has not restarted (no load-time global sweep), so they
        // are still in memory when an UNRELATED device A posts one fresh turn.
        var store = new CarModeTelemetryStore(_path, _ => { });
        SeedRawRecord(store, AgedRecord("b-old-1", DeviceB, daysOld: 120));
        SeedRawRecord(store, AgedRecord("b-old-2", DeviceB, daysOld: 200));

        store.Add(DeviceA, Record("a-fresh")); // a different credential's write

        // B's expired rows are UNTOUCHED - A's Add pruned only A's own partition. On the reverted (global)
        // age prune, A's Add would delete both of B's expired rows and this drops to 0: revert-proof.
        Assert.Equal(2, store.Count(DeviceB));
        var b = store.Recent(DeviceB, 100);
        Assert.Contains(b, r => r.TurnId == "b-old-1");
        Assert.Contains(b, r => r.TurnId == "b-old-2");
        Assert.Equal(new[] { "a-fresh" }, store.Recent(DeviceA, 100).Select(r => r.TurnId).ToArray()); // positive control
    }

    [Fact]
    public void Add_ByTheOwningDevice_StillAgePrunesItsOwnExpiredRows()
    {
        // The control for the test above: per-partition age retention is still LIVE, not disabled. When the
        // owning device B writes, ITS OWN expired row is aged out - proving the seeded row really is past the
        // window (so the test above is not vacuously green) and that the fix narrowed the prune's scope
        // without switching retention off.
        var store = new CarModeTelemetryStore(_path, _ => { });
        SeedRawRecord(store, AgedRecord("b-old", DeviceB, daysOld: 120));

        store.Add(DeviceB, Record("b-fresh")); // the owning device's own write ages its own partition

        Assert.Equal(new[] { "b-fresh" }, store.Recent(DeviceB, 100).Select(r => r.TurnId).ToArray());
    }
}
