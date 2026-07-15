using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Disk safety net for the fire-and-forget desktop dictation Send (issue #1130). The recorded WAV is
/// saved here the instant Send produces it and deleted again the moment the user's words are safe
/// (delivered into the session, or restored as text after a submit failure) - so a failed or slow
/// transcription can never lose the recording. This is NOT a queue: there is NO sweeper, NO retry,
/// and nothing ever re-drives a saved file (issue #1208 removed the durable queue deliberately). A
/// file only survives when the spoken words could not be recovered any other way, and the failure
/// report names it so the user can play or re-dictate from the audio.
/// </summary>
public static class DictationRecordingStore
{
    /// <summary>Where recordings are kept: base/dictation/recordings. Resolved per access, not baked
    /// into a get-only initializer, so CC_DIRECTOR_ROOT redirects it - an initializer runs once at type
    /// load and no test can undo it.</summary>
    public static string DefaultDirectory => CcStorage.DictationRecordings();

    /// <summary>
    /// Save one recorded WAV clip and return the full path of the saved file, or null when saving
    /// failed. Saving is a safety net, not a gate: a full disk or a locked directory must never block
    /// the dictation itself, so a failure is logged loudly and the send continues without the net.
    /// </summary>
    public static string? TrySave(byte[] wav, string? directory = null)
    {
        if (wav is null || wav.Length == 0)
        {
            FileLog.Write("[DictationRecordingStore] TrySave skipped: no audio bytes");
            return null;
        }
        try
        {
            var dir = directory ?? DefaultDirectory;
            Directory.CreateDirectory(dir);
            var name = $"dictation-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.wav";
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, wav);
            FileLog.Write($"[DictationRecordingStore] saved {wav.Length} bytes to {path}");
            return path;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationRecordingStore] TrySave FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Delete a recording whose words are now safe. Best-effort: a file that cannot be deleted is
    /// only disk noise, never a functional problem, so the failure is logged and swallowed.
    /// </summary>
    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            File.Delete(path);
            FileLog.Write($"[DictationRecordingStore] deleted {path}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DictationRecordingStore] TryDelete FAILED for {path}: {ex.Message}");
        }
    }
}
