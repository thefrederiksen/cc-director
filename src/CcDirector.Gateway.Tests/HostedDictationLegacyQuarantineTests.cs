using System;
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
