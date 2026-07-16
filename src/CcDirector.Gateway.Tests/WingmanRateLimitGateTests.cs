using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The shared wingman rate-limit cooldown (issue #1324): after a 429 the gate blocks further model
/// calls until the backoff elapses - honoring the provider's Retry-After, else an exponential ramp
/// capped to a ceiling - and a success resets it. A fake clock makes the timing deterministic.
/// </summary>
public sealed class WingmanRateLimitGateTests
{
    /// <summary>A hand-cranked clock so cooldown expiry is tested without any real waiting.</summary>
    private sealed class FakeClock
    {
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime NowUtc() => Now;
    }

    [Fact]
    public void FreshGate_IsNotInCooldown()
    {
        var gate = new WingmanRateLimitGate();
        Assert.False(gate.InCooldown(out var remaining));
        Assert.Equal(TimeSpan.Zero, remaining);
    }

    [Fact]
    public void OnRateLimited_NoRetryAfter_ArmsBaseCooldown()
    {
        var clock = new FakeClock();
        var gate = new WingmanRateLimitGate(clock.NowUtc, baseDelay: TimeSpan.FromSeconds(5), maxDelay: TimeSpan.FromSeconds(120));

        var backoff = gate.OnRateLimited(null);

        Assert.Equal(TimeSpan.FromSeconds(5), backoff);
        Assert.True(gate.InCooldown(out var remaining));
        Assert.Equal(TimeSpan.FromSeconds(5), remaining);
    }

    [Fact]
    public void Cooldown_ExpiresAfterTheDelay()
    {
        var clock = new FakeClock();
        var gate = new WingmanRateLimitGate(clock.NowUtc, baseDelay: TimeSpan.FromSeconds(5));
        gate.OnRateLimited(null);

        clock.Now = clock.Now.AddSeconds(4);
        Assert.True(gate.InCooldown(out _));       // still inside the window

        clock.Now = clock.Now.AddSeconds(1);       // now exactly at the boundary
        Assert.False(gate.InCooldown(out _));       // elapsed
    }

    [Fact]
    public void ConsecutiveRateLimits_BackOffExponentially_UpToCap()
    {
        var clock = new FakeClock();
        var gate = new WingmanRateLimitGate(clock.NowUtc, baseDelay: TimeSpan.FromSeconds(5), maxDelay: TimeSpan.FromSeconds(120));

        Assert.Equal(TimeSpan.FromSeconds(5), gate.OnRateLimited(null));    // 5 * 2^0
        Assert.Equal(TimeSpan.FromSeconds(10), gate.OnRateLimited(null));   // 5 * 2^1
        Assert.Equal(TimeSpan.FromSeconds(20), gate.OnRateLimited(null));   // 5 * 2^2
        Assert.Equal(TimeSpan.FromSeconds(40), gate.OnRateLimited(null));   // 5 * 2^3
        Assert.Equal(TimeSpan.FromSeconds(80), gate.OnRateLimited(null));   // 5 * 2^4
        Assert.Equal(TimeSpan.FromSeconds(120), gate.OnRateLimited(null));  // 5 * 2^5 = 160 -> capped
        Assert.Equal(TimeSpan.FromSeconds(120), gate.OnRateLimited(null));  // stays capped
    }

    [Fact]
    public void OnRateLimited_HonorsProviderRetryAfter()
    {
        var clock = new FakeClock();
        var gate = new WingmanRateLimitGate(clock.NowUtc, baseDelay: TimeSpan.FromSeconds(5), maxDelay: TimeSpan.FromSeconds(120));

        var backoff = gate.OnRateLimited(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), backoff);   // the provider's hint, not the 5s base
        Assert.True(gate.InCooldown(out var remaining));
        Assert.Equal(TimeSpan.FromSeconds(30), remaining);
    }

    [Fact]
    public void RetryAfter_AboveCeiling_IsCapped()
    {
        var gate = new WingmanRateLimitGate(maxDelay: TimeSpan.FromSeconds(120));
        Assert.Equal(TimeSpan.FromSeconds(120), gate.OnRateLimited(TimeSpan.FromSeconds(600)));
    }

    [Fact]
    public void ANearerCooldown_DoesNotShortenALongerOneAlreadyArmed()
    {
        var clock = new FakeClock();
        var gate = new WingmanRateLimitGate(clock.NowUtc, baseDelay: TimeSpan.FromSeconds(5));

        gate.OnRateLimited(TimeSpan.FromSeconds(60));   // arm 60s
        gate.OnRateLimited(TimeSpan.FromSeconds(5));     // a nearer 5s must not pull the cooldown in

        Assert.True(gate.InCooldown(out var remaining));
        Assert.Equal(TimeSpan.FromSeconds(60), remaining);
    }

    [Fact]
    public void OnSuccess_ClearsCooldownAndResetsTheRamp()
    {
        var clock = new FakeClock();
        var gate = new WingmanRateLimitGate(clock.NowUtc, baseDelay: TimeSpan.FromSeconds(5));
        gate.OnRateLimited(null);
        gate.OnRateLimited(null);    // ramp now at 10s

        gate.OnSuccess();

        Assert.False(gate.InCooldown(out _));
        // The ramp is back to base: the next 429 is a 5s backoff again, not a continued 20s.
        Assert.Equal(TimeSpan.FromSeconds(5), gate.OnRateLimited(null));
    }

    // ---- The half-open probe (2026-07-15). THE reason an outage used to be permanent. ----
    //
    // The gate blocked every call for the whole cooldown. So while gated, no call could succeed, so
    // nothing could clear the cooldown, so the only exit was the clock - which released the entire
    // fleet at a provider that had gone cold precisely BECAUSE of the gate's silence. They all timed
    // out and re-armed it. The fleet sat at 0/8 sessions with audio while the provider answered every
    // hand-made call perfectly.
    //
    // A closed gate must still send exactly one call: it is the only way to learn the service came
    // back, and it keeps the provider's model warm so the fleet returns to a warm one.

    [Fact]
    public void WhileGated_ExactlyOneCallerProbes_AndTheRestAreHeld()
    {
        var clock = new FakeClock();
        var gate = new WingmanRateLimitGate(clock.NowUtc);
        gate.OnRateLimited(null);

        // First caller in becomes the probe.
        Assert.True(gate.TryEnter(out var isProbe, out var left));
        Assert.True(isProbe);
        Assert.True(left > TimeSpan.Zero);

        // Everyone else is held back - that is the backpressure the 429 actually asked for.
        Assert.False(gate.TryEnter(out var second, out _));
        Assert.False(second);
        Assert.False(gate.TryEnter(out _, out _));
    }

    [Fact]
    public void AProbeThatSucceeds_EndsTheOutageImmediately()
    {
        var clock = new FakeClock();
        var gate = new WingmanRateLimitGate(clock.NowUtc);
        gate.OnRateLimited(null);
        Assert.True(gate.TryEnter(out var isProbe, out _));
        Assert.True(isProbe);

        // The provider is well again. This is the path that did not exist: the gate learns it, WITHOUT
        // waiting out the clock, because it kept one call flowing.
        gate.OnSuccess();

        Assert.False(gate.InCooldown(out _));
        Assert.True(gate.TryEnter(out var nowProbe, out _));
        Assert.False(nowProbe);   // not a probe any more - the gate is simply open
    }

    [Fact]
    public void AProbeThatIsStillRateLimited_ExtendsTheCooldown_AndLetsTheNextCallerProbe()
    {
        var clock = new FakeClock();
        var gate = new WingmanRateLimitGate(clock.NowUtc);
        gate.OnRateLimited(null);
        Assert.True(gate.TryEnter(out _, out _));

        // The probe came back 429. The cooldown extends - and crucially the slot is released, or the
        // gate would wedge half-open: one probe out forever, every other session gated forever.
        gate.OnRateLimited(null);
        Assert.True(gate.InCooldown(out _));
        Assert.True(gate.TryEnter(out var nextProbe, out _));
        Assert.True(nextProbe);
    }

    [Fact]
    public void AProbeThatReportsNothing_ReleasesTheSlot_SoTheGateCannotWedgeHalfOpen()
    {
        var clock = new FakeClock();
        var gate = new WingmanRateLimitGate(clock.NowUtc);
        gate.OnRateLimited(null);
        Assert.True(gate.TryEnter(out _, out _));
        Assert.False(gate.TryEnter(out _, out _));   // held while the probe is out

        gate.EndProbe();   // the probe threw / gave up without a verdict

        Assert.True(gate.TryEnter(out var nextProbe, out _));
        Assert.True(nextProbe);
    }

    [Fact]
    public void AProbeThatVanishes_CannotGateTheFleetPastTheCooldown()
    {
        var clock = new FakeClock();
        var gate = new WingmanRateLimitGate(clock.NowUtc);
        gate.OnRateLimited(null);
        Assert.True(gate.TryEnter(out _, out _));   // probe out, never reports back

        // Belt and braces: even if a probe is somehow never released, a lapsed cooldown must open the
        // gate to everyone. A leaked probe slot must never outlive the outage it was probing.
        clock.Now = clock.Now.AddSeconds(10);
        Assert.True(gate.TryEnter(out var isProbe, out var left));
        Assert.False(isProbe);
        Assert.Equal(TimeSpan.Zero, left);
    }
}
