using System.Text;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// VOICE V1: the voice STATE is partitioned per tenant, so one account's narration - its spoken text, the
/// reply it was made from, and the audio clip - is never reachable by another.
///
/// Every "cannot see it" assertion here is paired with a SAME-TENANT control on the same call, because an
/// empty answer proves nothing on its own: a seed that silently failed also returns empty, and that reads as
/// isolation. The control is what separates "isolated" from "nothing was ever stored".
///
/// The cross-tenant question is deliberately asked BEFORE its control in every test. That ordering is not
/// cosmetic - see the laundering note in the recipe below.
///
/// REVERT-PROOF RECIPE.
///
/// WHY THIS RECIPE LOOKS THE WAY IT DOES. An earlier version reverted four things: the two minted-shape
/// refusals and the two migration bodies. Eight tests went red and the isolation tests all stayed green,
/// and that green was written up as "the control". It was not a control - it was the finding. Those four
/// reverts are the PERIMETER (shape validation and one-time migration); none of them touches the
/// partitioning itself, so every isolation test stayed green because nothing they test had been mutated.
/// A revert-proof whose core assertions never move has shown you where the guard ISN'T. The rule this cost
/// us: A CONTROL HAS TO BE SHOWN TO BE SENSITIVE TO SOMETHING, OR IT IS JUST A TEST THAT ALWAYS PASSES.
///
/// So the recipe below mutates the BOUNDARY - the four places that actually make one tenant's state
/// unreachable from another - and the perimeter reverts are kept as a secondary group.
///
/// MECHANICS. Do NOT neutralise anything with <c>if (false)</c>: unreachable code is a build error here, so
/// the build fails and the run then executes a STALE binary and reports a false pass. Make the mutation
/// real, and CONFIRM the build line says succeeded before believing any result. Run the WHOLE Gateway
/// assembly - never a filter, which cannot see whether some other test already covered the behaviour or
/// whether the mutation broke something unrelated. Every Gateway run needs the serialized lane; a run that
/// ends with no summary line is contention, not a result.
///
/// NO EXCEPTION-ONLY LAUNDERING. A mutation that makes a test die on a NullReferenceException or an
/// input/output error BEFORE its distinguishing assertion has proved that the harness crashed, not that
/// isolation holds. Every test in this class is therefore written so the cross-tenant question is asked
/// FIRST, while nothing can have thrown, and every dereference is guarded by an explicit null assertion.
/// If a mutation below produces a stack trace instead of a failed assertion, that result does not count -
/// fix the test's ordering and re-run.
///
/// GROUP ONE - THE BOUNDARY. Each mutation keeps the tenant validation call (so the shape tests stay green
/// and attribution stays clean) and only collapses the partition:
///
///  B1. <c>WingmanVoiceService.StateFor</c> - return one shared bucket for every tenant.
///      EXPECTED RED: the six in-memory A/B tests (ready audio, voice-session marking, generating and
///      unavailable and nothing-to-narrate, served-via-fallback, cross-tenant clearing, regeneration
///      decision) plus Clips_reload_into_the_tenant_that_owned_them.
///  B2. <c>WingmanVoiceService.PartitionDirectoryFor</c> - return one shared directory for every tenant.
///      EXPECTED RED: Each_tenants_clips_live_in_its_own_directory (on the production-derived NotEqual and
///      the per-file byte assertions), Voice_session_set_reloads_into_the_tenant_that_owned_it, and
///      Self_host_moves_the_pre_partition_voice_state_into_the_local_partition.
///  B3. <c>VoiceTurnArchive.PartitionDirectoryFor</c> / <c>DirFor</c> - collapse to one directory.
///      EXPECTED RED: Turn_archive_is_partitioned_and_a_turn_id_alone_does_not_read_it and
///      Self_host_moves_pre_partition_archived_turns_into_the_local_partition.
///  B4. <c>GatewayTurnJobStore.StateFor</c> - return one shared bucket.
///      EXPECTED RED: Turn_job_store_is_partitioned_and_a_turn_id_alone_does_not_read_it.
///
/// GROUP TWO - THE PERIMETER (the original four; real, but they are not the boundary):
///
///  P1. <c>WingmanVoiceService.CanonicalTenantKey</c> - delete the <c>IsMintedAccountTenant</c> refusal.
///  P2. <c>VoiceTurnArchive.PartitionDirectoryFor</c> - delete its <c>IsMintedAccountTenant</c> refusal.
///  P3. <c>WingmanVoiceService.MigrateLegacyUnpartitionedState</c> - delete the whole body, both directions.
///  P4. <c>VoiceTurnArchive.MigrateLegacyUnpartitionedTurns</c> - delete the whole body, both directions.
///
/// Deleting both branches of a migration still distinguishes its two directions, and that is by design: the
/// hosted test leads with "the clip is GONE", the self-host test leads with "the clip ARRIVED in the local
/// partition", so with no migration at all they fail on opposite claims about different files rather than
/// on one shared assertion. If a future edit makes those two fail for the same reason, the pair has stopped
/// being a two-direction proof - split the run rather than accept it.
///
/// RESULTS: recorded when the lane is granted. Nothing is recorded here from a filtered run.
/// </summary>
public sealed class WingmanVoiceTenantPartitionTests
{
    private static readonly TenantId TenantA = new("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
    private static readonly TenantId TenantB = new("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb");

    private static byte[] Mp3(string marker) => Encoding.ASCII.GetBytes("ID3" + marker);

    private static string NewBaseDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-voice-partition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static WingmanVoiceService ServiceAt(string baseDir)
    {
        Func<Core.Configuration.WingmanModelRole, CancellationToken, Task<IAgentBrain>> brain =
            (_, _) => Task.FromResult<IAgentBrain>(null!);
        var vault = new KeyVault(Path.Combine(baseDir, "vault.json"));
        return new WingmanVoiceService(brain, vault, Path.Combine(baseDir, "voice-sessions.json"));
    }

    // ===== cross-tenant isolation, each with its same-tenant control =========================

    [Fact]
    public void Ready_audio_stored_for_one_tenant_is_invisible_to_another()
    {
        var svc = ServiceAt(NewBaseDir());
        svc.StoreReadyAudioForTest(TenantA, "s1", "spoken A", "reply A", Mp3("A"));

        // The DISTINGUISHING assertions come FIRST: the other tenant names the SAME session id and gets
        // nothing. Ordering matters here for the same reason it matters in the migration pair - if the
        // control ran first and a mutation made it null, the test would die on a null dereference before it
        // ever asked the question it exists to ask, and a crash would be laundered as a pass-turned-red for
        // the wrong reason. Ask the real question while nothing can have thrown yet.
        Assert.False(svc.HasVoice(TenantB, "s1"));
        Assert.Null(svc.GetAudio(TenantB, "s1"));
        Assert.Null(svc.Get(TenantB, "s1"));
        Assert.Empty(svc.ReadySessionIds(TenantB));

        // CONTROL, second: the same calls on the OWNING tenant do see it, so an empty answer above is
        // isolation and not a failed seed. Every dereference is guarded by an explicit null assertion, so a
        // broken partition reports a failed assertion rather than a NullReferenceException.
        Assert.True(svc.HasVoice(TenantA, "s1"));
        Assert.Equal(Mp3("A"), svc.GetAudio(TenantA, "s1"));
        var ownRead = svc.Get(TenantA, "s1");
        Assert.NotNull(ownRead);
        Assert.Equal("spoken A", ownRead!.Spoken);
        Assert.Contains("s1", svc.ReadySessionIds(TenantA));
    }

    [Fact]
    public void Voice_session_marking_is_per_tenant()
    {
        var svc = ServiceAt(NewBaseDir());
        svc.Mark(TenantA, "s1");

        Assert.True(svc.IsVoiceSession(TenantA, "s1"));            // control
        Assert.Contains("s1", svc.VoiceSessionIds(TenantA));       // control
        Assert.False(svc.IsVoiceSession(TenantB, "s1"));
        Assert.Empty(svc.VoiceSessionIds(TenantB));
    }

    [Fact]
    public void Generating_unavailable_and_nothing_to_narrate_are_per_tenant()
    {
        var svc = ServiceAt(NewBaseDir());
        svc.BeginGenerating(TenantA, "s1");
        svc.NoteRetrying(TenantA, "s1");
        svc.SetNothingToNarrate(TenantA, "s1", true);

        Assert.True(svc.IsGenerating(TenantA, "s1"));              // control
        Assert.NotNull(svc.VoiceUnavailableFor(TenantA, "s1"));    // control
        Assert.True(svc.NothingToNarrateFor(TenantA, "s1"));       // control

        Assert.False(svc.IsGenerating(TenantB, "s1"));
        Assert.Null(svc.VoiceUnavailableFor(TenantB, "s1"));
        Assert.False(svc.NothingToNarrateFor(TenantB, "s1"));
    }

    [Fact]
    public void Served_via_fallback_is_per_tenant()
    {
        var svc = ServiceAt(NewBaseDir());
        svc.StoreReadyAudioForTest(TenantA, "s1", "spoken", "reply", Mp3("A"), servedViaFallback: true);

        Assert.True(svc.ServedViaFallbackFor(TenantA, "s1"));       // control
        Assert.False(svc.ServedViaFallbackFor(TenantB, "s1"));
    }

    [Fact]
    public void Clearing_one_tenants_session_leaves_the_other_tenants_identically_named_session_intact()
    {
        var svc = ServiceAt(NewBaseDir());
        svc.StoreReadyAudioForTest(TenantA, "s1", "spoken A", "reply A", Mp3("A"));
        svc.StoreReadyAudioForTest(TenantB, "s1", "spoken B", "reply B", Mp3("B"));

        svc.OnSessionWorking(TenantA, "s1");
        Assert.False(svc.HasVoice(TenantA, "s1"));
        Assert.True(svc.HasVoice(TenantB, "s1"));                   // control: untouched
        Assert.Equal(Mp3("B"), svc.GetAudio(TenantB, "s1"));

        svc.Mark(TenantA, "s1");
        svc.Unmark(TenantA, "s1");
        Assert.True(svc.HasVoice(TenantB, "s1"));                   // control: still untouched
    }

    [Fact]
    public void Regeneration_decision_reads_only_the_asking_tenants_cached_reply()
    {
        var svc = ServiceAt(NewBaseDir());
        svc.StoreReadyAudioForTest(TenantA, "s1", "spoken A", "the same reply", Mp3("A"));

        // CONTROL: the owning tenant sees its own cached reply and stays quiet.
        Assert.False(svc.ShouldRegenerate(TenantA, "s1", "the same reply"));
        // The other tenant has nothing cached for that id, so it must regenerate - it cannot read A's clip.
        Assert.True(svc.ShouldRegenerate(TenantB, "s1", "the same reply"));
    }

    // ===== the partition is PHYSICAL and survives a restart =================================

    /// <summary>
    /// PRIMARY CANARY for <c>WingmanVoiceService.PartitionDirectoryFor</c>. Two tenants store a clip under
    /// the SAME session id; each must land in its own directory on disk.
    ///
    /// The directories are read back from the production method, NOT recomputed by the test. An earlier
    /// version of this test asserted <c>Assert.NotEqual(aDir, bDir)</c> over two paths the TEST had built
    /// from the two tenant ids - which is true by arithmetic no matter what the production code does, and
    /// would have stayed green with the partition deleted entirely. An assertion that cannot fail is not
    /// coverage.
    /// </summary>
    [Fact]
    public void Each_tenants_clips_live_in_its_own_directory()
    {
        var baseDir = NewBaseDir();
        var svc = ServiceAt(baseDir);
        svc.StoreReadyAudioForTest(TenantA, "s1", "spoken A", "reply A", Mp3("A"));
        svc.StoreReadyAudioForTest(TenantB, "s1", "spoken B", "reply B", Mp3("B"));

        // Production-derived, so this moves when the partitioning moves.
        var aDir = Path.Combine(svc.PartitionDirectoryFor(TenantA), "voice-audio");
        var bDir = Path.Combine(svc.PartitionDirectoryFor(TenantB), "voice-audio");
        Assert.NotEqual(aDir, bDir);

        // Each tenant's clip is a SEPARATE FILE, and each holds its own bytes - so one did not overwrite
        // the other at a shared path.
        Assert.True(File.Exists(Path.Combine(aDir, "s1.mp3")));
        Assert.True(File.Exists(Path.Combine(bDir, "s1.mp3")));
        Assert.Equal(Mp3("A"), File.ReadAllBytes(Path.Combine(aDir, "s1.mp3")));
        Assert.Equal(Mp3("B"), File.ReadAllBytes(Path.Combine(bDir, "s1.mp3")));
    }

    /// <summary>
    /// PRIMARY CANARY for the restart path: a clip must reload into the tenant that owned it, not into a
    /// shared bucket where the second tenant's load overwrites the first. Sensitive to BOTH
    /// <c>StateFor</c> and <c>PartitionDirectoryFor</c>, which is stated rather than hidden.
    /// </summary>
    [Fact]
    public void Clips_reload_into_the_tenant_that_owned_them()
    {
        var baseDir = NewBaseDir();
        var svc = ServiceAt(baseDir);
        svc.StoreReadyAudioForTest(TenantA, "s1", "spoken A", "reply A", Mp3("A"));
        svc.StoreReadyAudioForTest(TenantB, "s1", "spoken B", "reply B", Mp3("B"));

        var reloaded = ServiceAt(baseDir);
        // Byte comparisons first: a null or a wrong-tenant clip both fail as a plain assertion, never as a
        // dereference of null.
        Assert.Equal(Mp3("A"), reloaded.GetAudio(TenantA, "s1"));
        Assert.Equal(Mp3("B"), reloaded.GetAudio(TenantB, "s1"));

        var a = reloaded.Get(TenantA, "s1");
        var b = reloaded.Get(TenantB, "s1");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal("spoken A", a!.Spoken);
        Assert.Equal("spoken B", b!.Spoken);
    }

    [Fact]
    public void Voice_session_set_reloads_into_the_tenant_that_owned_it()
    {
        var baseDir = NewBaseDir();
        var svc = ServiceAt(baseDir);
        svc.Mark(TenantA, "s1");

        var reloaded = ServiceAt(baseDir);
        Assert.True(reloaded.IsVoiceSession(TenantA, "s1"));        // control
        Assert.False(reloaded.IsVoiceSession(TenantB, "s1"));
    }

    // ===== a non-minted tenant shape is REFUSED, never coerced ==============================

    [Fact]
    public void Traversal_tenant_is_refused_rather_than_resolved_to_the_parent_partition()
    {
        var svc = ServiceAt(NewBaseDir());
        Assert.Throws<ArgumentException>(() => svc.PartitionDirectoryFor(new TenantId("..")));
        Assert.Throws<ArgumentException>(() => svc.HasVoice(new TenantId(".."), "s1"));
        Assert.Throws<ArgumentException>(() => svc.PartitionDirectoryFor(new TenantId("../local")));
    }

    [Fact]
    public void Case_variant_of_a_minted_tenant_is_refused_rather_than_aliased_to_the_same_partition()
    {
        var svc = ServiceAt(NewBaseDir());
        var upper = new TenantId(TenantA.Value.ToUpperInvariant());
        Assert.Throws<ArgumentException>(() => svc.PartitionDirectoryFor(upper));
        Assert.Throws<ArgumentException>(() => svc.HasVoice(upper, "s1"));
    }

    [Fact]
    public void System_tenant_is_refused_a_voice_partition()
    {
        var svc = ServiceAt(NewBaseDir());
        Assert.Throws<ArgumentException>(() => svc.PartitionDirectoryFor(TenantId.System));
        Assert.Throws<ArgumentException>(() => svc.Mark(TenantId.System, "s1"));
    }

    [Fact]
    public void An_unresolved_tenant_is_denied_never_defaulted()
    {
        var svc = ServiceAt(NewBaseDir());
        Assert.Throws<ArgumentException>(() => svc.HasVoice(default, "s1"));
    }

    // ===== the legacy migration, proved in BOTH deployment modes ============================

    private static void SeedLegacyState(string baseDir)
    {
        File.WriteAllText(Path.Combine(baseDir, "voice-sessions.json"), "[\"s1\"]");
        var audioDir = Path.Combine(baseDir, "voice-audio");
        Directory.CreateDirectory(audioDir);
        File.WriteAllBytes(Path.Combine(audioDir, "s1.mp3"), Mp3("legacy"));
        File.WriteAllText(Path.Combine(audioDir, "s1.json"),
            "{\"Spoken\":\"legacy spoken\",\"Reply\":\"legacy reply\",\"AtUtc\":\"2026-01-01T00:00:00Z\"}");
    }

    [Fact]
    public void Hosted_deletes_the_pre_partition_voice_state_rather_than_guessing_an_owner()
    {
        var baseDir = NewBaseDir();
        SeedLegacyState(baseDir);

        var prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        try
        {
            var svc = ServiceAt(baseDir);
            // The clip is GONE from disk - not merely unreachable, actually deleted.
            Assert.False(File.Exists(Path.Combine(baseDir, "voice-sessions.json")));
            Assert.False(Directory.Exists(Path.Combine(baseDir, "voice-audio")));
            // And it was not quietly re-homed into some tenant on the way out.
            Assert.False(svc.HasVoice(TenantId.Local, "s1"));
            Assert.False(svc.IsVoiceSession(TenantId.Local, "s1"));
            Assert.False(svc.HasVoice(TenantA, "s1"));
        }
        finally { Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", prior); }
    }

    [Fact]
    public void Self_host_moves_the_pre_partition_voice_state_into_the_local_partition()
    {
        var baseDir = NewBaseDir();
        SeedLegacyState(baseDir);

        var prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);
        try
        {
            var svc = ServiceAt(baseDir);
            // ARRIVED is asserted FIRST, deliberately, and this ordering is load-bearing. Its twin above
            // asserts the opposite outcome (the clip is GONE). If both tests led with "gone from the old
            // location", then deleting the whole migration would fail BOTH on the same assertion for the
            // same reason, and a revert run could no longer tell the two directions apart - it would prove
            // only "something broke". Leading with the direction-specific claim keeps the pair honest:
            // with no migration at all, the hosted twin fails on "still there" and this one fails on
            // "never arrived", which are different assertions about different files.
            Assert.True(File.Exists(Path.Combine(baseDir, "tenants", "local", "voice-audio", "s1.mp3")));
            Assert.True(svc.IsVoiceSession(TenantId.Local, "s1"));
            Assert.True(svc.HasVoice(TenantId.Local, "s1"));
            Assert.Equal(Mp3("legacy"), svc.GetAudio(TenantId.Local, "s1"));
            // ...and only then that it was MOVED rather than copied.
            Assert.False(File.Exists(Path.Combine(baseDir, "voice-sessions.json")));
            Assert.False(Directory.Exists(Path.Combine(baseDir, "voice-audio")));
            // It landed in LOCAL only - it was not fanned out to an account tenant.
            Assert.False(svc.HasVoice(TenantA, "s1"));
        }
        finally { Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", prior); }
    }

    // ===== the voice-turn stores in the same state layer ====================================

    [Fact]
    public void Turn_archive_is_partitioned_and_a_turn_id_alone_does_not_read_it()
    {
        var root = NewBaseDir();
        var archive = new VoiceTurnArchive(root);
        var turnId = Guid.NewGuid().ToString();
        archive.Save(TenantA, new VoiceTurnArchiveRecord
        {
            TurnId = turnId, SessionId = "s1", UploadId = "u1",
            Transcript = "transcript A", Summary = "summary A", HasAudio = true, CreatedAtUtc = DateTime.UtcNow,
        }, Mp3("A"));

        // DISTINGUISHING first: the other tenant holds the exact turn id and still gets nothing on every
        // path. Asked before anything can have thrown, so a broken partition reports these assertions
        // rather than dying on a null dereference in the control below.
        Assert.Null(archive.Get(TenantB, turnId));
        Assert.Null(archive.GetAudio(TenantB, turnId));
        Assert.Empty(archive.ListForSession(TenantB, "s1"));
        Assert.Null(archive.FindByUpload(TenantB, "u1"));

        // CONTROL, second: the owning tenant reads it on every path, so the emptiness above is isolation
        // and not a failed save. The dereference is guarded.
        var own = archive.Get(TenantA, turnId);
        Assert.NotNull(own);
        Assert.Equal("summary A", own!.Summary);
        Assert.Equal(Mp3("A"), archive.GetAudio(TenantA, turnId));
        Assert.Single(archive.ListForSession(TenantA, "s1"));
        Assert.NotNull(archive.FindByUpload(TenantA, "u1"));

        // And the partition is PHYSICAL: the two tenants resolve to different directories, read back from
        // the production method rather than recomputed by the test.
        Assert.NotEqual(archive.PartitionDirectoryFor(TenantA), archive.PartitionDirectoryFor(TenantB));
    }

    [Fact]
    public void Turn_archive_refuses_a_traversal_tenant()
    {
        var archive = new VoiceTurnArchive(NewBaseDir());
        Assert.Throws<ArgumentException>(() => archive.PartitionDirectoryFor(new TenantId("..")));
        Assert.Throws<ArgumentException>(() => archive.Get(new TenantId(".."), Guid.NewGuid().ToString()));
        Assert.Throws<ArgumentException>(() => archive.PartitionDirectoryFor(new TenantId(TenantA.Value.ToUpperInvariant())));
    }

    [Fact]
    public void Turn_job_store_is_partitioned_and_a_turn_id_alone_does_not_read_it()
    {
        var store = new GatewayTurnJobStore();
        var job = store.Create(TenantA, "s1", "u1");

        // DISTINGUISHING first, for the same anti-laundering reason as the stores above.
        Assert.Null(store.Get(TenantB, job.TurnId));
        Assert.Null(store.FindTurnByUpload(TenantB, "u1"));

        // CONTROL second: the owning tenant does find it, so the nulls above are isolation, not a job that
        // was never created.
        Assert.NotNull(store.Get(TenantA, job.TurnId));
        Assert.NotNull(store.FindTurnByUpload(TenantA, "u1"));
    }

    [Fact]
    public void Hosted_deletes_pre_partition_archived_turns()
    {
        var root = NewBaseDir();
        var legacyTurn = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacyTurn);
        File.WriteAllText(Path.Combine(legacyTurn, "meta.json"), "{}");

        var prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        try
        {
            _ = new VoiceTurnArchive(root);
            Assert.False(Directory.Exists(legacyTurn));
        }
        finally { Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", prior); }
    }

    [Fact]
    public void Self_host_moves_pre_partition_archived_turns_into_the_local_partition()
    {
        var root = NewBaseDir();
        var turnId = Guid.NewGuid();
        var legacyTurn = Path.Combine(root, turnId.ToString("N"));
        Directory.CreateDirectory(legacyTurn);
        File.WriteAllText(Path.Combine(legacyTurn, "meta.json"),
            "{\"TurnId\":\"" + turnId + "\",\"SessionId\":\"s1\",\"UploadId\":\"u1\",\"Summary\":\"legacy summary\"}");

        var prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);
        try
        {
            var archive = new VoiceTurnArchive(root);
            // ARRIVED first, for the same reason as the voice-state twin above: leading with the
            // direction-specific claim keeps this test distinguishable from its hosted opposite when the
            // whole migration is deleted in a revert run.
            var moved = archive.Get(TenantId.Local, turnId.ToString());
            Assert.NotNull(moved);
            Assert.Equal("legacy summary", moved!.Summary);
            Assert.False(Directory.Exists(legacyTurn));
            Assert.Null(archive.Get(TenantA, turnId.ToString()));
        }
        finally { Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", prior); }
    }
}
