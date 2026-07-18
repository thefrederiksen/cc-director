namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The persisted form of one pending snooze (<see cref="Snooze.SnoozeRegistry.SnoozeEntry"/>) in the EF data
/// layer: one row in the <c>snoozes</c> table, keyed by <see cref="SessionId"/>.
///
/// <see cref="SessionId"/> is the PRIMARY KEY directly - it is an externally supplied, globally unique GUID
/// string (compared ordinally), the natural key, so there is no surrogate id and no case-fold question (the
/// worklist name problem does not arise here). The armed-vs-deferred invariant (exactly one of
/// <see cref="SnoozeUntilUtc"/> / <see cref="PendingMinutes"/> non-null) is maintained in the store logic,
/// not by a database constraint - matching the legacy behaviour exactly and staying provider-agnostic.
///
/// <see cref="SnoozeUntilUtc"/> and <see cref="OwnerTurnBaselineUtc"/> are UTC and round-trip through the
/// backbone's UTC DateTime convention (which registers converters for both DateTime and nullable DateTime,
/// so a null stays null and a value comes back as UTC).
/// </summary>
public sealed class SnoozeEntity : TenantScopedEntity
{
    /// <summary>The session this snooze holds. Primary key (an ordinal, globally unique GUID string).</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The absolute UTC return time of an ARMED snooze, or null for a DEFERRED one.</summary>
    public DateTime? SnoozeUntilUtc { get; set; }

    /// <summary>
    /// The Director that owned the session when the snooze was set (used to bound the registry). NULLABLE:
    /// the legacy store retained a null DirectorId exactly as read (it never coerced it to ""), so the
    /// column is nullable and the import preserves the exact value - a null stays null on round-trip.
    /// </summary>
    public string? DirectorId { get; set; }

    /// <summary>The remembered length of a DEFERRED snooze (clock starts on landing), or null when armed.</summary>
    public int? PendingMinutes { get; set; }

    /// <summary>The owning Director's own last-owner-turn stamp captured when the hold was asked for, or null.</summary>
    public DateTime? OwnerTurnBaselineUtc { get; set; }
}
