using CcDirector.Core.Storage;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Dictation delivery tombstones are bounded IN PRODUCTION, and the bound touches ONLY the records it is
/// allowed to touch (issue #1111).
///
/// WHY IT BOOTS A REAL GATEWAY. The designed retirement path for a terminal record is a client ack, and it
/// works - but an ack that will never come (a client that dropped its queue, was reinstalled, or simply never
/// returned) left its tombstone immortal, so the store grew without any ceiling. That was observed live: 28
/// records, every one DELIVERED, the oldest three weeks old. A test that CALLS the sweep cannot detect that,
/// because it passes whether or not anything in production runs it - which is exactly how this root grew
/// unbounded while looking covered, and the same trap its voice-turn sibling fell into. So this test never
/// calls the sweep. It writes records on disk, starts a real <see cref="GatewayHost"/>, and waits for the
/// Gateway to retire the right one on its own.
///
/// WHAT MUST SURVIVE MATTERS MORE THAN WHAT IS REMOVED. This sweep is deliberately not
/// <see cref="VoiceUploadStore.SweepAbandoned"/>, which deletes any aged upload directory without reading
/// its state. FAILED can be restored to PENDING by ClearFailed, and a record still inside its window may yet
/// be re-driven by its client - so age-deleting either would drop a dictation. Those assertions are the
/// point of this test, not the deletion.
///
/// A STALE PENDING RECORD IS NOW ABANDONED IN PLACE, AND THIS TEST CHANGED TO SAY SO. It used to assert that
/// an aged PENDING record survived untouched, and that was right while nothing bounded it. It was also how
/// seven of them came to be stuck on the hosted Gateway on 2026-08-28, the oldest five weeks old, each still
/// refusing human input on a session nobody could unlock. <see cref="VoiceUploadStore.ExpireStalePending"/>
/// now abandons one that has been silent past its bound: the directory and its tombstone SURVIVE (so a late
/// client cannot re-drive the id), the state becomes ABANDONED with a reason that names the server as the
/// cause, and the session lock is released. A FRESH pending record is still untouchable - that half of the
/// old contract is unchanged and is asserted below.
///
/// Only the SCHEDULE is compressed. The thirty-day age cut-off is production's own, and the records are aged
/// into the past on disk, so what runs here is the deployed retention rule rather than a shortened copy.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DictationTombstoneSweepWiringTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";

    private GatewayHost? _gateway;
    private string? _originalRoot;

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-tombstone-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-tombstone-instances-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        _originalRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        GatewayHost.DictationTombstoneSweepScheduleForTests = null;
        if (_gateway is not null) await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _originalRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
    }

    [Fact]
    public async Task RunningGateway_RetiresOldDeliveredTombstone_ButNeverPendingFailedOrFresh()
    {
        var root = CcStorage.DictationUploads();
        var old = DateTime.UtcNow.AddDays(-31);   // past the thirty-day production cut-off
        var recent = DateTime.UtcNow.AddDays(-2); // well inside it

        // The live defect: a DELIVERED record whose client never acknowledged it. Nothing else can ever
        // retire this, so without the sweep it is immortal.
        var deliveredOld = WriteRecord(root, "Delivered", old);
        // ABANDONED is terminal too, and equally unacknowledgeable once the client is gone.
        var abandonedOld = WriteRecord(root, "Abandoned", old);

        // ---- everything below must SURVIVE, however old ----

        // A PENDING record still inside its silence window holds a live session lock and audio still coming.
        // Nothing may touch it, however busy the sweep is. This is the assertion that separates this sweep
        // from the blunt age sweep next door.
        var pendingFresh = WriteRecord(root, "Pending", DateTime.UtcNow.AddHours(-1));
        // FAILED is explicitly NOT terminal - ClearFailed can restore it to PENDING - and it holds no session
        // lock, so the stale-pending expiry must leave it alone too.
        var failedOld = WriteRecord(root, "Failed", old);
        // A PENDING record that has been SILENT far past its bound. No client is coming back for this, and
        // until it is resolved its session cannot be typed into. It must be abandoned IN PLACE.
        var pendingStale = WriteRecord(root, "Pending", old);
        // Inside the window: the client may still legitimately re-drive this id.
        var deliveredFresh = WriteRecord(root, "Delivered", recent);
        // Unreadable: a half-written marker is never proof of anything, so it is left alone.
        var corruptOld = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corruptOld);
        File.WriteAllText(Path.Combine(corruptOld, "record.json"), "{ not valid json");
        Directory.SetLastWriteTimeUtc(corruptOld, old);
        // The partition container is not an upload; deleting it would take every tenant's records with it.
        var tenantsDir = Path.Combine(root, VoiceUploadStore.TenantPartitionDirectoryName);
        Directory.CreateDirectory(tenantsDir);
        Directory.SetLastWriteTimeUtc(tenantsDir, old);

        GatewayHost.DictationTombstoneSweepScheduleForTests = TimeSpan.FromMilliseconds(150);
        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken, authEnabled: false,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        var swept = await WaitUntilGone(deliveredOld, TimeSpan.FromSeconds(20));

        Assert.True(swept, $"the running Gateway never retired the stale DELIVERED tombstone at {deliveredOld} - " +
            "nothing in production is running the tombstone sweep");
        Assert.False(Directory.Exists(abandonedOld), "a stale ABANDONED tombstone was not retired");

        Assert.True(Directory.Exists(pendingFresh),
            "the sweep touched a PENDING record inside its silence window - that unlocks a session and drops " +
            "audio that was still coming");
        Assert.Contains("Pending", File.ReadAllText(Path.Combine(pendingFresh, "record.json")));
        Assert.True(Directory.Exists(failedOld),
            "the sweep deleted a FAILED record, which ClearFailed can still restore to PENDING");
        Assert.Contains("Failed", File.ReadAllText(Path.Combine(failedOld, "record.json")));

        // The stale one is ABANDONED IN PLACE, not deleted: the session unlocks, but the tombstone stays so a
        // late client cannot re-drive the id, and it names the SERVER as the cause rather than the user.
        var expiredWithin = await WaitUntilStateIs(pendingStale, "Abandoned", TimeSpan.FromSeconds(20));
        Assert.True(expiredWithin,
            $"the running Gateway never released the stale PENDING record at {pendingStale} - nothing in " +
            "production is expiring them, so its session stays locked against human input forever");
        Assert.True(Directory.Exists(pendingStale),
            "the stale PENDING record was DELETED rather than abandoned - the tombstone must survive so a " +
            "late client cannot re-drive the upload id");
        Assert.Contains(VoiceUploadStore.StalePendingReason,
            File.ReadAllText(Path.Combine(pendingStale, "record.json")));
        Assert.True(Directory.Exists(deliveredFresh),
            "the sweep deleted a tombstone inside the retention window, weakening the de-dupe guarantee");
        Assert.True(Directory.Exists(corruptOld),
            "the sweep deleted a record it could not read - an unreadable marker proves nothing");
        Assert.True(Directory.Exists(tenantsDir), "the sweep deleted the per-tenant partition container");
    }

    // ===== helpers =================================================================================

    /// <summary>
    /// Write one delivery record in the exact on-disk shape the Gateway writes, and age the directory - the
    /// signal the sweep judges by - to a chosen point in the past.
    /// </summary>
    private static string WriteRecord(string root, string state, DateTime lastWriteUtc)
    {
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "record.json"),
            $"{{\"State\":\"{state}\",\"Submitted\":false,\"MovedOn\":false,\"Transcript\":\"\"," +
            $"\"Reason\":null,\"SessionId\":\"{Guid.NewGuid()}\"}}");
        Directory.SetLastWriteTimeUtc(dir, lastWriteUtc);
        return dir;
    }

    /// <summary>
    /// Wait for a record to reach a state. Polled rather than asserted once, for the same reason
    /// <see cref="WaitUntilGone"/> is: the sweep runs on the Gateway's own timer, so the test must wait for
    /// production to act rather than call anything itself.
    /// </summary>
    private static async Task<bool> WaitUntilStateIs(string dir, string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var path = Path.Combine(dir, "record.json");
        while (DateTime.UtcNow < deadline)
        {
            try { if (File.ReadAllText(path).Contains($"\"State\":\"{state}\"")) return true; }
            catch { /* mid-write; try again */ }
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<bool> WaitUntilGone(string dir, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!Directory.Exists(dir)) return true;
            await Task.Delay(100);
        }
        return !Directory.Exists(dir);
    }
}
