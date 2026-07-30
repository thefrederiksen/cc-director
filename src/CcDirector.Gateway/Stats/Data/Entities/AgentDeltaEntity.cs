namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// One observed per-agent tally delta (<c>agent_delta</c>) - append only.
///
/// Carried forward UNCHANGED from SQLite schema version 5. This is its OWN table and that is not a stylistic
/// choice: the agent tally is NOT derivable from <see cref="StatDeltaEntity"/>, because the attribution has
/// two callers and only one of them feeds the totals. The ordinary delta path attributes the same delta the
/// totals get; the first-fold back-fill attributes a session's PRIOR high-water - turns ALREADY counted in
/// the totals from before the agent tally existed.
///
/// So carrying an agent id on <c>stat_delta</c> has no correct behaviour once the back-fill fires: writing a
/// row inflates the totals, and not writing one leaves the agent tally short. The accepted cost is that
/// <c>stat_delta</c> cannot answer turns-by-agent-by-hour, and it must not pretend to.
/// </summary>
public sealed class AgentDeltaEntity
{
    /// <summary>Surrogate row id (<c>id</c>), generated on add.</summary>
    public long Id { get; set; }

    /// <summary>The surrogate id of the agent (<c>agent_id</c>).</summary>
    public long AgentId { get; set; }

    /// <summary>Whether this delta was voice (<c>is_voice</c>).</summary>
    public bool IsVoice { get; set; }

    /// <summary>Turns in this delta (<c>turns</c>).</summary>
    public long Turns { get; set; }

    /// <summary>Characters in this delta (<c>chars</c>).</summary>
    public long Chars { get; set; }

    /// <summary>The owning tenant (<c>tenant</c>). A plain column, not part of any key.</summary>
    public string Tenant { get; set; } = "";
}
