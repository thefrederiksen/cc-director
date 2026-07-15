using CcDirector.ControlApi;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the desktop's cache of the Gateway-owned snooze lengths. The contract that matters is
/// what the right-click menu depends on: reading is instant, never throws, and never invents lengths.
/// </summary>
public sealed class SnoozeOptionsCacheTests
{
    /// <summary>A stand-in Gateway: answers with whatever the test sets, or throws when told to.</summary>
    private sealed class FakeHold : IGatewayHold
    {
        public SnoozeOptionsResponse? Answer { get; set; }
        public Exception? Throws { get; set; }
        public int Calls { get; private set; }

        public Task RecordHoldAsync(string sessionId, bool onHold, int? snoozeMinutes = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<SnoozeOptionsResponse?> GetSnoozeOptionsAsync(CancellationToken ct = default)
        {
            Calls++;
            if (Throws is not null) throw Throws;
            return Task.FromResult(Answer);
        }
    }

    private static SnoozeOptionsResponse Options(params int[] presets) =>
        new() { Presets = presets, DefaultMinutes = presets[0], MaxPresets = 5 };

    [Fact]
    public void Current_is_null_before_anything_has_been_fetched()
    {
        // Null is the honest answer: this desktop does not know the user's lengths yet. The menu then
        // offers only the plain Snooze rather than showing lengths that might not be the user's.
        var cache = new SnoozeOptionsCache(() => new FakeHold());
        Assert.Null(cache.Current);
    }

    [Fact]
    public async Task RefreshAsync_caches_what_the_Gateway_answered()
    {
        var hold = new FakeHold { Answer = Options(15, 60, 240, 480) };
        var cache = new SnoozeOptionsCache(() => hold);

        await cache.RefreshAsync();

        Assert.Equal(new[] { 15, 60, 240, 480 }, cache.Current!.Presets);
        Assert.Equal(15, cache.Current!.DefaultMinutes);
    }

    [Fact]
    public async Task RefreshAsync_keeps_the_last_known_lengths_when_the_Gateway_fails()
    {
        // An unreachable Gateway must not blank the menu or throw into a context-menu build. The cache is
        // the ONE place allowed to swallow that failure, and it keeps the last real answer.
        var hold = new FakeHold { Answer = Options(15, 60, 240, 480) };
        var cache = new SnoozeOptionsCache(() => hold);
        await cache.RefreshAsync();

        hold.Throws = new HttpRequestException("gateway is down");
        await cache.RefreshAsync();

        Assert.Equal(new[] { 15, 60, 240, 480 }, cache.Current!.Presets);
    }

    [Fact]
    public async Task RefreshAsync_does_not_throw_when_the_Gateway_fails_before_anything_was_cached()
    {
        var hold = new FakeHold { Throws = new HttpRequestException("gateway is down") };
        var cache = new SnoozeOptionsCache(() => hold);

        await cache.RefreshAsync();

        // Still null - it did not invent a list to cover the failure.
        Assert.Null(cache.Current);
    }

    [Fact]
    public async Task RefreshAsync_leaves_the_cache_null_when_no_Gateway_is_configured()
    {
        // A not-configured Gateway answers null rather than throwing; that is not an error, and it must
        // not become a made-up list either.
        var cache = new SnoozeOptionsCache(() => new FakeHold { Answer = null });

        await cache.RefreshAsync();

        Assert.Null(cache.Current);
    }

    [Fact]
    public async Task RefreshAsync_survives_having_no_Gateway_client_at_all()
    {
        // GatewayHold is null while the Gateway is unconfigured, and the cache reads it lazily.
        var cache = new SnoozeOptionsCache(() => null);

        await cache.RefreshAsync();

        Assert.Null(cache.Current);
    }

    [Fact]
    public async Task Current_serves_a_fresh_value_without_asking_the_Gateway_again()
    {
        // The menu is rebuilt on EVERY right-click. If each read hit the network the menu would be both
        // slow and chatty, which is the whole reason this cache exists.
        var hold = new FakeHold { Answer = Options(15, 60) };
        var cache = new SnoozeOptionsCache(() => hold);
        await cache.RefreshAsync();
        var callsAfterWarm = hold.Calls;

        for (var i = 0; i < 20; i++) _ = cache.Current;

        Assert.Equal(callsAfterWarm, hold.Calls);
    }

    [Fact]
    public void StaleAfter_is_short_enough_to_notice_a_Cockpit_edit_without_being_chatty()
    {
        Assert.InRange(SnoozeOptionsCache.StaleAfter, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15));
    }
}
