namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A session's CUMULATIVE token spend, lean enough to ride the roster snapshot on every session on every
/// poll (issue #1637, the gateway-sqlite tokens wire). The running sums of input, output and cached tokens
/// across the whole conversation, read once at each turn-end from the tool's own records and stamped on the
/// session - never recomputed on the poll path.
///
/// This is DELIBERATELY not <see cref="SessionUsageDto"/>. That type carries the per-turn breakdown (up to
/// sixty <see cref="TurnUsageDto"/> entries) for the on-demand usage view, which is far too heavy to send
/// for every session on every roster poll. This carries only the totals a governance page sums, so the wire
/// stays cheap. The on-demand view keeps using the full type through its own command.
///
/// Every field is a running total, so it only grows within one agent session; a value that DROPS means the
/// tool started a fresh conversation under the same session id (a restart), which the Gateway fold treats
/// the same way it treats a dropped input-stats count - as fresh spend from zero, never as a negative.
///
/// SPEND, NOT OCCUPANCY. <see cref="ContextTokens"/> is the one gauge here and it is NOT additive: it is how
/// full the window was at the latest turn-end, carried so a client can show the live context figure without
/// a second read. It must never be summed across turns. The additive spend numbers are the other four.
/// </summary>
public sealed class TokenTotalsDto
{
    /// <summary>Running sum of uncached input tokens across the whole session.</summary>
    public long InputTokens { get; set; }

    /// <summary>Running sum of output tokens across the whole session.</summary>
    public long OutputTokens { get; set; }

    /// <summary>Running sum of cache-read input tokens across the whole session.</summary>
    public long CacheReadTokens { get; set; }

    /// <summary>Running sum of cache-creation input tokens across the whole session.</summary>
    public long CacheCreationTokens { get; set; }

    /// <summary>How full the context window was at the latest turn-end (input + cache read + cache
    /// creation of the last assistant line). A GAUGE, not spend: never summed across turns.</summary>
    public long ContextTokens { get; set; }

    /// <summary>When the latest counted assistant line was written (UTC), or null when the records carry
    /// no usage-bearing line yet. Lets a client tell a live reading from a stale one.</summary>
    public DateTime? AsOfUtc { get; set; }
}
