namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The persisted form of one mission's WHY (<see cref="MissionNotes.MissionNoteStore.MissionNote"/>) in the EF
/// data layer: one row in the <c>mission_notes</c> table, keyed by the mission's NORMALIZED name.
///
/// <see cref="Key"/> is the PRIMARY KEY directly - it is the already-normalized grouping key (trimmed and
/// lower-cased by <see cref="MissionNotes.MissionNoteStore.NormalizeKey"/>), the same key the old store used
/// as its <c>Dictionary(StringComparer.Ordinal)</c> key. Because the store normalizes at the key boundary and
/// then compares ordinally, storing the normalized key as an ordinally-compared primary key (SQLite's default
/// BINARY collation) reproduces the lookup EXACTLY - there is no case-insensitive comparison at lookup time
/// and no case-fold question (the U+017F problem the work-list name key has does not arise, because the value
/// is already folded before it becomes the key).
///
/// <see cref="Mission"/> is the display name as last written (the original casing), kept alongside for
/// reference. <see cref="UpdatedAtUtc"/> is UTC and round-trips through the backbone's UTC DateTime
/// convention. <c>tenant_id</c> + the global query filter are inherited from the base.
/// </summary>
public sealed class MissionNoteEntity : TenantScopedEntity
{
    /// <summary>The normalized (trimmed, lower-cased) mission key. Primary key (ordinally compared).</summary>
    public string Key { get; set; } = "";

    /// <summary>The mission display name as last written (original casing).</summary>
    public string Mission { get; set; } = "";

    /// <summary>The mission's WHY text (trimmed). A row exists only for a set WHY - an empty WHY deletes it.</summary>
    public string Why { get; set; } = "";

    /// <summary>When the WHY was last set (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
