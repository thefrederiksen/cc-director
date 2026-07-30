namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// One observed token-spend increase (<c>token_delta</c>) - append only, high-watered per session so only the
/// GROWTH since the last poll is folded, never the running total.
///
/// Carried forward UNCHANGED from SQLite schema version 5 (MigrateToVersion3 in
/// <see cref="CcDirector.Gateway.Stats.GatewayStatsDatabase"/>), where the reasoning is written out in full.
/// The two rules that must survive the port:
///  - SPEND, NOT OCCUPANCY. All four columns are cumulative, additive counts. Context-window occupancy is a
///    GAUGE - it goes up AND down - so summing it is meaningless and it is deliberately absent. Adding it
///    here would be adding a number that lies the moment it is aggregated.
///  - NO modality and NO surface. Tokens are the model's work, not the human's input channel, and the total
///    arrives per session as one cumulative figure that cannot be split across voice/typed buckets. Those
///    columns would advertise a division the data cannot make.
/// </summary>
public sealed class TokenDeltaEntity
{
    /// <summary>Surrogate row id (<c>id</c>), generated on add.</summary>
    public long Id { get; set; }

    /// <summary>The UTC hour bucket (<c>hour_utc</c>) in the form "yyyy-MM-ddTHH". A string, as on
    /// <see cref="StatDeltaEntity.HourUtc"/>.</summary>
    public string HourUtc { get; set; } = "";

    /// <summary>The surrogate id of the model the spend is attributed to (<c>model_id</c>). NULLABLE, and
    /// unlike <see cref="StatDeltaEntity.ModelId"/> a null here has exactly ONE meaning - "not recorded yet" -
    /// because every token row is written after the model dimension existed. No since-stamp is needed to read
    /// it.</summary>
    public long? ModelId { get; set; }

    /// <summary>Input tokens in this delta (<c>input_tokens</c>).</summary>
    public long InputTokens { get; set; }

    /// <summary>Output tokens in this delta (<c>output_tokens</c>).</summary>
    public long OutputTokens { get; set; }

    /// <summary>Cache-read tokens in this delta (<c>cache_read_tokens</c>).</summary>
    public long CacheReadTokens { get; set; }

    /// <summary>Cache-creation tokens in this delta (<c>cache_creation_tokens</c>).</summary>
    public long CacheCreationTokens { get; set; }

    /// <summary>The owning tenant (<c>tenant</c>). A plain column, not part of any key.</summary>
    public string Tenant { get; set; } = "";
}
