namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// One observed human input delta (<c>stat_delta</c>) - append only, recorded from the cutover forward.
///
/// Carried forward UNCHANGED from SQLite schema version 5
/// (<see cref="CcDirector.Gateway.Stats.GatewayStatsDatabase"/>). Every column name is the version 5 name,
/// because a self-host store already exists on disk with these exact names and a rename would strand it.
///
/// The design decisions behind the shape are recorded at length on MigrateToVersion1 in that file and are
/// not repeated here. The two that a reader of THIS file must not undo:
///  - <see cref="RepoId"/>, <see cref="ModelId"/> and <see cref="CheckoutId"/> are SURROGATE integers, never
///    a repository, model or path string in any form. The database is never asked to compare one.
///  - <see cref="IsVoice"/> and <see cref="Wingman"/> are stored flags, not derivable from
///    <see cref="Modality"/>: the voice test is case-insensitive while the totals split is case-sensitive,
///    and a turn TYPED while voice mode is on is a wingman turn.
/// </summary>
public sealed class StatDeltaEntity
{
    /// <summary>Surrogate row id (<c>id</c>), generated on add.</summary>
    public long Id { get; set; }

    /// <summary>The UTC hour bucket (<c>hour_utc</c>) in the form "yyyy-MM-ddTHH". A STRING, deliberately -
    /// every read projection groups and ranges on it as text, and
    /// <see cref="CcDirector.Gateway.Stats.GatewayStatsDatabase.ArchiveMarker"/> is a legal value.</summary>
    public string HourUtc { get; set; } = "";

    /// <summary>The session the delta was observed on (<c>session_id</c>).</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The input modality as reported (<c>modality</c>), compared case-SENSITIVELY by the totals
    /// split - which is why <see cref="IsVoice"/> exists beside it.</summary>
    public string Modality { get; set; } = "";

    /// <summary>The input surface as reported (<c>surface</c>).</summary>
    public string Surface { get; set; } = "";

    /// <summary>Whether this delta was voice (<c>is_voice</c>), stored rather than derived from
    /// <see cref="Modality"/>.</summary>
    public bool IsVoice { get; set; }

    /// <summary>The surrogate id of the repository ("owner/repo" repo name since version 4) the turn ran
    /// against (<c>repo_id</c>).</summary>
    public long RepoId { get; set; }

    /// <summary>Whether the session had voice mode ON at fold time (<c>wingman</c>). NOT
    /// <c>modality = 'voice'</c>: a session's entire turn delta - including TYPED turns - is wingman traffic
    /// while voice mode is on.</summary>
    public bool Wingman { get; set; }

    /// <summary>Turns in this delta (<c>turns</c>).</summary>
    public long Turns { get; set; }

    /// <summary>Characters in this delta (<c>chars</c>).</summary>
    public long Chars { get; set; }

    /// <summary>The surrogate id of the model the session was RECORDED running (<c>model_id</c>). NULLABLE,
    /// and the nullability is the design: a session's first turn folds before the agent has recorded a model,
    /// and this store never revisits a written row. A null before the model dimension began and a null after
    /// it are told apart by comparing <see cref="HourUtc"/> against the <c>models_since_utc</c> meta stamp.
    /// </summary>
    public long? ModelId { get; set; }

    /// <summary>The surrogate id of the LOCAL checkout the turn was driven in (<c>checkout_id</c>). NULLABLE
    /// because version 4 added it by ALTER TABLE; no live fold ever writes a null.</summary>
    public long? CheckoutId { get; set; }

    /// <summary>The owning tenant (<c>tenant</c>). A plain column, not part of any key.</summary>
    public string Tenant { get; set; } = "";
}
