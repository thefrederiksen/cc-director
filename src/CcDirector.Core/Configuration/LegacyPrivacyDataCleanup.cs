using CcDirector.Core.Storage;

namespace CcDirector.Core.Configuration;

/// <summary>
/// One-way cleanup for retired tracking configuration and event files. It deliberately deletes queued
/// payloads instead of reading or forwarding them, and is safe to run from both Director and Gateway
/// startup.
/// </summary>
public static class LegacyPrivacyDataCleanup
{
    private static readonly string[] RetiredTopLevelKeys = ["telemetry", "telemetry_consent"];

    /// <summary>
    /// Removes retired keys and files, preserving all unrelated configuration and account credentials.
    /// Returns the number of files removed for startup diagnostics.
    /// </summary>
    public static int Run()
    {
        CcDirectorConfigService.RemoveTopLevelKeys(RetiredTopLevelKeys);

        var files = new[]
        {
            Path.Combine(CcStorage.Config(), "director", "telemetry-queue.json"),
            Path.Combine(CcStorage.Config(), "director", "telemetry-consent-cache.json"),
            Path.Combine(CcStorage.Config(), "director", "devthrottle-usage-events.jsonl"),
            Path.Combine(CcStorage.Config(), "director", "devthrottle-auth-events.jsonl"),
            Path.Combine(CcStorage.Config(), "gateway", "devthrottle-auth-events.jsonl"),
            Path.Combine(CcStorage.Root(), "carmode-telemetry.json"),
        };

        var removed = 0;
        foreach (var path in files)
        {
            if (!File.Exists(path))
                continue;
            File.Delete(path);
            removed++;
        }

        // The retired directory may contain full raw and cleaned transcript text. It cannot be safely
        // migrated into the minimized history schema, so remove it without reading its contents.
        var retiredTranscriptionLog = Path.Combine(CcStorage.Root(), "transcription-log");
        if (Directory.Exists(retiredTranscriptionLog))
        {
            Directory.Delete(retiredTranscriptionLog, recursive: true);
            removed++;
        }
        return removed;
    }
}
