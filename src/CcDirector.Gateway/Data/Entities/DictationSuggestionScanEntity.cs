namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The stored RESULT of a tenant's most recent dictionary-suggestion scan (devthrottle issue #2115). One row
/// per tenant, overwritten by each scan.
///
/// WHY STORED: scanning is a scheduled daily job (just after midnight in the tenant's own time zone) or an
/// explicit "Scan now" - it mines up to 5,000 transcripts and may spend a model call. The navigation badge
/// and the Dictionary page must NOT trigger that work; they read this row. This replaces the old design
/// where a 45-second badge poll recomputed the mining every two minutes.
///
/// The row also carries the screening outcome: when the screening model could not be reached the scan says
/// so here (<see cref="ScreeningOk"/>, <see cref="ScreeningError"/>) and the page shows it - unjudged
/// candidates are NEVER shown unscreened.
///
/// <c>tenant_id</c> + the global query filter are inherited from <see cref="TenantScopedEntity"/>.
/// </summary>
public sealed class DictationSuggestionScanEntity : TenantScopedEntity
{
    /// <summary>When the scan ran (UTC). The daily sweep's due check reads this - the row IS the durable
    /// "last ran" marker, so a Gateway restart never double-runs or skips a day.</summary>
    public DateTime ScannedAtUtc { get; set; }

    /// <summary>True when every candidate that needed screening got a verdict (including the trivial case of
    /// nothing new to screen). False when the screening model was unreachable or answered unusably - the
    /// scan still stored previously-approved suggestions, and the page says screening is unavailable.</summary>
    public bool ScreeningOk { get; set; }

    /// <summary>When <see cref="ScreeningOk"/> is false: the operator-readable reason (exception message).
    /// Empty when screening succeeded.</summary>
    public string ScreeningError { get; set; } = "";

    /// <summary>The approved, evidence-carrying suggestions as a JSON array (term, variants, counts) - the
    /// exact list the Dictionary page and the badge serve. A bounded sub-document (at most the miner's
    /// MaxSuggestions entries), so it does not need its own table.</summary>
    public string SuggestionsJson { get; set; } = "[]";
}
