using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1884, the UN-DENY SAFETY GATE. When the /dictation and /wingman/utterance upload families were
/// refused on hosted, the pre-partition shared upload root could hold cross-tenant staged audio, delivery
/// records and transcripts with no owner. Serving those families again (so the owner's mobile dictation
/// works) means that legacy, unattributable data must never be treated as live: not served to a caller, not
/// aged-and-deleted by the sweep, and not surfaced by the base handle's PENDING lock projection.
///
/// The DIRECTORY PARTITION already stops a hosted ACCOUNT tenant from ever reading a legacy dir - an account
/// reads only <c>base/tenants/&lt;id&gt;/</c> and a legacy dir lives directly at <c>base/&lt;uploadId&gt;</c>,
/// a path it cannot address. What these tests pin is the remaining edge: the BASE (Local) handle's own passes
/// over the shared root, which <see cref="VoiceUploadStore.QuarantineLegacyUploads"/> moves the legacy dirs
/// out of - once, at startup on hosted (see <c>GatewayHost.StartAsync</c>).
///
/// MOVE, NEVER DELETE, and idempotent/re-entrant, are asserted directly here because they are the guardrails
/// the change was granted on: a delete would lose recorded speech, and a non-re-entrant move would corrupt on
/// a restart or a second worker.
/// </summary>
public sealed class HostedDictationLegacyQuarantineTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-legacy-quarantine-" + Guid.NewGuid().ToString("N"));

    private VoiceUploadStore BaseStore() => new VoiceUploadStore(_root, TenantId.Local);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* cleanup */ }
    }

    [Fact]
    public void A_legacy_upload_dir_is_moved_aside_and_never_deleted()
    {
        // A pre-partition upload sitting directly under the shared root, with a delivered record (its
        // transcript) and a staged chunk on disk - exactly the shape the un-deny must not serve.
        var baseStore = BaseStore();
        var legacyId = Guid.NewGuid().ToString();
        baseStore.MarkDelivered(legacyId, submitted: true, movedOn: false, transcript: "legacy-secret-transcript");
        var legacyDir = Path.Combine(_root, Guid.Parse(legacyId).ToString("N"));
        Assert.True(File.Exists(Path.Combine(legacyDir, "record.json")), "precondition: the legacy record is at the base root");

        var moved = baseStore.QuarantineLegacyUploads();

        Assert.Equal(1, moved);
        // Moved OUT of the served/enumerated root...
        Assert.False(Directory.Exists(legacyDir), "the legacy dir must no longer sit at the base root");
        // ...and INTO quarantine with its bytes intact - a MOVE, not a delete. No recorded speech is lost.
        var quarantined = Path.Combine(_root, VoiceUploadStore.QuarantineDirectoryName, Guid.Parse(legacyId).ToString("N"));
        Assert.True(Directory.Exists(quarantined), "the legacy dir must be preserved under quarantine");
        Assert.True(File.Exists(Path.Combine(quarantined, "record.json")), "the legacy record (its transcript) is preserved, not deleted");

        // The base handle no longer reads it as a live record.
        Assert.Null(baseStore.ReadRecord(legacyId));
    }

    [Fact]
    public void A_hosted_account_partition_never_reads_a_legacy_base_root_upload()
    {
        // The account partition is base/tenants/<id>/, so a legacy dir at base/<id> is not even addressable
        // from it - the isolation holds by construction, before quarantine has run at all. (Quarantine exists
        // for the BASE handle's own passes, not for the account read path, which was never exposed.)
        var baseStore = BaseStore();
        var legacyId = Guid.NewGuid().ToString();
        baseStore.MarkDelivered(legacyId, submitted: true, movedOn: false, transcript: "legacy-secret-transcript");

        var account = new TenantId(Guid.NewGuid().ToString());
        var accountStore = baseStore.ForTenant(account);

        Assert.Null(accountStore.ReadRecord(legacyId));
        // And after quarantine it is still absent from the account partition (nothing changed for it).
        baseStore.QuarantineLegacyUploads();
        Assert.Null(accountStore.ReadRecord(legacyId));
    }

    [Fact]
    public async Task Quarantine_removes_a_legacy_pending_from_the_base_handle_lock_projection()
    {
        // The concrete state the quarantine cleans: a legacy PENDING record's session is seen as locked by the
        // BASE (Local) handle's projection over the shared root. On a hosted Gateway that projection would
        // otherwise surface a session that belongs to no live tenant.
        var baseStore = BaseStore();
        var legacyId = Guid.NewGuid().ToString();
        var legacySession = Guid.NewGuid().ToString();
        baseStore.MarkPending(legacyId, legacySession);
        await baseStore.StoreChunkAsync(legacyId, 0, System.Text.Encoding.UTF8.GetBytes("legacy-audio"), null);

        // REVERT-PROOF: this is true only because the legacy record is still at the base root. Remove the
        // QuarantineLegacyUploads call from GatewayHost.StartAsync and this state is what the base handle keeps
        // seeing on hosted.
        Assert.True(baseStore.IsSessionLocked(legacySession), "precondition: the base handle sees the legacy pending");

        var moved = baseStore.QuarantineLegacyUploads();

        Assert.Equal(1, moved);
        Assert.False(baseStore.IsSessionLocked(legacySession),
            "after quarantine the base handle's pending projection must no longer see the legacy record");
        // The bytes are preserved (moved, not deleted) - the audio and its marker are still on disk.
        var quarantined = Path.Combine(_root, VoiceUploadStore.QuarantineDirectoryName, Guid.Parse(legacyId).ToString("N"));
        Assert.True(File.Exists(Path.Combine(quarantined, "record.json")));
        Assert.True(File.Exists(Path.Combine(quarantined, "00000.part")));
    }

    [Fact]
    public void Quarantine_is_idempotent_and_re_entrant()
    {
        var baseStore = BaseStore();
        var legacyId = Guid.NewGuid().ToString();
        baseStore.MarkDelivered(legacyId, submitted: true, movedOn: false, transcript: "legacy");

        // A real account partition alongside it, so the run has a tenants container to (correctly) leave alone.
        var account = new TenantId(Guid.NewGuid().ToString());
        baseStore.ForTenant(account).MarkDelivered(Guid.NewGuid().ToString(), submitted: true, movedOn: false, transcript: "account-owned");
        var tenantsContainer = Path.Combine(_root, VoiceUploadStore.TenantPartitionDirectoryName);
        Assert.True(Directory.Exists(tenantsContainer), "precondition: an account partition container exists");

        var first = baseStore.QuarantineLegacyUploads();
        Assert.Equal(1, first);

        // Re-entrant: a second run moves nothing, does not throw, and does not disturb the quarantine dir, the
        // tenants container, or the already-quarantined data.
        var second = baseStore.QuarantineLegacyUploads();
        Assert.Equal(0, second);
        Assert.True(Directory.Exists(tenantsContainer), "the tenants partition container is never quarantined");
        var quarantined = Path.Combine(_root, VoiceUploadStore.QuarantineDirectoryName, Guid.Parse(legacyId).ToString("N"));
        Assert.True(File.Exists(Path.Combine(quarantined, "record.json")), "the already-quarantined data is untouched by a second run");
    }

    [Fact]
    public void A_colliding_quarantine_target_still_moves_the_source_aside_without_overwriting()
    {
        // THE COLLISION HOLE (issue #1884, finding 3). A legacy id was quarantined by an earlier run (or a
        // concurrent worker), and then a rolling/older worker recreated the SAME id at the base root. The old
        // code saw quarantine/<id> already present and simply skipped - leaving the freshly-recreated legacy
        // dir LIVE at the base root, where the base age sweep and the base PENDING projection still read it.
        // The fix moves the source aside under a unique name, so NO legacy source is ever left live at base,
        // and never overwrites the already-quarantined data.
        var baseStore = BaseStore();
        var legacyId = Guid.NewGuid().ToString();
        var cid = Guid.Parse(legacyId).ToString("N");

        // An occupant already sitting in the canonical quarantine slot, with its own distinct content.
        var occupant = Path.Combine(_root, VoiceUploadStore.QuarantineDirectoryName, cid);
        Directory.CreateDirectory(occupant);
        File.WriteAllText(Path.Combine(occupant, "occupant.txt"), "already-quarantined-bytes");

        // A NEW legacy dir with the same id, recreated live at the base root, with DIFFERENT content.
        baseStore.MarkDelivered(legacyId, submitted: true, movedOn: false, transcript: "recreated-legacy-transcript");
        var legacyDir = Path.Combine(_root, cid);
        Assert.True(File.Exists(Path.Combine(legacyDir, "record.json")), "precondition: the recreated legacy dir is live at base");

        var moved = baseStore.QuarantineLegacyUploads();

        // The source is moved aside - NO legacy source is left live at the base root.
        Assert.Equal(1, moved);
        Assert.False(Directory.Exists(legacyDir), "the recreated legacy dir must NOT be left live at the base root");
        Assert.True(baseStore.ReadRecord(legacyId) is null, "the base handle must no longer read the recreated legacy record as live");

        // The already-quarantined occupant is untouched (never overwritten): move, not overwrite.
        Assert.Equal("already-quarantined-bytes", File.ReadAllText(Path.Combine(occupant, "occupant.txt")));
        Assert.False(File.Exists(Path.Combine(occupant, "record.json")), "the occupant slot must not have been overwritten with the source");

        // The source's bytes are preserved under a unique, non-canonical dup name (never lost - move, not delete).
        var dup = Path.Combine(_root, VoiceUploadStore.QuarantineDirectoryName, cid + "__dup-1");
        Assert.True(File.Exists(Path.Combine(dup, "record.json")), "the recreated legacy bytes must be preserved under a unique dup slot");
    }

    [Fact]
    public async Task Concurrent_workers_quarantine_every_legacy_upload_without_loss_and_leave_none_live()
    {
        // THE CONCURRENCY CLAIM the method's summary is granted on ("safe under restart and concurrent
        // workers"), driven directly. GatewayHost.StartAsync calls this at startup on hosted, and the fleet
        // runs several Gateway workers, so two can enter QuarantineLegacyUploads over the SAME shared base root
        // at once. This stages the HARD case for that - the collision hole of finding 3, now under contention:
        // every legacy id ALREADY has its canonical quarantine slot occupied (an earlier run quarantined it and
        // then it was recreated live at base). The old "skip if the canonical slot is taken" would leave every
        // recreated source LIVE at base; the fix moves each aside under a unique name. This is the line the test
        // is revert-proof against - restore the skip (FreeQuarantineTarget / the always-move at the call site)
        // and "no legacy left live" reddens.
        //
        // The invariants asserted are PHYSICAL, not the workers' return values: two workers can each observe a
        // successful rename to the same target before either sees the other, so the counts legitimately
        // over-report. What must hold on disk is no recorded speech lost, no legacy source left live, no occupant
        // overwritten, and convergence to a no-op.
        var baseStore = BaseStore();
        var quarantineDir = Path.Combine(_root, VoiceUploadStore.QuarantineDirectoryName);
        Directory.CreateDirectory(quarantineDir);

        const int legacyCount = 24;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal); // canonical id -> its transcript
        foreach (var _ in Enumerable.Range(0, legacyCount))
        {
            var id = Guid.NewGuid().ToString();
            var cid = Guid.Parse(id).ToString("N");
            var transcript = "recreated-legacy-" + cid;

            // The occupant already sitting in the canonical quarantine slot, with its OWN distinct content.
            var occupant = Path.Combine(quarantineDir, cid);
            Directory.CreateDirectory(occupant);
            File.WriteAllText(Path.Combine(occupant, "occupant.txt"), "already-quarantined-" + cid);

            // The recreated legacy dir, live at the base root, with different content and the same id.
            baseStore.MarkDelivered(id, submitted: true, movedOn: false, transcript: transcript);
            expected[cid] = transcript;
        }

        // Two workers over the SAME base root, at once - the shape of two Gateway processes sharing one Azure
        // Files root.
        var workerOne = BaseStore();
        var workerTwo = BaseStore();
        await Task.WhenAll(
            Task.Run(() => workerOne.QuarantineLegacyUploads()),
            Task.Run(() => workerTwo.QuarantineLegacyUploads()));

        // NO LEGACY LEFT LIVE. Not one canonical upload-id directory remains at the base root for any base pass
        // (the age sweep, the PENDING projection) to still read as live. THIS is the revert canary: the old
        // skip-on-collision leaves every recreated source here.
        var survivingAtBase = Directory.EnumerateDirectories(_root)
            .Select(d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Where(name => Guid.TryParseExact(name, "N", out _))
            .ToList();
        Assert.Empty(survivingAtBase);

        foreach (var (canonicalId, transcript) in expected)
        {
            // THE OCCUPANT IS NEVER OVERWRITTEN. Its canonical slot still holds its own bytes and never gained
            // the source's record - a move aside, not an overwrite.
            var occupant = Path.Combine(quarantineDir, canonicalId);
            Assert.Equal("already-quarantined-" + canonicalId, File.ReadAllText(Path.Combine(occupant, "occupant.txt")));
            Assert.False(File.Exists(Path.Combine(occupant, "record.json")),
                $"occupant slot for {canonicalId} must not have been overwritten by the recreated source");

            // NO DATA LOSS and NO DUPLICATION. The recreated source's transcript is preserved under EXACTLY ONE
            // unique __dup-N sibling - moved, never deleted, and the race never made a second physical copy.
            var dupRecords = Directory.EnumerateDirectories(quarantineDir)
                .Where(d =>
                {
                    var name = Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    return name.StartsWith(canonicalId + "__dup-", StringComparison.Ordinal);
                })
                .Select(d => Path.Combine(d, "record.json"))
                .Where(File.Exists)
                .ToList();
            Assert.True(dupRecords.Count == 1,
                $"recreated legacy {canonicalId} must be preserved under exactly one dup slot (found {dupRecords.Count})");
            Assert.Contains(transcript, File.ReadAllText(dupRecords[0]), StringComparison.Ordinal);
        }

        // IDEMPOTENT UNDER CONCURRENCY. A further pass converges to a no-op - nothing is left to move.
        Assert.Equal(0, baseStore.QuarantineLegacyUploads());
    }

    [Fact]
    public void Quarantine_on_an_account_partition_is_a_no_op()
    {
        // Only the base (Local) handle scans the shared root; an account partition is clean by construction, so
        // running it there moves nothing - it must never reach up into the shared root it is nested under.
        var baseStore = BaseStore();
        var legacyId = Guid.NewGuid().ToString();
        baseStore.MarkDelivered(legacyId, submitted: true, movedOn: false, transcript: "legacy");

        var account = new TenantId(Guid.NewGuid().ToString());
        var moved = baseStore.ForTenant(account).QuarantineLegacyUploads();

        Assert.Equal(0, moved);
        // The legacy dir at the base root is untouched by the account-partition call.
        Assert.True(Directory.Exists(Path.Combine(_root, Guid.Parse(legacyId).ToString("N"))));
    }
}
