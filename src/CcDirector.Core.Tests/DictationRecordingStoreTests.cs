using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The disk safety net for the fire-and-forget desktop dictation Send (issue #1130): the WAV must be
/// written before transcription and deletable once the words are safe, and neither operation may ever
/// throw into the send path - saving is a net, not a gate.
/// </summary>
public sealed class DictationRecordingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* scratch dir; best effort */ }
    }

    [Fact]
    public void TrySave_WritesTheWavAndReturnsItsPath()
    {
        var wav = new byte[] { 1, 2, 3, 4, 5 };

        var path = DictationRecordingStore.TrySave(wav, _dir);

        Assert.NotNull(path);
        Assert.StartsWith(_dir, path);
        Assert.EndsWith(".wav", path);
        Assert.Equal(wav, File.ReadAllBytes(path!));
    }

    [Fact]
    public void TrySave_TwoSavesInTheSameInstant_DoNotCollide()
    {
        var first = DictationRecordingStore.TrySave(new byte[] { 1 }, _dir);
        var second = DictationRecordingStore.TrySave(new byte[] { 2 }, _dir);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TrySave_EmptyAudio_ReturnsNullWithoutWriting()
    {
        var path = DictationRecordingStore.TrySave(Array.Empty<byte>(), _dir);

        Assert.Null(path);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void TrySave_UnusableDirectory_ReturnsNullWithoutThrowing()
    {
        // A FILE at the directory path makes CreateDirectory throw - the net must fail soft.
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        File.WriteAllText(_dir, "not a directory");

        var path = DictationRecordingStore.TrySave(new byte[] { 1 }, _dir);

        Assert.Null(path);
        File.Delete(_dir);
    }

    [Fact]
    public void TryDelete_RemovesTheFile_AndToleratesNullAndMissing()
    {
        var path = DictationRecordingStore.TrySave(new byte[] { 9 }, _dir);
        Assert.True(File.Exists(path));

        DictationRecordingStore.TryDelete(path);
        Assert.False(File.Exists(path));

        // Neither a null path nor an already-deleted file may throw into the send path.
        DictationRecordingStore.TryDelete(null);
        DictationRecordingStore.TryDelete(path);
    }
}
