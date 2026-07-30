namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// A runtime scalar keyed by name (<c>meta</c>) - not a statistic, but a fact about when a statistic began.
///
/// Carried forward UNCHANGED from SQLite schema version 5, including the composite primary key
/// (<c>tenant</c>, <c>name</c>).
///
/// Its two occupants:
///  - <c>agents_since_utc</c> - when the per-agent breakdown started counting. PER TENANT since version 5,
///    stamped on that tenant's first observation and never moved after that.
///  - <see cref="CcDirector.Gateway.Stats.GatewayStatsDatabase.ModelsSinceKey"/> (<c>models_since_utc</c>) -
///    when the model dimension began. A SCHEMA fact rather than a per-tenant one: it rides the local tenant's
///    row and is read tenant-agnostically. Without it a null model id is unreadable, because a row folded
///    BEFORE the model dimension existed and a row folded after it whose session had simply not recorded a
///    model yet both store null, and a page that cannot tell them apart reports the entire history as "model
///    unknown" as though the data were missing rather than never collected.
///
/// Writes are insert-if-absent - ON CONFLICT DO NOTHING, matching version 5's INSERT OR IGNORE. These stamps
/// are written once and never moved: if a row already carries the key, the ORIGINAL is the true beginning,
/// and overwriting it with a later time would silently reclassify real rows as predating the dimension.
/// </summary>
public sealed class MetaEntity
{
    /// <summary>The owning tenant (<c>tenant</c>). Part of the primary key.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>The scalar's name (<c>name</c>). Part of the primary key.</summary>
    public string Name { get; set; } = "";

    /// <summary>The scalar's value (<c>value</c>).</summary>
    public string Value { get; set; } = "";
}
