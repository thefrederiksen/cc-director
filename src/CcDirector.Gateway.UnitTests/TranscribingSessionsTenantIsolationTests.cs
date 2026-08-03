using System;
using System.IO;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1884, Gap B: the Gateway-owned transcribing state is TENANT-KEYED, so one account can never set,
/// clear, or read ANOTHER account's transcribing state by supplying that account's session id.
///
/// THE DEFECT THIS CLOSES. <see cref="TranscribingSessions"/> is a single process-wide instance shared by
/// every tenant on the hosted Gateway, and it used to key both of its maps (the idle "Transcribing..." mark
/// and the "actively transcribing" mark) on the BARE session id. A session id is a client-supplied GUID that
/// travels in logs and retries - not a tenant boundary - so once the <c>/dictation</c> family was un-denied on
/// hosted, tenant B could register with tenant A's session id and paint A's session orange for the idle
/// window, or complete/abandon an id carrying A's session id and CLEAR A's real mark on a live dictation.
///
/// THE FIX, proven here two ways:
///   1. <see cref="Set_clear_and_read_are_isolated_per_tenant_for_the_same_session_id"/> and
///      <see cref="The_actively_transcribing_mark_is_isolated_per_tenant"/> drive
///      <see cref="TranscribingSessions"/> directly with two tenants over ONE session id. Revert the keying
///      (make the map key ignore the tenant) and these go RED: B would read A's mark, and B's clear would wipe
///      A's mark.
///   2. <see cref="A_hosted_tenants_pending_dictation_is_read_from_its_OWN_partition"/> and
///      <see cref="Another_tenants_mark_never_colours_this_tenants_roster_row"/> drive the SAME
///      <c>GatewayHost.DictationStatusFor</c> the live roster calls, with the REAL tenant-partitioned
///      <see cref="VoiceUploadStore"/>. They prove the two halves of finding 2 together: the status is read
///      from the CALLER'S partition (a hosted tenant's PENDING dictation is invisible through the Local/base
///      handle), and a mark that belongs to another tenant never colours this tenant's row.
/// </summary>
public sealed class TranscribingSessionsTenantIsolationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "transcribing-iso-" + Guid.NewGuid().ToString("N"));

    // Two distinct MINTED account tenants (canonical lowercase GUIDs - the only shape VoiceUploadStore admits
    // as a partition) plus the base/Local handle they both partition from.
    private readonly TenantId _tenantA = new(Guid.NewGuid().ToString("D"));
    private readonly TenantId _tenantB = new(Guid.NewGuid().ToString("D"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is not a test failure */ }
    }

    // ===== the mark itself, driven directly with two tenants over one session id ====================

    [Fact]
    public void Set_clear_and_read_are_isolated_per_tenant_for_the_same_session_id()
    {
        var marks = new TranscribingSessions();
        var sid = Guid.NewGuid().ToString();

        // A marks its own session transcribing.
        marks.Begin(_tenantA, sid);

        // A reads it (positive control); B - supplying A's very session id - does NOT (B cannot READ A's mark).
        Assert.True(marks.IsTranscribing(_tenantA, sid));
        Assert.False(marks.IsTranscribing(_tenantB, sid));

        // B beginning ITS OWN mark on the same id does not touch A's view, and vice versa - the two are
        // independent (B cannot SET into A's mark).
        marks.Begin(_tenantB, sid);
        Assert.True(marks.IsTranscribing(_tenantA, sid));
        Assert.True(marks.IsTranscribing(_tenantB, sid));

        // B ending the id clears only B's mark; A's is untouched (B cannot CLEAR A's mark).
        marks.End(_tenantB, sid);
        Assert.False(marks.IsTranscribing(_tenantB, sid));
        Assert.True(marks.IsTranscribing(_tenantA, sid));

        // A can still clear its own.
        marks.End(_tenantA, sid);
        Assert.False(marks.IsTranscribing(_tenantA, sid));
    }

    [Fact]
    public void The_actively_transcribing_mark_is_isolated_per_tenant()
    {
        var marks = new TranscribingSessions();
        var sid = Guid.NewGuid().ToString();

        marks.MarkActivelyTranscribing(_tenantA, sid);

        Assert.True(marks.IsActivelyTranscribing(_tenantA, sid));
        Assert.False(marks.IsActivelyTranscribing(_tenantB, sid)); // B cannot read A's active mark

        // B clearing the id clears only B's (nonexistent) mark; A's active mark stands.
        marks.ClearActivelyTranscribing(_tenantB, sid);
        Assert.True(marks.IsActivelyTranscribing(_tenantA, sid)); // B cannot clear A's active mark

        marks.ClearActivelyTranscribing(_tenantA, sid);
        Assert.False(marks.IsActivelyTranscribing(_tenantA, sid));
    }

    // ===== the roster-read seam, with the real tenant-partitioned store =============================

    [Fact]
    public void A_hosted_tenants_pending_dictation_is_read_from_its_OWN_partition()
    {
        // Finding 2: DictationStatusFor must read the CALLER'S partition, not the Local/base handle. A hosted
        // tenant's PENDING dictation lives under base/tenants/<id>, which the base-root projection deliberately
        // does not descend into - so reading through the base handle MISSES it.
        var marks = new TranscribingSessions();
        var basement = new VoiceUploadStore(_root, TenantId.Local);
        var storeA = basement.ForTenant(_tenantA);

        var sid = Guid.NewGuid().ToString();
        var uploadId = Guid.NewGuid().ToString("N");
        storeA.MarkPending(uploadId, sid); // a durable PENDING dictation in A's OWN partition
        marks.Begin(_tenantA, sid);        // and a live progress mark, so the phase is "Uploading"

        // Read through A's own partition (what the fixed wiring passes): the status is visible.
        Assert.Equal(DictationPhase.Uploading,
            GatewayHost.DictationStatusFor(_tenantA, sid, marks, storeA));

        // Read through the Local/base handle (what a revert of finding 2 would pass): A's PENDING is under the
        // tenants container, which the base projection skips, so the status is silently NULL. This is the
        // assertion that reddens if DictationStatusFor is ever put back on the base handle.
        Assert.Null(GatewayHost.DictationStatusFor(_tenantA, sid, marks, basement));
    }

    [Fact]
    public void Another_tenants_mark_never_colours_this_tenants_roster_row()
    {
        // Finding 1 through the production seam. Both tenants hold a PENDING dictation for the SAME session id,
        // but only A has a live progress mark. If the mark keying reverted to the bare session id, B - reading
        // its own partition, which does hold a PENDING record - would pick up A's mark and paint "Uploading".
        var marks = new TranscribingSessions();
        var basement = new VoiceUploadStore(_root, TenantId.Local);
        var storeA = basement.ForTenant(_tenantA);
        var storeB = basement.ForTenant(_tenantB);

        var sid = Guid.NewGuid().ToString();
        storeA.MarkPending(Guid.NewGuid().ToString("N"), sid);
        storeB.MarkPending(Guid.NewGuid().ToString("N"), sid);
        marks.Begin(_tenantA, sid); // ONLY A is progressing

        // A: PENDING + its own live mark -> Uploading.
        Assert.Equal(DictationPhase.Uploading,
            GatewayHost.DictationStatusFor(_tenantA, sid, marks, storeA));

        // B: PENDING but NO mark of its own -> nothing. A's mark is not B's to read.
        Assert.Null(GatewayHost.DictationStatusFor(_tenantB, sid, marks, storeB));
    }
}
