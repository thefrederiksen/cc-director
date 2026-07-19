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
/// REVERT-PROOF RECIPE. Do NOT neutralise a guard with <c>if (false)</c>: unreachable code is a build error
/// in this repository, the build then fails, and a test run after a failed build executes the STALE binary
/// and reports a false pass. DELETE the guard, and CONFIRM the build line says succeeded before believing
/// any result. Run the WHOLE Gateway suite, not a filter on this class - a filter cannot see whether an
/// existing test elsewhere already covered the behaviour, nor whether removing a guard broke something
/// unrelated. The Gateway suite is serialized on the build machine; a run that ends with no summary line is
/// contention, not a result.
///
/// ONE run removes all four guards at once, because each one has its own test with its own assertion, so
/// their reds stay individually attributable:
///
///  1. <c>WingmanVoiceService.CanonicalTenantKey</c> - DELETE the <c>IsMintedAccountTenant</c> refusal.
///  2. <c>VoiceTurnArchive.PartitionDirectoryFor</c> - DELETE its <c>IsMintedAccountTenant</c> refusal.
///  3. <c>WingmanVoiceService.MigrateLegacyUnpartitionedState</c> - DELETE the whole body, BOTH the hosted
///     delete branch and the self-host move.
///  4. <c>VoiceTurnArchive.MigrateLegacyUnpartitionedTurns</c> - DELETE the whole body, both directions.
///
/// Deleting both branches of a migration still distinguishes its two directions, and that is not an
/// accident - it is why the two migration tests assert in the order they do. The hosted test leads with
/// "the clip is GONE", the self-host test leads with "the clip ARRIVED in the local partition". With no
/// migration at all they therefore fail on OPPOSITE claims about different files, not on one shared
/// assertion. Had both led with "gone from the old location" they would have failed identically and the
/// run would have proved only "something broke". If a future edit makes these two fail for the same
/// reason, the pair has stopped being a two-direction proof - split the run rather than accept it.
///
/// OBSERVED with all four deleted (build succeeded; 8 RED, and each red names its own guard):
///   Assert.Throws, no exception thrown - the tenant-shape guards:
///     Case_variant_of_a_minted_tenant_is_refused_rather_than_aliased_to_the_same_partition   (guard 1)
///     System_tenant_is_refused_a_voice_partition                                             (guard 1)
///     Traversal_tenant_is_refused_rather_than_resolved_to_the_parent_partition               (guard 1)
///     Turn_archive_refuses_a_traversal_tenant                                                (guard 2)
///   Assert.False failure, "it is still there" - the HOSTED delete direction:
///     Hosted_deletes_the_pre_partition_voice_state_rather_than_guessing_an_owner             (guard 3)
///     Hosted_deletes_pre_partition_archived_turns                                            (guard 4)
///   Assert.True / Assert.NotNull failure, "it never arrived" - the SELF-HOST move direction:
///     Self_host_moves_the_pre_partition_voice_state_into_the_local_partition                 (guard 3)
///     Self_host_moves_pre_partition_archived_turns_into_the_local_partition                  (guard 4)
///
/// The eleven isolation tests stayed GREEN throughout, which is the control: they use a valid tenant and
/// seed their own state, so no deleted guard is on their path. Restoring all four returns the suite to
/// green.
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

        // CONTROL: the same call on the owning tenant DOES see it, so an empty answer below is isolation
        // and not a failed seed.
        Assert.True(svc.HasVoice(TenantA, "s1"));
        Assert.Equal(Mp3("A"), svc.GetAudio(TenantA, "s1"));
        Assert.Equal("spoken A", svc.Get(TenantA, "s1")!.Spoken);
        Assert.Contains("s1", svc.ReadySessionIds(TenantA));

        // The other tenant names the SAME session id and gets nothing.
        Assert.False(svc.HasVoice(TenantB, "s1"));
        Assert.Null(svc.GetAudio(TenantB, "s1"));
        Assert.Null(svc.Get(TenantB, "s1"));
        Assert.Empty(svc.ReadySessionIds(TenantB));
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

    [Fact]
    public void Each_tenants_clips_live_in_its_own_directory_and_reload_into_its_own_partition()
    {
        var baseDir = NewBaseDir();
        var svc = ServiceAt(baseDir);
        svc.StoreReadyAudioForTest(TenantA, "s1", "spoken A", "reply A", Mp3("A"));
        svc.StoreReadyAudioForTest(TenantB, "s1", "spoken B", "reply B", Mp3("B"));

        var aDir = Path.Combine(baseDir, "tenants", TenantA.Value, "voice-audio");
        var bDir = Path.Combine(baseDir, "tenants", TenantB.Value, "voice-audio");
        Assert.True(File.Exists(Path.Combine(aDir, "s1.mp3")));
        Assert.True(File.Exists(Path.Combine(bDir, "s1.mp3")));
        // The tenant id is a PATH COMPONENT: the two identically-named sessions are different files.
        Assert.NotEqual(aDir, bDir);

        var reloaded = ServiceAt(baseDir);
        Assert.Equal(Mp3("A"), reloaded.GetAudio(TenantA, "s1"));   // control
        Assert.Equal(Mp3("B"), reloaded.GetAudio(TenantB, "s1"));   // control
        Assert.Equal("spoken A", reloaded.Get(TenantA, "s1")!.Spoken);
        Assert.Equal("spoken B", reloaded.Get(TenantB, "s1")!.Spoken);
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

        // CONTROL: the owning tenant reads it on every path.
        Assert.Equal("summary A", archive.Get(TenantA, turnId)!.Summary);
        Assert.Equal(Mp3("A"), archive.GetAudio(TenantA, turnId));
        Assert.Single(archive.ListForSession(TenantA, "s1"));
        Assert.NotNull(archive.FindByUpload(TenantA, "u1"));

        // The other tenant holds the exact turn id and still gets nothing.
        Assert.Null(archive.Get(TenantB, turnId));
        Assert.Null(archive.GetAudio(TenantB, turnId));
        Assert.Empty(archive.ListForSession(TenantB, "s1"));
        Assert.Null(archive.FindByUpload(TenantB, "u1"));
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

        Assert.NotNull(store.Get(TenantA, job.TurnId));                 // control
        Assert.NotNull(store.FindTurnByUpload(TenantA, "u1"));          // control

        Assert.Null(store.Get(TenantB, job.TurnId));
        Assert.Null(store.FindTurnByUpload(TenantB, "u1"));
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
