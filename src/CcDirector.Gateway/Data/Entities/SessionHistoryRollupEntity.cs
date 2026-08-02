namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One repository group's one-day roll-up paragraph, in the <c>session_history_rollups</c> table
/// (issue #2194). The History page shows a written summary per repository per day; producing that
/// with a model on every page load would be a real recurring spend, so the roll-up is computed ONCE
/// in the background history sweep and CACHED here. A past day whose sessions have all ended never
/// changes, so its cached row is effectively permanent; <see cref="InputHash"/> detects the cases
/// that do change (today's sessions still running, a late-arriving session summary) and marks the
/// row stale for recomputation.
///
/// Derived from prompt-derived session summaries, so it is customer content: tenant-scoped by the
/// global query filter like everything else in this database. Retention matches session_history
/// (90 days), pruned by the same sweep.
/// </summary>
public sealed class SessionHistoryRollupEntity : TenantScopedEntity
{
    /// <summary>The grouping key: owner/repo when the origin remote is known, else the repository
    /// path. Caller-supplied (derived from pushed session facts), so the key is composite with the
    /// tenant - the session_spend reasoning.</summary>
    public string RepoKey { get; set; } = "";

    /// <summary>The UTC day this roll-up covers (date component only).</summary>
    public DateTime DayUtc { get; set; }

    /// <summary>The written paragraph. Null while pending or after giving up (see Attempts).</summary>
    public string? SummaryText { get; set; }

    /// <summary>Hash of the inputs (session ids, their summary state, their endings) the paragraph
    /// was computed from. A mismatch against the current inputs means the row is stale.</summary>
    public string InputHash { get; set; } = "";

    /// <summary>Bounded retry counter, reset whenever the inputs change.</summary>
    public int Attempts { get; set; }

    public DateTime ComputedAtUtc { get; set; }

    /// <summary>
    /// When the session summaries this paragraph was written from were READ. Not the same as
    /// <see cref="ComputedAtUtc"/>, which is when the finished paragraph was saved - the model call sits
    /// between them.
    ///
    /// It exists so a row can be judged stale by its MATERIAL rather than by its save time, and it is read
    /// by <see cref="Gateway.History.SessionHistoryStore.ReadRollups"/>, which never serves a row whose
    /// material predates the account's erasure. The insert of a cached paragraph cannot be made conditional
    /// in one portable statement, so a paragraph computed before a delete can be inserted after it; the
    /// compensating delete normally removes it, but a process that stops in between would leave it. With
    /// this column that orphan is unreachable rather than merely unlikely - it is never served, and the next
    /// erasure or the retention prune removes it.
    /// </summary>
    /// <remarks>Rows that predate this column carry the default minimum date, so any erasure at all makes
    /// them unreachable. That is the safe direction and it is deliberate: a paragraph written before this
    /// column existed cannot prove its material is newer than the member's delete.</remarks>
    public DateTime MaterialReadAtUtc { get; set; }
}
