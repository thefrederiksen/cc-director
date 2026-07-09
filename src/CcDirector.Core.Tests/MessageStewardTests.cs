using CcDirector.Core.Configuration;
using CcDirector.Core.Fleet;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The fleet-message steward (flag: messaging.steward): dedupe + per-source rate limit + broadcast
/// throttle on a session's outgoing fleet messages. Driven by an injected clock so the sliding windows are
/// deterministic. Sources/targets are opaque keys, so plain strings stand in for session GUIDs.
/// </summary>
public sealed class MessageStewardTests
{
    private DateTime _now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    private MessageSteward New(MessageStewardOptions? opts = null) => new(opts ?? new MessageStewardOptions(), () => _now);

    [Fact]
    public void Allows_a_first_message()
    {
        var d = New().CheckMessage("src-A", "tgt-1", "hello");
        Assert.True(d.Allowed);
        Assert.Equal(StewardOutcome.Allowed, d.Outcome);
    }

    [Fact]
    public void Dedupe_suppresses_an_exact_duplicate_within_the_window()
    {
        var s = New(new MessageStewardOptions { DedupeWindowMs = 3000 });
        Assert.True(s.CheckMessage("src-A", "tgt-1", "hello").Allowed);

        _now = _now.AddMilliseconds(500);
        var dup = s.CheckMessage("src-A", "tgt-1", "hello");

        Assert.False(dup.Allowed);
        Assert.Equal(StewardOutcome.DuplicateSuppressed, dup.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(dup.Reason)); // never silent
    }

    [Fact]
    public void Dedupe_allows_the_same_text_after_the_window()
    {
        var s = New(new MessageStewardOptions { DedupeWindowMs = 3000 });
        Assert.True(s.CheckMessage("src-A", "tgt-1", "hello").Allowed);

        _now = _now.AddMilliseconds(3001);
        Assert.True(s.CheckMessage("src-A", "tgt-1", "hello").Allowed);
    }

    [Fact]
    public void Dedupe_window_slides_so_a_continuous_retry_loop_stays_suppressed()
    {
        var s = New(new MessageStewardOptions { DedupeWindowMs = 3000 });
        Assert.True(s.CheckMessage("src-A", "tgt-1", "loop").Allowed);

        // Fire every 1s (< window) repeatedly: each stays suppressed because the window slides on each repeat.
        for (var i = 0; i < 10; i++)
        {
            _now = _now.AddMilliseconds(1000);
            Assert.Equal(StewardOutcome.DuplicateSuppressed, s.CheckMessage("src-A", "tgt-1", "loop").Outcome);
        }

        // Only after a gap of at least the window does it deliver again.
        _now = _now.AddMilliseconds(3001);
        Assert.True(s.CheckMessage("src-A", "tgt-1", "loop").Allowed);
    }

    [Fact]
    public void Dedupe_distinguishes_text_target_and_source()
    {
        var s = New();
        Assert.True(s.CheckMessage("src-A", "tgt-1", "hello").Allowed);
        Assert.True(s.CheckMessage("src-A", "tgt-1", "world").Allowed); // different text
        Assert.True(s.CheckMessage("src-A", "tgt-2", "hello").Allowed); // different target
        Assert.True(s.CheckMessage("src-B", "tgt-1", "hello").Allowed); // different source
    }

    [Fact]
    public void RateLimit_trips_on_a_flood_and_passes_normal_traffic()
    {
        var s = New(new MessageStewardOptions { PerSourcePerMin = 5 });

        for (var i = 0; i < 5; i++)
        {
            _now = _now.AddMilliseconds(10);
            Assert.True(s.CheckMessage("src-A", "tgt-1", $"m{i}").Allowed); // distinct texts, so dedupe never fires
        }

        _now = _now.AddMilliseconds(10);
        var over = s.CheckMessage("src-A", "tgt-1", "m5");
        Assert.False(over.Allowed);
        Assert.Equal(StewardOutcome.RateLimited, over.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(over.Reason));

        // A DIFFERENT source under the cap is unaffected - normal traffic passes.
        Assert.True(s.CheckMessage("src-B", "tgt-1", "hi").Allowed);
    }

    [Fact]
    public void RateLimit_window_slides_so_traffic_resumes_after_a_minute()
    {
        var s = New(new MessageStewardOptions { PerSourcePerMin = 3 });
        for (var i = 0; i < 3; i++)
        {
            _now = _now.AddMilliseconds(10);
            Assert.True(s.CheckMessage("src-A", "tgt-1", $"m{i}").Allowed);
        }
        _now = _now.AddMilliseconds(10);
        Assert.Equal(StewardOutcome.RateLimited, s.CheckMessage("src-A", "tgt-1", "over").Outcome);

        _now = _now.AddSeconds(61); // the rolling window rolls past the earlier sends
        Assert.True(s.CheckMessage("src-A", "tgt-1", "after").Allowed);
    }

    [Fact]
    public void Broadcast_throttle_is_independent_of_the_per_target_rate_limit()
    {
        var s = New(new MessageStewardOptions { BroadcastsPerMin = 2, PerSourcePerMin = 100 });

        _now = _now.AddMilliseconds(10); Assert.True(s.CheckBroadcast("src-A", "b0").Allowed);
        _now = _now.AddMilliseconds(10); Assert.True(s.CheckBroadcast("src-A", "b1").Allowed);
        _now = _now.AddMilliseconds(10);
        var over = s.CheckBroadcast("src-A", "b2");

        Assert.False(over.Allowed);
        Assert.Equal(StewardOutcome.BroadcastThrottled, over.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(over.Reason));

        // Per-target sends are NOT consumed by the broadcast throttle.
        Assert.True(s.CheckMessage("src-A", "tgt-1", "still-ok").Allowed);
    }

    [Fact]
    public void Disabled_steward_allows_everything_byte_identical()
    {
        var s = New(new MessageStewardOptions { Enabled = false, PerSourcePerMin = 1, BroadcastsPerMin = 1, DedupeWindowMs = 100_000 });
        Assert.False(s.Enabled);

        // The same message far over every cap - all allowed because the steward is off.
        for (var i = 0; i < 20; i++)
            Assert.True(s.CheckMessage("src-A", "tgt-1", "dup").Allowed);
        for (var i = 0; i < 20; i++)
            Assert.True(s.CheckBroadcast("src-A", "b").Allowed);
    }

    [Fact]
    public void No_source_is_allowed_generic_framing()
    {
        var s = New();
        Assert.True(s.CheckMessage(null, "tgt-1", "hi").Allowed);
        Assert.True(s.CheckMessage("", "tgt-1", "hi").Allowed);
        Assert.True(s.CheckBroadcast(null, "hi").Allowed);
    }

    [Fact]
    public void NonPositive_caps_disable_that_limit_but_dedupe_still_works()
    {
        var s = New(new MessageStewardOptions { PerSourcePerMin = 0, BroadcastsPerMin = 0, DedupeWindowMs = 3000 });

        // Rate limit disabled: 50 distinct messages all pass.
        for (var i = 0; i < 50; i++)
        {
            _now = _now.AddMilliseconds(10);
            Assert.True(s.CheckMessage("src-A", "tgt-1", $"m{i}").Allowed);
        }

        // Dedupe is still enforced.
        Assert.True(s.CheckMessage("src-A", "tgt-1", "same").Allowed);
        Assert.Equal(StewardOutcome.DuplicateSuppressed, s.CheckMessage("src-A", "tgt-1", "same").Outcome);
    }
}
