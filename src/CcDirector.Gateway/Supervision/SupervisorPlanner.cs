using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Supervision;

/// <summary>What the supervisor does next with a classified fault.</summary>
public enum SupervisorActionKind
{
    /// <summary>Leave the session alone. The answer for a clean turn end - and the majority answer.</summary>
    DoNothing,

    /// <summary>Wait <see cref="SupervisorAction.Delay"/>, then re-send "continue" (subject to the engine's
    /// own pre-send checks: still idle, no menu on the screen).</summary>
    WaitThenContinue,

    /// <summary>Touch nothing and raise a hand: the recovery log record, the loud process log line, and the
    /// owner email.</summary>
    Escalate,
}

/// <summary>One decision: what to do, how long to wait first, and the closed-vocabulary cause it is filed
/// under in the recovery log.</summary>
public sealed record SupervisorAction(SupervisorActionKind Kind, TimeSpan Delay, string Cause, string Detail);

/// <summary>
/// The recovery state machine (issue #915), pure so the whole escalation ladder is testable without waiting
/// real minutes. Given the fault class, which attempt is next, and the tenant's settings, it returns the one
/// action to take.
///
/// The ladder for a transient transport fault is exactly the shape the owner asked for: attempt 1 waits the
/// short first retry, every attempt after it waits the long cadence, and once the ceiling is passed it
/// escalates instead of retrying forever.
/// </summary>
public static class SupervisorPlanner
{
    /// <summary>A rate-limited backoff never grows past this, however many attempts it takes.</summary>
    public static readonly TimeSpan MaxRateLimitedDelay = TimeSpan.FromMinutes(60);

    /// <summary>
    /// The next action. <paramref name="attempt"/> is 1-based and counts SENDS within one episode: attempt 1
    /// is the first "continue" after the short wait, attempt 2 the first long-cadence one, and so on.
    /// </summary>
    public static SupervisorAction Next(SessionFaultClass fault, int attempt, SupervisorSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempts are 1-based.");

        switch (fault)
        {
            case SessionFaultClass.None:
                return new SupervisorAction(SupervisorActionKind.DoNothing, TimeSpan.Zero, ActivityCauses.Unknown,
                    "the turn ended cleanly - nothing to recover");

            case SessionFaultClass.NonRecoverable:
                return new SupervisorAction(SupervisorActionKind.Escalate, TimeSpan.Zero, ActivityCauses.NonRecoverable,
                    "the work itself cannot proceed, so continuing it would change nothing");

            case SessionFaultClass.ContextFull:
                // Phase 1 deliberately does not send here: a session whose context is full swallows prompts,
                // so a "continue" would be silently eaten. Compact-then-continue is phase 2 (#1403).
                return new SupervisorAction(SupervisorActionKind.Escalate, TimeSpan.Zero, ActivityCauses.ContextFull,
                    "the context window is full - recovering it needs a compaction first (phase 2)");

            case SessionFaultClass.Unclassified:
                return new SupervisorAction(SupervisorActionKind.Escalate, TimeSpan.Zero, ActivityCauses.UnclassifiedFault,
                    "the turn ended on a fault nothing here recognizes");

            case SessionFaultClass.TransientTransport:
            case SessionFaultClass.RateLimited:
                if (attempt > 1 + settings.MaxLongRetries)
                {
                    return new SupervisorAction(SupervisorActionKind.Escalate, TimeSpan.Zero, ActivityCauses.RetryCeiling,
                        $"gave up after {settings.MaxLongRetries} long retries - this is an outage, not a blip");
                }
                var delay = fault == SessionFaultClass.TransientTransport
                    ? TransientDelay(attempt, settings)
                    : RateLimitedDelay(attempt, settings);
                var cause = fault == SessionFaultClass.TransientTransport
                    ? ActivityCauses.TransientTransport
                    : ActivityCauses.RateLimited;
                return new SupervisorAction(SupervisorActionKind.WaitThenContinue, delay, cause,
                    $"attempt {attempt} of {1 + settings.MaxLongRetries}, waiting {Describe(delay)}");

            default:
                throw new ArgumentOutOfRangeException(nameof(fault), fault, "Unknown fault class");
        }
    }

    /// <summary>The short wait once, then the long cadence for every attempt after it.</summary>
    private static TimeSpan TransientDelay(int attempt, SupervisorSettings settings)
        => attempt == 1 ? settings.FirstRetry : settings.RetryCadence;

    /// <summary>
    /// Rate limiting starts at the long cadence and doubles, capped at <see cref="MaxRateLimitedDelay"/>: a
    /// throttled provider is not a blip, and hammering it 45 seconds later earns another refusal.
    ///
    /// It does NOT honour a retry-after value, because there is none to honour here: the supervisor reads a
    /// terminal screen, not an HTTP response, so the header the provider sent never reaches this code. A
    /// number invented from screen text would be a fabricated measurement, so the backoff is the honest
    /// answer instead.
    /// </summary>
    private static TimeSpan RateLimitedDelay(int attempt, SupervisorSettings settings)
    {
        var doublings = Math.Min(attempt - 1, 8);           // 2^8 is already far past the cap
        var scaled = settings.RetryCadence * Math.Pow(2, doublings);
        return scaled > MaxRateLimitedDelay ? MaxRateLimitedDelay : scaled;
    }

    /// <summary>A plain-English delay for the log and the recovery record.</summary>
    public static string Describe(TimeSpan delay)
        => delay < TimeSpan.FromMinutes(1)
            ? $"{delay.TotalSeconds:0} seconds"
            : $"{delay.TotalMinutes:0} minutes";
}
