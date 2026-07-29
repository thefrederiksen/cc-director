namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The LATEST skill-placement outcome a Director reported for one agent family, in the
/// <c>skill_placement_state</c> table: how many of the library's skills actually reached that agent on
/// that machine, and what stopped the rest.
///
/// WHY THE GATEWAY NEEDS TO KNOW. The library's whole promise is that a skill published here is a skill
/// every agent can read. Whether that is TRUE is a fact only the machine can observe - the Gateway can
/// see that it served a skill and still be wrong about whether anything can read it. That gap is not
/// theoretical: a retired installer's leftovers occupied all three built-in names on a real machine, so
/// NOTHING was placed for Claude Code and the agent went on reading a two-month-old copy. The Gateway
/// served everything correctly and had no idea. Publishing without this feed is deploying blind.
///
/// OVERWRITE, NOT APPEND. One row per (tenant, director, agent kind); a new report replaces the row. The
/// question is "is placement working NOW", so a history would be storage and retention bought for a
/// question nobody asks. <see cref="ReceivedAtUtc"/> is stamped by the Gateway and
/// <see cref="ObservedAtUtc"/> by the Director, so a Director whose reports stopped is visible as itself
/// rather than as a machine where everything is fine.
///
/// KEY: composite <c>(tenant_id, DirectorId, AgentKind)</c>. Both non-tenant parts are CALLER-supplied,
/// which is exactly why the tenant is in the key: two accounts can run identically-named Directors, and
/// without it one account's report would fail to insert over the other's and learn the row exists.
///
/// THE PAYLOAD CARRIES SKILL IDS, COUNTS AND A DIRECTORY PATH - NEVER SKILL CONTENT and never anything
/// from the user's repositories. <see cref="ProblemsJson"/> is a serialized list of
/// <c>SkillPlacementProblemDto</c>, whose shape has no room for a file body.
///
/// <c>tenant_id</c> + the global query filter are inherited from <see cref="TenantScopedEntity"/>.
/// </summary>
public sealed class SkillPlacementStateEntity : TenantScopedEntity
{
    /// <summary>The Director that reported. Part of the composite primary key.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>The agent family the report is about ("ClaudeCode", "Codex", ...). Part of the key.</summary>
    public string AgentKind { get; set; } = "";

    /// <summary>The reporting machine's display name, so a problem can be pointed at a machine.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>How many skills the library held for that machine when the report was taken.</summary>
    public int Held { get; set; }

    /// <summary>How many of them the agent could actually read afterwards.</summary>
    public int Reachable { get; set; }

    /// <summary>True when the Director had never reached the Gateway, so nothing was placed for a reason
    /// that is not the machine's fault. Kept apart from a real placement failure because the two want
    /// completely different responses.</summary>
    public bool StoreMissing { get; set; }

    /// <summary>The serialized problem list (a JSON array of <c>SkillPlacementProblemDto</c>).</summary>
    public string ProblemsJson { get; set; } = "[]";

    /// <summary>When the DIRECTOR observed the outcome.</summary>
    public DateTime ObservedAtUtc { get; set; }

    /// <summary>When the GATEWAY received it. Both are kept so a Director that stopped reporting reads as
    /// stale rather than as healthy.</summary>
    public DateTime ReceivedAtUtc { get; set; }
}
