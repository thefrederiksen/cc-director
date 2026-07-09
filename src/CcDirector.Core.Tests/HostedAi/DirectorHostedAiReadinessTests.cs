using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Core.Tests.HostedAi;

/// <summary>
/// Issue #940 (epic #937): the Director/desktop readiness helper. It gathers the mode locally and the
/// balance over HTTP, then defers to the shared
/// <see cref="HostedAiReadiness"/> - so the desktop resolves the identical state the Gateway does. These
/// prove it consults only the input the mode needs and maps each combination correctly, including the
/// add-credits-without-restart flow.
/// </summary>
public sealed class DirectorHostedAiReadinessTests
{
    private static DirectorHostedAiReadiness Build(
        TranscriptionMode mode,
        string? key = null,
        long? balanceMicros = null,
        Action? onKeyFetch = null,
        Action? onBalanceFetch = null)
        => new(
            () => mode,
            _ => { onKeyFetch?.Invoke(); return Task.FromResult(key); },
            _ => { onBalanceFetch?.Invoke(); return Task.FromResult(balanceMicros); });

    [Fact]
    public async Task LegacyByo_NoKey_UsesDevThrottleBalanceAndReadyWhenUnknown()
        => Assert.Equal(HostedAiState.Ready, await Build(TranscriptionMode.Byo, key: null).CheckAsync());

    [Fact]
    public async Task LegacyByo_WithKey_Ready()
        => Assert.Equal(HostedAiState.Ready, await Build(TranscriptionMode.Byo, key: "sk-abc").CheckAsync());

    [Fact]
    public async Task LegacyByo_FetchesBalance()
    {
        var balanceFetched = false;
        await Build(TranscriptionMode.Byo, key: "sk-abc", onBalanceFetch: () => balanceFetched = true).CheckAsync();
        Assert.True(balanceFetched);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task DevThrottle_EmptyBalance_NeedsCredits(long balance)
        => Assert.Equal(HostedAiState.NeedsCredits, await Build(TranscriptionMode.DevThrottle, balanceMicros: balance).CheckAsync());

    [Fact]
    public async Task DevThrottle_PositiveBalance_Ready()
        => Assert.Equal(HostedAiState.Ready, await Build(TranscriptionMode.DevThrottle, balanceMicros: 5_000_000).CheckAsync());

    [Fact]
    public async Task DevThrottle_UnknownBalance_DoesNotBlock_Ready()
        => Assert.Equal(HostedAiState.Ready, await Build(TranscriptionMode.DevThrottle, balanceMicros: null).CheckAsync());

    [Fact]
    public async Task DevThrottle_DoesNotFetchKey()
    {
        var keyFetched = false;
        await Build(TranscriptionMode.DevThrottle, balanceMicros: 5_000_000, onKeyFetch: () => keyFetched = true).CheckAsync();
        Assert.False(keyFetched);
    }

    [Fact]
    public async Task DevThrottle_AddCredits_UnlocksWithoutRestart()
    {
        long balance = 0;
        var check = new DirectorHostedAiReadiness(
            () => TranscriptionMode.DevThrottle,
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<long?>(balance));

        Assert.Equal(HostedAiState.NeedsCredits, await check.CheckAsync());
        balance = 5_000_000; // user adds $5
        Assert.Equal(HostedAiState.Ready, await check.CheckAsync());
    }

    [Fact]
    public void Constructor_NullDelegates_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new DirectorHostedAiReadiness(null!, _ => Task.FromResult<string?>(null), _ => Task.FromResult<long?>(0)));
        Assert.Throws<ArgumentNullException>(() => new DirectorHostedAiReadiness(() => TranscriptionMode.Byo, null!, _ => Task.FromResult<long?>(0)));
        Assert.Throws<ArgumentNullException>(() => new DirectorHostedAiReadiness(() => TranscriptionMode.Byo, _ => Task.FromResult<string?>(null), null!));
    }
}
