using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Data;

/// <summary>
/// How <see cref="LegacyJsonImport.Recoverable"/> reacts to a CORRUPT legacy input file (one the store's
/// import cannot parse). The choice is per-store BY CRITICALITY, and it is a deliberate contract, not a bug:
/// <list type="bullet">
/// <item><see cref="FailLoud"/> (the default) - an operationally-critical store (cron jobs, work-list claims,
/// snooze/hold state, push subscriptions, wingman instructions) must NEVER silently lose data, so a corrupt
/// file THROWS and is left in place for the operator; the Gateway does not boot half-blind. All five shipped
/// stores use this and are byte-identical (they omit the parameter).</item>
/// <item><see cref="Quarantine"/> - a COSMETIC store whose data must not block boot (the mission WHY notes)
/// renames the corrupt file aside as <c>&lt;path&gt;.corrupt-&lt;stamp&gt;</c> (preserving the bytes for the
/// operator) and boots EMPTY, reproducing that store's long-standing quarantine behaviour exactly.</item>
/// </list>
/// </summary>
public enum CorruptFilePolicy
{
    /// <summary>A corrupt input file throws and is left in place (the default; operationally-critical stores).</summary>
    FailLoud,

    /// <summary>A corrupt input file is renamed aside as <c>.corrupt-&lt;stamp&gt;</c> and the store boots empty
    /// (cosmetic stores that must not block boot).</summary>
    Quarantine,
}

/// <summary>
/// Thrown by a store's import when the legacy INPUT FILE cannot be parsed (a corrupt document) - as opposed
/// to an infrastructure failure (a database or migration error). <see cref="LegacyJsonImport.Recoverable"/>
/// distinguishes the two: under <see cref="CorruptFilePolicy.Quarantine"/> it catches ONLY this type (a bad
/// input) and quarantines; every other exception - a DB/migrate failure - always propagates, even under
/// Quarantine, so infrastructure problems still fail loud.
///
/// It derives from <see cref="InvalidOperationException"/> so the fail-loud stores' existing
/// <c>Assert.Throws&lt;InvalidOperationException&gt;</c> corrupt-file tests stay green unchanged.
/// </summary>
public sealed class LegacyJsonImportCorruptException : InvalidOperationException
{
    public LegacyJsonImportCorruptException(string message, Exception? inner = null) : base(message, inner) { }
}

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
    ///
    /// <paramref name="corruptFilePolicy"/> selects what happens when <paramref name="importCommitted"/>
    /// reports a corrupt INPUT file by throwing a <see cref="LegacyJsonImportCorruptException"/>. The default,
    /// <see cref="CorruptFilePolicy.FailLoud"/>, does not catch it - so the five shipped stores are unchanged.
    /// <see cref="CorruptFilePolicy.Quarantine"/> catches ONLY that type, renames the corrupt file aside as
    /// <c>.corrupt-&lt;stamp&gt;</c>, and returns (boot empty). A DB/migrate failure is NOT a
    /// <see cref="LegacyJsonImportCorruptException"/>, so it always propagates - infrastructure still fails
    /// loud even under Quarantine.
    /// </summary>
    public static void Recoverable(string path, string logPrefix, Func<bool> isPopulated, Action importCommitted,
        CorruptFilePolicy corruptFilePolicy = CorruptFilePolicy.FailLoud)
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

        // First upgrade: the store parses and inserts its rows, then the imported file is renamed aside
        // best-effort. A parse error surfaces as LegacyJsonImportCorruptException; under FailLoud it
        // propagates and the file is left in place (fail-loud, all-or-nothing), under Quarantine it is
        // renamed aside as .corrupt and the store boots empty. Any OTHER exception (a DB/migrate failure)
        // always propagates - infrastructure is never quarantined.
        try
        {
            importCommitted();
        }
        catch (LegacyJsonImportCorruptException) when (corruptFilePolicy == CorruptFilePolicy.Quarantine)
        {
            Quarantine(path, logPrefix);
            return;
        }
        TryRenameAside(path, logPrefix);
    }

    /// <summary>
    /// Preserve an unreadable input file as <c>&lt;path&gt;.corrupt-&lt;stamp&gt;</c> and log loudly, then let
    /// the store boot empty. Used only under <see cref="CorruptFilePolicy.Quarantine"/> for a COSMETIC store
    /// whose corrupt data must not block Gateway boot; the bytes are preserved for the operator, never
    /// silently overwritten. If even the quarantine rename fails, that exception propagates.
    /// </summary>
    private static void Quarantine(string path, string logPrefix)
    {
        var quarantinePath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(path, quarantinePath);
        FileLog.Write($"{logPrefix} Import FAILED: legacy file {path} is corrupt; quarantined to {quarantinePath}; " +
                      "starting empty (cosmetic store - a corrupt input must not block Gateway boot). Operator " +
                      "action: inspect the quarantined file to recover the data.");
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
