namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One version of a skill's content in the <c>skill_versions</c> table. A version is a complete
/// snapshot - name, summary, triggers, and the body markdown - so nothing about a version can drift
/// after publication: a published or superseded row is IMMUTABLE, and only the single draft row of a
/// skill may change.
///
/// The <see cref="Summary"/> and <see cref="Triggers"/> are the REGISTER half: they ride every
/// session's launch briefing, one line per skill, and are what an agent uses to decide whether to
/// fetch. The <see cref="BodyMarkdown"/> is the fetched half, paid for only by a session that
/// actually uses the skill. Both are versioned together, so what an agent chose from and what it then
/// read can never come from different versions.
/// </summary>
public sealed class SkillVersionEntity : GatewayMintedKeyEntity
{
    /// <summary>The skill this version belongs to. (SkillId, Version) is unique per tenant.</summary>
    public string SkillId { get; set; } = "";

    /// <summary>1-based version number within the skill.</summary>
    public int Version { get; set; }

    /// <summary>"draft", "published", or "superseded". At most ONE draft and ONE published row exist
    /// per skill; publish flips draft to published and published to superseded in one transaction.</summary>
    public string Status { get; set; } = SkillVersionStatus.Draft;

    // ---- content snapshot -------------------------------------------------------------------------

    public string Name { get; set; } = "";

    /// <summary>ONE line: what the skill does. Rides every briefing, so it is length-capped hard.</summary>
    public string Summary { get; set; } = "";

    /// <summary>The phrases that should bring this skill to mind. Owned type serialized to a JSON
    /// column (the cron store's "bulky sub-doc -> JSON in a column" pattern).</summary>
    public List<string> Triggers { get; set; } = new();

    /// <summary>The skill's instructions (markdown). Capped by the store at 200 KB. This is the part
    /// an agent fetches only when it is about to use the skill.</summary>
    public string BodyMarkdown { get; set; } = "";

    // ---- provenance -------------------------------------------------------------------------------

    /// <summary>SHA-256 over the canonical complete bundle (metadata + triggers + body + ordered file
    /// hashes). Doubles as the optimistic-concurrency token for draft edits, and lets a client that
    /// already holds a skill know whether what it holds is current without fetching the body.</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>Who authored this version: a session id, an agent name, or "human:&lt;user&gt;".</summary>
    public string AuthoredBy { get; set; } = "";

    /// <summary>Optional one-line note describing what changed in this version.</summary>
    public string? ChangeNote { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? PublishedUtc { get; set; }
}

/// <summary>The legal <see cref="SkillVersionEntity.Status"/> values.</summary>
public static class SkillVersionStatus
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Superseded = "superseded";
}
