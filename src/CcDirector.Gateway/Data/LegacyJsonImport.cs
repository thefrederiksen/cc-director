using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Data;

/// <summary>
/// Shared helper for the one-time JSON-to-SQLite migration each structured store runs on first upgrade.
/// After a store has imported a legacy JSON file into the EF database, it renames the file aside so the
/// import never runs again and the original data stays on disk as a backup.
///
/// <see cref="Recoverable"/> owns the guard/rename/recovery plumbing that every migrated store shares, so
/// there is ONE implementation of the recoverable, idempotent, best-effort rename-aside flow rather than an
/// inline copy per store. Each store still owns its own populated-check, parse, and insert; only the
/// surrounding plumbing lives here.
/// </summary>
public static class LegacyJsonImport
{
    /// <summary>
    /// Run a store's one-time legacy-JSON import with the shared recoverable, idempotent, best-effort
    /// rename-aside plumbing:
    /// <list type="bullet">
    /// <item>No legacy file on disk - nothing to do.</item>
    /// <item>The file exists but the table is ALREADY populated (a prior import committed but its rename-aside
    /// did not complete - the file was briefly locked) - rename it aside now, NEVER re-importing over existing
    /// rows, so the leftover self-heals. Best-effort (see <see cref="TryRenameAside"/>): a rename that fails
    /// again leaves the data safe and lets the next boot retry.</item>
    /// <item>First upgrade (file exists, table empty) - the store's <paramref name="importCommitted"/> parses
    /// and inserts its own rows inside a transaction and commits. A parse error THROWS from there and the file
    /// is left in place (fail-loud, all-or-nothing); this helper does not catch it. On success the imported
    /// file is renamed aside, best-effort - the data is committed and the empty-table guard blocks any
    /// re-import, so a briefly-locked file is logged and retried next boot rather than failing the Gateway.</item>
    /// </list>
    /// The <paramref name="isPopulated"/> check and <paramref name="importCommitted"/> import are the store's
    /// own logic (they open their own scoped context); this helper only sequences them and owns the renames.
    /// </summary>
    public static void Recoverable(string path, string logPrefix, Func<bool> isPopulated, Action importCommitted)
    {
        if (!File.Exists(path))
            return;

        if (isPopulated())
        {
            // Idempotent recovery: the data is already in the database, so a lingering legacy file is a
            // rename that failed after the commit. Rename it aside without touching the existing rows.
            TryRenameAside(path, logPrefix);
            return;
        }

        // First upgrade: the store parses and inserts its rows (fail-loud on a parse error propagates and
        // leaves the file in place), then the imported file is renamed aside best-effort.
        importCommitted();
        TryRenameAside(path, logPrefix);
    }

    /// <summary>
    /// Rename an imported legacy file to <c>&lt;path&gt;.migrated-&lt;UTCstamp&gt;</c>. Called only AFTER a
    /// successful, committed import, so the aside copy is a verified backup. Throws if the rename fails - used
    /// where the caller wants the failure surfaced. The recoverable flow uses <see cref="TryRenameAside"/>
    /// instead, which swallows the failure because the empty-table guard prevents a re-import.
    /// </summary>
    public static void RenameAside(string path, string logPrefix)
    {
        var aside = $"{path}.migrated-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(path, aside);
        FileLog.Write($"{logPrefix} Import: renamed the imported legacy file {path} aside to {aside} (kept as a backup)");
    }

    /// <summary>
    /// Rename the imported legacy file aside, best-effort. The data is already committed to the database and
    /// the empty-table guard prevents any re-import, so a failed rename (for example a briefly-locked file)
    /// must NOT fail the Gateway - it is logged and left for the next boot's recovery to retry.
    /// </summary>
    public static void TryRenameAside(string path, string logPrefix)
    {
        try
        {
            RenameAside(path, logPrefix);
        }
        catch (Exception ex)
        {
            FileLog.Write($"{logPrefix} Import: rename-aside of {path} failed (data is safe in the database; " +
                          $"the next boot retries): {ex.Message}");
        }
    }
}
