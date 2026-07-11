namespace CcDirector.Gateway.Wingman;

/// <summary>
/// A shared, fleet-wide cooldown for the hosted wingman model call (issue #1324). When the provider
/// rate limits us with HTTP 429, calling it again immediately just earns another 429 - the storm that
/// blanked every voice session with "no narration is ready". This gate records a cooldown after a 429
/// and reports it so <see cref="WingmanVoiceService.GenerateAsync"/> skips the model call until it
/// elapses: it honors the provider's Retry-After when given, and otherwise backs off exponentially
/// (5s, 10s, 20s ... capped) across consecutive hits. A success resets the ramp.
///
/// There is ONE gate per Gateway because every voice session shares the one provider, so a single
/// cooldown calms the whole fleet at once. No per-caller jitter is needed here: jitter exists to
/// de-synchronize a HERD of independent clients, but this is a single caller, so a plain shared
/// cooldown is correct and simpler. Thread-safe; the clock is injectable so the backoff is unit-testable.
/// </summary>
internal sealed class WingmanRateLimitGate
{
    private readonly Func<DateTime> _nowUtc;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly object _lock = new();
    private DateTime _cooldownUntilUtc = DateTime.MinValue;
    private int _consecutive;

    /// <param name="nowUtc">Clock source; <see cref="DateTime.UtcNow"/> when null (tests inject a fake).</param>
    /// <param name="baseDelay">The first backoff after a single 429 (default 5 seconds).</param>
    /// <param name="maxDelay">The ceiling the exponential ramp and any Retry-After are capped to (default 120 seconds).</param>
    public WingmanRateLimitGate(Func<DateTime>? nowUtc = null, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
    {
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(5);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(120);
    }

    /// <summary>True while a cooldown is in effect; <paramref name="remaining"/> is how long is left.</summary>
    public bool InCooldown(out TimeSpan remaining)
    {
        lock (_lock)
        {
            var left = _cooldownUntilUtc - _nowUtc();
            if (left > TimeSpan.Zero) { remaining = left; return true; }
            remaining = TimeSpan.Zero;
            return false;
        }
    }

    /// <summary>
    /// Record a 429 and (re)arm the cooldown; returns the backoff applied. Honors
    /// <paramref name="retryAfter"/> when the provider sent one (capped to the ceiling); otherwise
    /// doubles from the base delay for each consecutive hit up to the ceiling. A nearer cooldown never
    /// shortens a longer one already armed.
    /// </summary>
    public TimeSpan OnRateLimited(TimeSpan? retryAfter)
    {
        lock (_lock)
        {
            if (_consecutive < int.MaxValue) _consecutive++;
            TimeSpan backoff;
            if (retryAfter is { } ra && ra > TimeSpan.Zero)
            {
                backoff = ra > _maxDelay ? _maxDelay : ra;
            }
            else
            {
                // 5 * 2^(n-1), capped. The exponent is clamped so the Pow never overflows on a long outage.
                var seconds = _baseDelay.TotalSeconds * Math.Pow(2, Math.Min(_consecutive - 1, 20));
                backoff = TimeSpan.FromSeconds(Math.Min(seconds, _maxDelay.TotalSeconds));
            }
            var until = _nowUtc() + backoff;
            if (until > _cooldownUntilUtc) _cooldownUntilUtc = until;
            return backoff;
        }
    }

    /// <summary>A model call succeeded - clear the cooldown and reset the backoff ramp to base.</summary>
    public void OnSuccess()
    {
        lock (_lock)
        {
            _consecutive = 0;
            _cooldownUntilUtc = DateTime.MinValue;
        }
    }
}
