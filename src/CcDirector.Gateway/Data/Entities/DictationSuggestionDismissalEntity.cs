namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One dictionary-suggestion the tenant has DISMISSED (devthrottle issue #2075): a term the mining pass keeps
/// wanting to suggest, that the customer told us to stop offering. Persisted per tenant so the dismissal
/// survives across devices and restarts and is honored identically by the Dictionary page and by the API -
/// "dismiss" is not offered again until the customer restores it.
///
/// KEY: composite <c>(tenant_id, Term)</c>. <see cref="Term"/> is the NORMALIZED canonical spelling (lower-
/// cased letters and digits only, the same fold the miner clusters on), so re-dismissing the same term under
/// a different casing or punctuation cannot create a second row, and the miner's exclusion check is an exact
/// match. Term is derived, not caller-supplied free text, so the composite-key-per-tenant pattern applies.
///
/// The evidence is snapshotted onto the row (<see cref="DisplayTerm"/>, <see cref="WrongCount"/>,
/// <see cref="TotalCount"/>, <see cref="VariantsJson"/>) so the "Dismissed terms" screen can still show WHY a
/// term was once offered ("was heard as Cooper Netties - wrong 2 of 2 times") months later, even if the
/// underlying transcripts have since aged out of retention.
///
/// <c>tenant_id</c> + the global query filter are inherited from <see cref="TenantScopedEntity"/>.
/// </summary>
public sealed class DictationSuggestionDismissalEntity : TenantScopedEntity
{
    /// <summary>The normalized canonical spelling (lower-cased alphanumerics). Part of the composite key.</summary>
    public string Term { get; set; } = "";

    /// <summary>The canonical spelling as it was shown to the customer (original casing), for display/restore.</summary>
    public string DisplayTerm { get; set; } = "";

    /// <summary>How many times the term was heard wrong when it was dismissed (evidence snapshot).</summary>
    public int WrongCount { get; set; }

    /// <summary>How many times the term was said at all when it was dismissed (evidence snapshot).</summary>
    public int TotalCount { get; set; }

    /// <summary>The wrong spellings observed at dismissal time, as a JSON array of <c>{heard,count}</c> - the
    /// evidence the "Dismissed terms" screen replays. Stored as JSON text (a bounded sub-document), so it does
    /// not need its own table.</summary>
    public string VariantsJson { get; set; } = "[]";

    /// <summary>When the customer dismissed this term (UTC).</summary>
    public DateTime DismissedAtUtc { get; set; }
}
