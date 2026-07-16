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
///
/// IT MUST LET ONE CALL THROUGH WHILE IT WAITS, AND THAT IS THE WHOLE POINT (2026-07-15).
///
/// This gate used to block EVERY call for the full cooldown. That is a latch, not backpressure, and it
/// converted a provider having a bad ten minutes into an outage that could not end:
///
///   1. The speech provider scales its model down when nobody calls it. A cold call takes 17-40s
///      (measured; a 16-character call took 39.9s) against a deadline that was tighter than that.
///   2. The call times out. The gate arms and the fleet goes silent for 120 seconds.
///   3. 120 seconds of NOBODY CALLING is exactly how the model goes cold. The gate's own silence
///      manufactured the condition that caused the timeout.
///   4. The cooldown lapses, every session rushes a cold model at once, they all time out, re-arm.
///
/// The fleet sat at 0/8 sessions with audio, all reporting "the voice service is not responding",
/// while the service answered every hand-made call perfectly. Three warm-up calls by hand took it to
/// 6/8 with NO code change. The provider was never the problem after the first minute; we were.
///
/// So a closed gate must still send ONE call - the half-open probe of a standard circuit breaker,
/// which here earns its keep twice over:
///
///   * it is the only way to LEARN the provider recovered (a gate that never calls never finds out -
///     that is the spiral, and it is why an outage did not simply end when the outage ended); and
///   * it KEEPS THE MODEL WARM, so when the fleet is let back in it meets a warm provider instead of
///     the cold start the old cooldown guaranteed.
///
/// The probe is not a synthetic ping. It is one real session's narration, chosen first-come, so a
/// probe that succeeds has done useful work and that session has its voice. One call is also exactly
/// what the 429 asked for: "slow down" - not "stop, and go cold, and come back all at once".
///
/// Thread-safe; the clock is injectable so the backoff is unit-testable.
/// </summary>
internal sealed class WingmanRateLimitGate
{
    private readonly Func<DateTime> _nowUtc;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly object _lock = new();
    private DateTime _cooldownUntilUtc = DateTime.MinValue;
    private int _consecutive;
    /// <summary>True while the single half-open probe is out. Guarded by <see cref="_lock"/>.</summary>
    private bool _probeInFlight;

    /// <param name="nowUtc">Clock source; <see cref="DateTime.UtcNow"/> when null (tests inject a fake).</param>
    /// <param name="baseDelay">The first backoff after a single 429 (default 5 seconds).</param>
    /// <param name="maxDelay">The ceiling the exponential ramp and any Retry-After are capped to (default 120 seconds).</param>
    public WingmanRateLimitGate(Func<DateTime>? nowUtc = null, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
    {
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(5);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(120);
    }

    /// <summary>True while a cooldown is in effect; <paramref name="remaining"/> is how long is left.
    /// Reports the raw cooldown state only - it does NOT decide whether a caller may proceed, because
    /// during a cooldown exactly one caller may (the probe). Use <see cref="TryEnter"/> to ask that.</summary>
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
    /// Ask to make a call. THE gate decision - callers must use this rather than reading
    /// <see cref="InCooldown"/> and skipping, which is the latch this class exists to not be.
    ///
    /// Outcomes:
    ///   * no cooldown -> true, <paramref name="isProbe"/> false. Normal running.
    ///   * cooldown, nobody probing -> true, <paramref name="isProbe"/> TRUE. This caller is the single
    ///     half-open probe: it both tests recovery and keeps the provider's model warm. It MUST report
    ///     back via <see cref="OnSuccess"/> or <see cref="OnRateLimited"/> (which release the probe), or
    ///     <see cref="EndProbe"/> if it did neither.
    ///   * cooldown, probe already out -> false. Held back, which is the backpressure the 429 asked for.
    ///
    /// <paramref name="remaining"/> is the cooldown left (zero when not gated), for the caller's log.
    /// </summary>
    public bool TryEnter(out bool isProbe, out TimeSpan remaining)
    {
        lock (_lock)
        {
            var left = _cooldownUntilUtc - _nowUtc();
            if (left <= TimeSpan.Zero)
            {
                // Not gated. A lapsed cooldown also means any probe bookkeeping is moot - clear it so a
                // probe that never reported back cannot wedge the gate half-open forever.
                _probeInFlight = false;
                isProbe = false;
                remaining = TimeSpan.Zero;
                return true;
            }
            remaining = left;
            if (_probeInFlight) { isProbe = false; return false; }
            _probeInFlight = true;
            isProbe = true;
            return true;
        }
    }

    /// <summary>Release the probe slot without a verdict - the probe neither succeeded nor was rate
    /// limited (it threw, or the caller gave up). The cooldown stands; the next caller may probe.</summary>
    public void EndProbe()
    {
        lock (_lock) { _probeInFlight = false; }
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
            // A probe that came back rate limited has reported: release the slot so the NEXT caller can
            // probe once this longer cooldown lapses. Without this the gate wedges half-open - one probe
            // out forever, every other session gated forever - which is the same silence in a new hat.
            _probeInFlight = false;
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

    /// <summary>
    /// A call succeeded - clear the cooldown and reset the backoff ramp to base.
    ///
    /// This is how the outage ENDS, and before the probe existed there was no way to reach it while
    /// gated: the gate blocked every call, so no call could succeed, so nothing ever cleared the
    /// cooldown except it timing out into another cold stampede. A successful probe now re-opens the
    /// gate the moment the provider is well - and because the probe kept the model warm, the fleet
    /// comes back to a warm provider rather than the cold start that started all this.
    /// </summary>
    public void OnSuccess()
    {
        lock (_lock)
        {
            _probeInFlight = false;
            _consecutive = 0;
            _cooldownUntilUtc = DateTime.MinValue;
        }
    }
}
