namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// One observed AGENT-TO-AGENT delta (<c>agent_driven_delta</c>) - turns another agent drove into a session.
/// Append only.
///
/// Carried forward UNCHANGED from SQLite schema version 5. A SEPARATE table from <see cref="StatDeltaEntity"/>
/// deliberately, for two reasons recorded there:
///  - These turns must NEVER enter the human totals, hourly series or buckets - the voice-versus-typed
///    numbers must stay about the human. Behind a lane flag on the shared table that becomes a RULE every
///    human aggregate query has to remember, and it fails SILENTLY: the voice share would quietly start
///    including agent traffic and nothing would look wrong. In its own table it CANNOT be summed in by
///    accident.
///  - The shapes disagree. The agent-driven high-water is keyed by SESSION ALONE, while the human high-water
///    is keyed by session AND modality AND surface. One table would force one of them to lie about its key.
///
/// No hour, no repository, no modality: this lane feeds only the per-agent tally and a global pair, so
/// carrying columns nothing populates would be a dimension nothing emits.
/// </summary>
public sealed class AgentDrivenDeltaEntity
{
    /// <summary>Surrogate row id (<c>id</c>), generated on add.</summary>
    public long Id { get; set; }

    /// <summary>The surrogate id of the driving agent (<c>agent_id</c>).</summary>
    public long AgentId { get; set; }

    /// <summary>Turns in this delta (<c>turns</c>).</summary>
    public long Turns { get; set; }

    /// <summary>Characters in this delta (<c>chars</c>).</summary>
    public long Chars { get; set; }

    /// <summary>The owning tenant (<c>tenant</c>). A plain column, not part of any key.</summary>
    public string Tenant { get; set; } = "";
}
