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

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal(DeviceA, doc.RootElement[0].GetProperty("DeviceHash").GetString());
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

    [Fact]
    public void Load_ExistingRecordsKeepTheirRecordedDevice_SoNoAttributionIsInvented()
    {
        // Records written before this partition existed already carry the device hash the SERVER stamped on
        // them, so partitioning the reads attributes nothing that was not already recorded - no migration.
        var legacy = $"[{{\"TurnId\":\"legacy-a\",\"ReceivedAtUtc\":\"{DateTime.UtcNow:o}\",\"DeviceHash\":\"{DeviceA}\"}},"
                   + $"{{\"TurnId\":\"legacy-b\",\"ReceivedAtUtc\":\"{DateTime.UtcNow:o}\",\"DeviceHash\":\"{DeviceB}\"}}]";
        File.WriteAllText(_path, legacy);

        var store = new CarModeTelemetryStore(_path, _ => { });

        Assert.Equal(new[] { "legacy-a" }, store.Recent(DeviceA, 10).Select(r => r.TurnId).ToArray());
        Assert.Equal(new[] { "legacy-b" }, store.Recent(DeviceB, 10).Select(r => r.TurnId).ToArray());
    }

    [Fact]
    public void Load_PurgesRecordsThatCarryNoDevice_RatherThanGuessingAnOwner()
    {
        // A record with no recorded device has no attribution at all. Inventing one would be the forbidden
        // half-partition, so it is purged; the attributed record beside it is untouched.
        var mixed = $"[{{\"TurnId\":\"orphan\",\"ReceivedAtUtc\":\"{DateTime.UtcNow:o}\",\"DeviceHash\":\"\"}},"
                  + $"{{\"TurnId\":\"owned\",\"ReceivedAtUtc\":\"{DateTime.UtcNow:o}\",\"DeviceHash\":\"{DeviceA}\"}}]";
        File.WriteAllText(_path, mixed);

        var store = new CarModeTelemetryStore(_path, _ => { });
        store.Add(DeviceA, Record("fresh")); // any write persists the store, so the purge reaches the file

        Assert.Equal(new[] { "fresh", "owned" }, store.Recent(DeviceA, 10).Select(r => r.TurnId).ToArray()); // positive control
        Assert.Empty(store.Recent(DeviceB, 10));

        // The orphan is GONE from the stored document - not merely filtered out of one read path, where it
        // would sit forever consuming the growth-guard budget and waiting for a future unfiltered reader.
        using var doc = JsonDocument.Parse(File.ReadAllText(_path));
        var storedTurnIds = doc.RootElement.EnumerateArray().Select(r => r.GetProperty("TurnId").GetString()).ToArray();
        Assert.Equal(new[] { "owned", "fresh" }, storedTurnIds);
    }

    // ---- Contention: one device must not push another device's records out ----

    [Fact]
    public void GrowthGuard_EvictsFromTheBusiestDevice_NeverFromAQuietOne()
    {
        // The file cap is a size guard, not a licence to delete a neighbour's data. A device that floods the
        // store may only evict its own records while it is the largest partition.
        var store = new CarModeTelemetryStore(_path, _ => { }, maxRecords: 5);
        store.Add(DeviceA, Record("a-keep"));

        for (var i = 0; i < 20; i++) store.Add(DeviceB, Record($"b-{i}"));

        var a = store.Recent(DeviceA, 100);
        Assert.Single(a);                                  // the quiet device kept its record
        Assert.Equal("a-keep", a[0].TurnId);
        Assert.Equal(4, store.Count(DeviceB));             // the flooder absorbed the whole eviction
    }
}
