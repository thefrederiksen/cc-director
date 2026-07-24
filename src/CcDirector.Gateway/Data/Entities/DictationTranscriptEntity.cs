namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One stored dictation transcript (issue #509): the raw transcript and the cleaned transcript for a single
/// utterance, persisted per tenant in the <c>dictation_transcripts</c> table so a customer's
/// mistranscriptions can be mined later to build their dictionary. Write-only for now - there is no read
/// surface and no display; the reading, mining, and page experience are tracked separately (devthrottle
/// issue #2075). This entity is the storage foundation only.
///
/// ONE PATH, self-host is just one tenant: the write is identical whether we host the Gateway or the customer
/// self-hosts. On the single-tenant local install every row is the <see cref="Core.Tenancy.TenantId.Local"/>
/// ("local") tenant, so behavior is identical to a store with no tenant column; on the hosted Gateway each
/// account's rows are isolated by the same global query filter every other tenant-scoped table uses. This
/// retires the old host-global flat file (<c>dictation/sessions/*.jsonl</c>) that had to be switched off on
/// hosted precisely because it was not tenant-aware.
///
/// KEY: composite <c>(tenant_id, Id)</c> - the established per-tenant key pattern (mirroring cron_jobs,
/// workflows). <see cref="Id"/> is a code-minted GUID, per tenant, so two tenants can never collide.
/// <see cref="TimestampUtc"/> is UTC and round-trips through the backbone's UTC DateTime convention; it is
/// indexed with the tenant (<c>(tenant_id, TimestampUtc)</c>) for retention and any future read.
///
/// <c>tenant_id</c> + the global query filter are inherited from <see cref="TenantScopedEntity"/>.
/// </summary>
public sealed class DictationTranscriptEntity : TenantScopedEntity
{
    /// <summary>The per-tenant row id (a code-minted GUID). Part of the composite primary key.</summary>
    public string Id { get; set; } = "";

    /// <summary>When this transcript was recorded (UTC).</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>The turn id the transcription-health record shares, for correlation. May be null.</summary>
    public string? TurnId { get; set; }

    /// <summary>The surface that produced the utterance (e.g. "dictation", "voice", "batch").</summary>
    public string Source { get; set; } = "";

    /// <summary>The raw transcript as the provider returned it, before any dictionary correction.</summary>
    public string RawText { get; set; } = "";

    /// <summary>The cleaned transcript after the validated dictionary correction. Equals <see cref="RawText"/>
    /// when no correction was applied.</summary>
    public string CleanedText { get; set; } = "";

    /// <summary>True when the dictionary correction actually changed the raw transcript.</summary>
    public bool CleanupApplied { get; set; }
}
