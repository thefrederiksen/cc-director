namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One model VERDICT on a mined dictionary-suggestion candidate (devthrottle issue #2115): whether the
/// screening model judged the candidate a distinctive term worth suggesting (a name, a brand, a piece of
/// jargon the speech model keeps misspelling) or ordinary vocabulary that must never be suggested.
///
/// WHY PERSISTED: the screening call costs real model spend, and a term's nature does not change - "that"
/// will never become jargon, "mindzie" will never become ordinary English. So a term is judged AT MOST ONCE
/// per tenant, ever; every later scan reads the stored verdict instead of asking the model again. That is
/// what keeps the steady-state screening cost per tenant near zero.
///
/// KEY: composite <c>(tenant_id, Term)</c>. <see cref="Term"/> is the NORMALIZED canonical spelling (lower-
/// cased letters and digits, the same fold the miner clusters on), so the same term re-mined under different
/// casing or punctuation maps onto its one verdict, exactly as the dismissal store matches.
///
/// <c>tenant_id</c> + the global query filter are inherited from <see cref="TenantScopedEntity"/>.
/// </summary>
public sealed class DictationSuggestionVerdictEntity : TenantScopedEntity
{
    /// <summary>The normalized canonical spelling (lower-cased alphanumerics). Part of the composite key.</summary>
    public string Term { get; set; } = "";

    /// <summary>The canonical spelling as the miner surfaced it (original casing), for display/diagnosis.</summary>
    public string DisplayTerm { get; set; } = "";

    /// <summary>True when the screening model approved the candidate as a distinctive term worth suggesting;
    /// false when it judged the candidate ordinary vocabulary (or an inflection cluster) to suppress.</summary>
    public bool Approved { get; set; }

    /// <summary>The model's one-line reason, verbatim - kept so a surprising verdict can be diagnosed later
    /// without re-asking the model. Display-only; nothing branches on it.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The model id that judged this candidate (for diagnosis when screening quality is questioned).</summary>
    public string Model { get; set; } = "";

    /// <summary>When the verdict was recorded (UTC).</summary>
    public DateTime JudgedAtUtc { get; set; }
}
