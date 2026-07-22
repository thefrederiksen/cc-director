namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The one-time import marker for the <c>devices.json</c> -> <c>device_credentials</c> migration (MTR-14A),
/// persisted in the <c>device_import_markers</c> table. It is what makes the importer IDEMPOTENT: the importer
/// reads the legacy JSON registry and inserts a row per device AND this marker inside a single transaction, so
/// a marker row exists only when the import committed in full. A later run that finds the marker does nothing -
/// the JSON file is never re-imported, so re-running the importer (a restart, a redeploy, a retry) can never
/// duplicate rows or resurrect devices that were revoked after the import.
///
/// A GLOBAL table like <see cref="TenantEntity"/> and <see cref="DeviceCredentialEntity"/> - not tenant-scoped,
/// because the import it guards spans every tenant's devices at once and runs before any tenant is resolved.
/// </summary>
public sealed class DeviceImportMarkerEntity
{
    /// <summary>The source identity of the import this marker records - the absolute path of the
    /// <c>devices.json</c> file that was imported. The natural primary key: one marker per source file, so the
    /// importer's idempotency key is the exact file it migrated. Ordinally compared.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>When the import committed (UTC).</summary>
    public DateTime ImportedAtUtc { get; set; }

    /// <summary>How many device rows the import wrote (0 when the source file was absent or empty - the import
    /// still ran and is still marked done, so an empty legacy registry is not re-scanned forever).</summary>
    public int ImportedCount { get; set; }
}
