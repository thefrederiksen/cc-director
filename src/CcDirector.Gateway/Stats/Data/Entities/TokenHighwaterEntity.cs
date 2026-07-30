namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// The last cumulative token counts seen for a live session (<c>token_highwater</c>), so only the INCREASE
/// folds. Mirrors <see cref="SessionHighwaterEntity"/> exactly: a reported count that DROPPED (a Director
/// restarted the session with a fresh conversation) is fresh spend from zero, not a negative.
///
/// Carried forward UNCHANGED from SQLite schema version 5, including the composite primary key
/// (<c>tenant</c>, <c>session_id</c>).
///
/// All four counts are running sums over the whole transcript, so they only grow within one conversation -
/// which is what makes the high-water increment correct. Context occupancy is NOT here: it is not cumulative
/// and high-watering it would be meaningless.
///
/// A READ-MODIFY-WRITE PATH: every write must be an explicit ON CONFLICT DO UPDATE, never a change-tracked
/// read-then-save. See <see cref="SessionHighwaterEntity"/>.
/// </summary>
public sealed class TokenHighwaterEntity
{
    /// <summary>The owning tenant (<c>tenant</c>). Part of the primary key.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>The session (<c>session_id</c>). Part of the primary key.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The highest cumulative input token count seen (<c>input_tokens</c>).</summary>
    public long InputTokens { get; set; }

    /// <summary>The highest cumulative output token count seen (<c>output_tokens</c>).</summary>
    public long OutputTokens { get; set; }

    /// <summary>The highest cumulative cache-read token count seen (<c>cache_read_tokens</c>).</summary>
    public long CacheReadTokens { get; set; }

    /// <summary>The highest cumulative cache-creation token count seen (<c>cache_creation_tokens</c>).</summary>
    public long CacheCreationTokens { get; set; }

    /// <summary>What <see cref="InputTokens"/> held immediately before the most recent raise
    /// (<c>previous_input_tokens</c>), so the raise statement returns what it changed rather than leaving the
    /// writer to infer it. See <see cref="SessionHighwaterEntity.PreviousTurns"/>.</summary>
    public long PreviousInputTokens { get; set; }

    /// <summary>What <see cref="OutputTokens"/> held immediately before the most recent raise
    /// (<c>previous_output_tokens</c>).</summary>
    public long PreviousOutputTokens { get; set; }

    /// <summary>What <see cref="CacheReadTokens"/> held immediately before the most recent raise
    /// (<c>previous_cache_read_tokens</c>).</summary>
    public long PreviousCacheReadTokens { get; set; }

    /// <summary>What <see cref="CacheCreationTokens"/> held immediately before the most recent raise
    /// (<c>previous_cache_creation_tokens</c>).</summary>
    public long PreviousCacheCreationTokens { get; set; }
}
