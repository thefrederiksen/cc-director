namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The head record of a skill in the <c>skills</c> table: identity and lifecycle only, never content.
/// Content lives on immutable <see cref="SkillVersionEntity"/> rows so a fetched skill can name the
/// exact version it read, and so a bad edit is superseded rather than overwritten.
///
/// A SKILL IS NOT A WORKFLOW. A workflow is a way of working that governs a whole mission; a skill is
/// a capability an agent reaches for mid-task. They share this storage shape - the reason this file
/// reads like <see cref="WorkflowEntity"/> - but they are separate registers with separate lists,
/// because putting them in one list would make the fleet choose between them as if they were
/// alternatives (devthrottle_internal issue 995).
///
/// The key is the skill's public slug id ("move-session", "fleet-comms"), minted by the author and
/// validated by the store. Built-in skills (<see cref="IsBuiltIn"/>) are seeded from embedded
/// resources at startup and are READ-ONLY: they are DevThrottle's, they update with the binary, and
/// the sanctioned way to customize one is to clone it. <see cref="ShippedContentHash"/> records what
/// the running binary ships so the seeder can tell "already current" from "needs republishing".
/// </summary>
public sealed class SkillEntity : TenantScopedEntity
{
    /// <summary>The skill's slug id (part of the primary key), e.g. "move-session".</summary>
    public string Id { get; set; } = "";

    /// <summary>True for the skills the Gateway ships. Built-ins can never be deleted or edited.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Soft delete for user-defined skills. Archived skills leave the register but their
    /// versions remain readable by explicit version.</summary>
    public bool Archived { get; set; }

    /// <summary>
    /// The owner's switch: OFF means the skill is left out of every agent's launch briefing and the
    /// default fetch is refused with a clear message - and NOTHING is deleted. Versions and history
    /// stay, pinned explicit-version reads keep resolving, and the switch is instant both ways
    /// fleet-wide. Distinct from <see cref="Archived"/>: off is a standing choice shown in the
    /// register; archived is removal from it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The highest version number ever minted for this skill (the draft counter).</summary>
    public int LatestVersion { get; set; }

    /// <summary>The currently published version number, or null when nothing is published yet (a
    /// draft-only skill, invisible to the register listing and to every briefing).</summary>
    public int? PublishedVersion { get; set; }

    /// <summary>Built-ins only: the canonical content hash of what THIS binary ships for the skill.</summary>
    public string? ShippedContentHash { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
