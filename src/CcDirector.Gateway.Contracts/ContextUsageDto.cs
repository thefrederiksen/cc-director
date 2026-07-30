namespace CcDirector.Gateway.Contracts;

/// <summary>
/// How full a session's context window is right now - the agent-agnostic gauge the user
/// interface binds to (capability <c>ContextUsage</c>). Deliberately small and separate from
/// <see cref="SessionUsageDto"/> (a Claude-JSONL-shaped totals object): this answers only the
/// narrow question "how full is the window", so any agent that can report it - whether by parsing
/// a transcript or by asking the running tool - can fill the same shape. Served by
/// GET /sessions/{sid}/context.
/// </summary>
public sealed class ContextUsageDto
{
    /// <summary>Tokens currently occupying the context window.</summary>
    public long UsedTokens { get; set; }

    /// <summary>The model's context-window size, or null when it is not known (an unmapped model);
    /// when null the user interface shows the raw used-token count with no percent.</summary>
    public long? WindowTokens { get; set; }

    /// <summary>Used as a percentage of the window (0-100), or null when the window size is
    /// unknown.</summary>
    public double? PercentUsed { get; set; }

    /// <summary>When this reading was last true (the latest counted assistant line's timestamp),
    /// or null when no usage-bearing line exists yet.</summary>
    public DateTime? AsOfUtc { get; set; }

    /// <summary>
    /// WHERE <see cref="WindowTokens"/> came from (issue #1100). The string form of
    /// <c>CcDirector.Core.Drivers.ContextWindowSource</c> - the contracts assembly carries no enum of its
    /// own so a new source never needs a lock-step change on both sides of the wire.
    ///
    /// It exists because provenance is the difference between a measurement and an assumption, and the
    /// screen could not previously tell you which one it was showing. "ctx 184k / 200k (92%)" in red
    /// looked exactly like a fact; the 200,000 had been derived from the letters "opus" in a model name,
    /// and the true window was 1,000,000.
    ///
    /// Null or "Unknown" means no window is known, and the gauge shows the raw used-token count alone.
    /// </summary>
    public string? WindowSource { get; set; }
}
