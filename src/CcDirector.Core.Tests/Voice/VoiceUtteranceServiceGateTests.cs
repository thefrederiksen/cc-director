using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Voice;
using Xunit;

namespace CcDirector.Core.Tests.Voice;

/// <summary>
/// Tests for the audio completeness gate (issue #586) in
/// <see cref="VoiceUtteranceService"/>. These exercise only the gate paths
/// (empty capture, missing/zero-byte segment) which return BEFORE any
/// transcription, so they run without OpenAI or a live session.
///
/// CC_DIRECTOR_ROOT is pinned to a temp dir so the chunk staging lands there. This class's
/// docstring used to call the service's folder "a per-user temp root" - it was not, it was the
/// REAL %LOCALAPPDATA%\cc-director\voice-utterances of the running Director, baked into a static
/// readonly field no redirect could reach. Each test still uses a fresh GUID utterance id.
/// </summary>
[Collection("CcStorageRoot")] // serializes all classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class VoiceUtteranceServiceGateTests : IDisposable
{
    private readonly VoiceUtteranceService _svc;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly string _dir;
    private readonly string _root;
    private readonly string? _prevRoot;

    public VoiceUtteranceServiceGateTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-utterance-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        var options = new AgentOptions();
        _svc = new VoiceUtteranceService(new SessionManager(options), options);
        // Ask CcStorage rather than re-deriving the path: the old copy hardcoded %LOCALAPPDATA%
        // and so followed the production class into the real Director's folder.
        _dir = Path.Combine(CcStorage.VoiceUtterances(), _id);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Complete_EmptyCapture_FailsLoud()
    {
        // Acceptance criterion 5: an empty capture (zero chunks declared) fails
        // with a named error and never produces an empty transcript.
        _svc.Register(_id);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.CompleteAsync(_id, totalChunks: 0, mime: "audio/webm", repoPath: ""));
        Assert.Contains("empty capture", ex.Message);
    }

    [Fact]
    public async Task Complete_MissingSegment_RefusedAsIncomplete_NamesIndex()
    {
        // Acceptance criterion 1: a complete call missing a declared segment is
        // refused as "incomplete", naming the missing index, with no transcript.
        _svc.Register(_id);
        var c0 = Encoding.UTF8.GetBytes("voice-chunk-0");
        await _svc.StoreChunkAsync(_id, 0, c0, Sha(c0));
        // Declare two chunks but only store index 0; index 1 is missing.

        var resp = await _svc.CompleteAsync(_id, totalChunks: 2, mime: "audio/webm", repoPath: "");

        Assert.Equal("incomplete", resp.Status);
        Assert.NotNull(resp.Error);
        Assert.Contains("1", resp.Error);
    }

    [Fact]
    public async Task StoredChunks_landInTheRedirectedRoot_notTheRealDirectorFolder()
    {
        // Asserted, not assumed. Before this, the service baked %LOCALAPPDATA% into a static
        // readonly field, so these tests staged audio chunks inside the REAL running Director's
        // voice-utterances folder no matter what root the test set.
        _svc.Register(_id);
        var chunk = Encoding.UTF8.GetBytes("voice-chunk-0");
        await _svc.StoreChunkAsync(_id, 0, chunk, Sha(chunk));

        Assert.StartsWith(_root, _dir, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(_dir), $"expected the staging dir at {_dir}");
        Assert.NotEmpty(Directory.GetFiles(_dir));

        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cc-director", "voice-utterances", _id);
        Assert.False(Directory.Exists(real), $"a redirected root must not stage into {real}");
    }
}
