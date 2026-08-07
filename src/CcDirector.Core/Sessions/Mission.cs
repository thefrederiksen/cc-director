namespace CcDirector.Core.Sessions;

/// <summary>
/// A Mission: the named unit of work a pod of sessions is collectively chartered to accomplish
/// (see docs/new_architecture/mission-as-first-class-unit-of-work.md). A Mission is its OWN persisted
/// record - not merely an attachment field on a session - so it survives a Director/Manager restart and
/// later anchors the cockpit map. Sessions ATTACH to a Mission by its <see cref="MissionId"/>.
///
/// Role cardinality (one Architect, one Manager, N Workers) is enforced by the derived role model, so a
/// Mission deliberately stores NO role seats - only its identity and name.
///
/// MISSIONS ARE FLAT. Nesting (a parent link, making a tree of Missions) was specified in the design
/// document, built, and tested - and then never used once across every Mission this fleet created. It was
/// removed on 2026-08-07 rather than carried indefinitely: an unused field still has to be understood by
/// everyone who reads this type, kept correct in every store and route that touches it, and reasoned about
/// by every feature added afterwards. If a real case for sub-Missions turns up, add it back deliberately
/// then - the design document records the original reasoning and the removal.
/// </summary>
public sealed class Mission
{
    /// <summary>Stable identity of the Mission, minted at creation. Sessions attach by this value.</summary>
    public Guid MissionId { get; set; }

    /// <summary>Human-friendly name of the Mission (e.g. "Session Lifecycle").</summary>
    public string MissionName { get; set; } = string.Empty;

    /// <summary>
    /// WHY this mission exists, in the owner's own words - shown front and center on its card, because a
    /// mission with no stated reason is a red flag the screen makes obvious rather than a silent blank.
    /// Empty means UNSET, and the card shows its flag; there is no separate "has a why" boolean.
    ///
    /// This lives ON THE MISSION, keyed by <see cref="MissionId"/> like everything else. It used to live in
    /// its own <c>mission_notes</c> table keyed by the mission's LOWER-CASED NAME, which meant the WHY was
    /// attached to a string rather than to a mission: two missions sharing a name shared one WHY, and
    /// renaming a mission would have orphaned it silently - the card simply falling back to "no why set".
    /// That is why this moved here BEFORE rename was built rather than after.
    /// </summary>
    public string Why { get; set; } = string.Empty;

    /// <summary>When <see cref="Why"/> was last set (UTC), or null if it has never been set.</summary>
    public DateTimeOffset? WhyUpdatedAt { get; set; }

    /// <summary>UTC timestamp the Mission was created. Used for stable list ordering.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The tenant that owns this Mission, stamped at creation from the CALLER's authenticated identity -
    /// never from anything a client sends. Every read is filtered on it, so a mission is only ever served to
    /// the tenant that wrote it.
    ///
    /// Null means UNATTRIBUTED: a record written before missions carried an owner. On a single-tenant
    /// install there is exactly one tenant by construction, so <see cref="MissionStore"/> may be told to
    /// adopt those rows as that tenant; where more than one tenant shares the store, an unattributed row
    /// belongs to nobody and is served to nobody. See <see cref="MissionStore"/> for the rule.
    /// </summary>
    public string? TenantId { get; set; }
}
