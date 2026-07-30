namespace CcDirector.Gateway.Supervision;

/// <summary>
/// The session supervisor's knobs (issue #915), resolved per tenant with the documented defaults below. The
/// engine reads these ONCE per recovery episode, so a change takes effect on the next fault rather than
/// halfway through a wait.
///
/// Default ON. That is the product decision recorded on the issue: unattended runs surviving a network blip
/// is the whole promise, so it is the out-of-the-box behaviour and an account opts OUT.
/// </summary>
public sealed record SupervisorSettings
{
    /// <summary>The master switch. Default on.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The FIRST wait, before the first "continue" - long enough for a name-resolution blip to
    /// clear, short enough that a night is not lost. Default 45 seconds.</summary>
    public TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(DefaultFirstRetrySeconds);

    /// <summary>The long cadence used from the second attempt on. Default 15 minutes.</summary>
    public TimeSpan RetryCadence { get; init; } = TimeSpan.FromMinutes(DefaultRetryCadenceMinutes);

    /// <summary>How many LONG-cadence attempts follow the first short one before the supervisor escalates
    /// instead of retrying forever. Default 8 - roughly two hours of trying.</summary>
    public int MaxLongRetries { get; init; } = DefaultMaxLongRetries;

    /// <summary>
    /// Whether step 3 - the model fallback - may look at an unrecognized terminating error. Default on, and
    /// independently switchable because it is the ONLY tier that sends terminal text off the machine.
    /// </summary>
    public bool ModelFallbackEnabled { get; init; } = true;

    public const int DefaultFirstRetrySeconds = 45;
    public const int DefaultRetryCadenceMinutes = 15;
    public const int DefaultMaxLongRetries = 8;

    /// <summary>The shipped defaults, as one value.</summary>
    public static readonly SupervisorSettings Defaults = new();

    // ---- validation bounds ------------------------------------------------------------------------------
    // A stored override outside these bounds is not honoured: a zero-second first retry would hammer a
    // session, and a zero ceiling with a one-minute cadence would be the infinite blind loop the issue
    // explicitly forbids. Out-of-bounds degrades to the documented default, never to "no limit".

    public const int MinFirstRetrySeconds = 5;
    public const int MaxFirstRetrySeconds = 600;
    public const int MinRetryCadenceMinutes = 1;
    public const int MaxRetryCadenceMinutes = 120;
    public const int MinLongRetries = 0;
    public const int MaxLongRetriesAllowed = 48;

    /// <summary>True when a first-retry override is usable.</summary>
    public static bool IsValidFirstRetrySeconds(int seconds)
        => seconds >= MinFirstRetrySeconds && seconds <= MaxFirstRetrySeconds;

    /// <summary>True when a cadence override is usable.</summary>
    public static bool IsValidRetryCadenceMinutes(int minutes)
        => minutes >= MinRetryCadenceMinutes && minutes <= MaxRetryCadenceMinutes;

    /// <summary>True when a ceiling override is usable. Zero is legal and means "the short retry only".</summary>
    public static bool IsValidMaxLongRetries(int retries)
        => retries >= MinLongRetries && retries <= MaxLongRetriesAllowed;
}
