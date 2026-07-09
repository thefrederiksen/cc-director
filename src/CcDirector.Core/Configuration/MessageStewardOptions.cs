namespace CcDirector.Core.Configuration;

/// <summary>
/// Tunable policy for the fleet-message steward (flag: <c>messaging.steward</c>). The steward guards a
/// session's OUTGOING fleet messages at its own Director: exact-duplicate dedupe, a per-source rate limit,
/// and a broadcast throttle. Default-ON with GENEROUS limits so only genuine floods trip it; set
/// <see cref="Enabled"/> false to disable entirely (byte-identical to before). Every threshold is tunable.
/// </summary>
public sealed class MessageStewardOptions
{
    /// <summary>Master switch. Default true (on). When false the steward allows everything - byte-identical.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// An exact-duplicate message (same source + target + text) within this many milliseconds is suppressed
    /// as a safe duplicate (this also absorbs a retry loop). The window slides on each repeat, so a
    /// continuous loop stays suppressed until it stops for at least this long. Default 3000.
    /// </summary>
    public int DedupeWindowMs { get; set; } = 3000;

    /// <summary>
    /// Max per-target messages (send + ask) one source may send per rolling 60 seconds before it is rate
    /// limited. Generous by design - only a genuine flood trips it. Non-positive disables the rate limit.
    /// Default 60.
    /// </summary>
    public int PerSourcePerMin { get; set; } = 60;

    /// <summary>
    /// Max broadcasts one source may send per rolling 60 seconds before it is throttled. A broadcast fans
    /// out to the whole fleet, so it is capped tighter than per-target sends. Non-positive disables the
    /// broadcast throttle. Default 10.
    /// </summary>
    public int BroadcastsPerMin { get; set; } = 10;
}
