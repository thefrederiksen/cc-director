using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Prompts;

/// <summary>
/// The prompt log's retention window and its enforcement (CR-3b, devthrottle_internal issue #1180).
/// Before this sweep existed the log said "retention is unbounded" out loud, which for a store of
/// customer prompt text was the finding, not a feature. Now the log sits on the same footing as the
/// other bounded stores (turn-review 7 days, session history 90 days, dictation transcripts 90 days,
/// voice-turn archives 24 hours): a window, and a sweep that makes the window true.
///
/// This sweep does NOT ride the <see cref="Tenancy.TenantScopedSweep"/> seam, on purpose: that seam
/// exists to enter an ambient tenant scope for the database stores, and it fans out over the tenant
/// CENSUS. The prompt log is file-partitioned and takes its tenant explicitly, and its purge walks the
/// partitions found ON DISK - so a partition orphaned by a deleted tenant (which the census can no
/// longer name) still ages out instead of keeping that customer's prompt text forever.
///
/// Timer cadence and lifecycle are owned by GatewayHost (the ActivityRetentionSweep pattern).
/// </summary>
public sealed class PromptLogRetentionSweep
{
    /// <summary>
    /// THE default retention window for prompt/reply history: 90 days, the owner's ruling of 2026-07-31
    /// (chosen over 30 so month-over-month comparison across history is possible). The customer-facing
    /// data map (devthrottle_internal/docs/architecture/data-map.md) states the same number and must
    /// change with it - one constant, one table row, together.
    /// </summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(90);

    /// <summary>
    /// Self-host override: a positive whole number of days. Self-host is the operator's own machine and
    /// their own data, so they may keep history LONGER (or shorter) than the product default. On the
    /// hosted Gateway this variable is deliberately IGNORED: hosted retention is the published product
    /// default, and an operator-side variable must not quietly change a promise made to customers.
    /// </summary>
    public const string RetentionDaysEnvVar = "CC_GATEWAY_PROMPT_RETENTION_DAYS";

    private readonly GatewayPromptLog _log;
    private readonly TimeSpan _retention;

    public PromptLogRetentionSweep(GatewayPromptLog log, TimeSpan retention)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention), "The prompt-log retention window must be positive.");
        _retention = retention;
    }

    /// <summary>The window this sweep enforces (resolved once at construction).</summary>
    public TimeSpan Retention => _retention;

    /// <summary>
    /// Resolve the effective window from the deployment mode and the raw environment value. Pure and
    /// static so a test can prove every branch without touching process environment or a real host.
    /// An UNSET variable is the default everywhere. A SET variable is honored on self-host and refused
    /// with a loud throw when malformed - a typo silently falling back to the default would be the
    /// operator believing a window that is not the one running. On hosted a set variable is ignored
    /// (and logged), never applied.
    /// </summary>
    public static TimeSpan ResolveRetention(bool isHosted, string? configuredDays)
    {
        if (string.IsNullOrWhiteSpace(configuredDays))
            return DefaultRetention;

        if (isHosted)
        {
            FileLog.Write($"[PromptLogRetentionSweep] {RetentionDaysEnvVar} is set but this is the hosted Gateway - ignored; hosted retention is the product default ({DefaultRetention.TotalDays:0} days)");
            return DefaultRetention;
        }

        if (!int.TryParse(configuredDays.Trim(), out var days) || days <= 0)
            throw new InvalidOperationException(
                $"{RetentionDaysEnvVar} must be a positive whole number of days; found '{configuredDays}'. " +
                "Unset it to use the default, or set it to the number of days to keep prompt history.");

        return TimeSpan.FromDays(days);
    }

    /// <summary>One pass: purge every partition's files older than the window. Returns files removed.</summary>
    public int Sweep()
    {
        var cutoffUtc = DateTime.UtcNow - _retention;
        var deleted = _log.PurgeOlderThan(cutoffUtc);
        if (deleted > 0)
            FileLog.Write($"[PromptLogRetentionSweep] purged {deleted} daily files older than {cutoffUtc:O}");
        return deleted;
    }
}
