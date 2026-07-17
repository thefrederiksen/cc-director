using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Data;

/// <summary>
/// Shared helper for the one-time JSON-to-SQLite migration each structured store runs on first upgrade.
/// After a store has imported a legacy JSON file into the EF database, it renames the file aside so the
/// import never runs again and the original data stays on disk as a backup.
/// </summary>
public static class LegacyJsonImport
{
    /// <summary>
    /// Rename an imported legacy file to <c>&lt;path&gt;.migrated-&lt;UTCstamp&gt;</c>. Called only AFTER a
    /// successful, committed import, so the aside copy is a verified backup. Fail-loud: if the rename throws
    /// the exception propagates - a store whose data is in the database but whose legacy file was NOT moved
    /// aside would re-import on the next boot, so leaving it in place silently is not acceptable.
    /// </summary>
    public static void RenameAside(string path, string logPrefix)
    {
        var aside = $"{path}.migrated-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(path, aside);
        FileLog.Write($"{logPrefix} Import: renamed the imported legacy file {path} aside to {aside} (kept as a backup)");
    }
}
