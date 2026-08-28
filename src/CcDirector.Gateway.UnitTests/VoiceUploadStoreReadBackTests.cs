using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The read-back check in <see cref="VoiceUploadStore"/>.
///
/// WHAT IT DEFENDS AGAINST. Assemble's completeness gate measures every staged chunk and refuses the upload
/// unless all of them are present and non-empty. On 2026-08-27 the hosted Gateway passed that gate with a
/// chunk measured at 871,724 bytes and then READ the same file back as zero bytes - twice, within five
/// seconds, on the Azure Files share the staging lives on. The empty assembly reached the completion path,
/// which responds to empty audio by DELETING the staging, so one bad read discarded audio the user had
/// already uploaded. The phone recovered only because it still held the on-device copy and sent the same
/// bytes a third time; the user saw "Saved - still sending" throughout.
///
/// The values in the first case are the ones actually observed, not invented ones. The fault itself is a
/// storage-layer inconsistency that cannot be staged on a local filesystem, so what is tested here is the
/// DECISION taken when it happens - against a known-bad input, rather than only against a healthy one.
/// </summary>
public class VoiceUploadStoreReadBackTests
{
    [Fact]
    public void ReadBackVerdict_ReadIsShortOfTheMeasurement_ReportsIncompleteAndKeepsStaging()
    {
        // The exact shape of the 2026-08-27 event: one chunk, measured 871,724 bytes, read back as zero.
        var verdict = VoiceUploadStore.ReadBackVerdict("5d609a3dd0414a9da89cb8131bf993cc", index: 0, measured: 871_724, read: 0, totalChunks: 1);

        Assert.NotNull(verdict);
        // Incomplete, NOT ok-with-empty-audio: incomplete is the contract that makes the client re-send and
        // leaves the staged bytes in place. An empty Ok is what deleted the recording.
        Assert.Equal("incomplete", verdict!.Value.Status);
        Assert.Null(verdict.Value.Audio);
        // Every index is named, because a read that disagreed with the measurement tells us nothing about
        // which chunk was misread - so all of them are re-sent.
        Assert.Equal(new[] { 0 }, verdict.Value.Missing);
    }

    [Fact]
    public void ReadBackVerdict_MultiChunkMismatch_NamesEveryIndexToResend()
    {
        var verdict = VoiceUploadStore.ReadBackVerdict("upload", index: 1, measured: 7_019_564, read: 4_000_000, totalChunks: 3);

        Assert.NotNull(verdict);
        Assert.Equal("incomplete", verdict!.Value.Status);
        Assert.Equal(new[] { 0, 1, 2 }, verdict.Value.Missing);
    }

    [Fact]
    public void ReadBackVerdict_ReadMatchesTheMeasurement_AllowsAssemblyToProceed()
    {
        // The healthy path must stay completely out of the way: null means "no objection, carry on".
        Assert.Null(VoiceUploadStore.ReadBackVerdict("upload", index: 0, measured: 871_724, read: 871_724, totalChunks: 1));
    }

    [Fact]
    public void ReadBackVerdict_IsPerChunk_SoTwoFaultsCannotCancelEachOtherOut()
    {
        // The reason this is checked per chunk and not on the assembled total. Chunk 0 measured 100 and read
        // 0; chunk 1 measured 100 and read 200. The SUM agrees exactly (200 == 200), so a total-only check
        // would accept a scrambled recording and send it to the transcriber. Each chunk is judged alone, so
        // both of these are caught.
        Assert.NotNull(VoiceUploadStore.ReadBackVerdict("upload", index: 0, measured: 100, read: 0, totalChunks: 2));
        Assert.NotNull(VoiceUploadStore.ReadBackVerdict("upload", index: 1, measured: 100, read: 200, totalChunks: 2));
    }

    [Fact]
    public void ReadBackVerdict_ReadIsLONGERThanTheMeasurement_IsAlsoRefused()
    {
        // A read longer than the measurement is the same class of fault (a stale directory entry, a chunk
        // rewritten under us) and is equally unsafe to transcribe. The check is equality, not a floor.
        var verdict = VoiceUploadStore.ReadBackVerdict("upload", index: 0, measured: 1_000, read: 2_000, totalChunks: 1);

        Assert.NotNull(verdict);
        Assert.Equal("incomplete", verdict!.Value.Status);
    }
}
