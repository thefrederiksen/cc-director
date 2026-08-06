using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Core.Tests.HostedAi;

/// <summary>
/// Issue #940 (epic #937), rewritten by the Included AI mission (issue #1360): the Director/desktop
/// readiness helper defers to the shared <see cref="HostedAiReadiness"/>, which no longer consults
/// the balance - so the desktop makes NO pre-dictation credit read and always resolves Ready in
/// DevThrottle mode. The runtime 402 is the only gate. The never-fetches tests are the desktop half
/// of the Q2 revert-proof.
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
    public async Task LegacyByo_Ready()
        => Assert.Equal(HostedAiState.Ready, await Build(TranscriptionMode.Byo, key: null).CheckAsync());

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task DevThrottle_ZeroBalance_StillReady(long balance)
        // Included AI revert-proof (issue #1360): the old gate answered NeedsCredits here and blocked
        // the zero-balance entitled member the mission's acceptance test serves.
        => Assert.Equal(HostedAiState.Ready, await Build(TranscriptionMode.DevThrottle, balanceMicros: balance).CheckAsync());

    [Fact]
    public async Task DevThrottle_PositiveBalance_Ready()
        => Assert.Equal(HostedAiState.Ready, await Build(TranscriptionMode.DevThrottle, balanceMicros: 5_000_000).CheckAsync());

    [Fact]
    public async Task DevThrottle_UnknownBalance_Ready()
        => Assert.Equal(HostedAiState.Ready, await Build(TranscriptionMode.DevThrottle, balanceMicros: null).CheckAsync());

    [Fact]
    public async Task NeverFetchesKeyOrBalance()
    {
        // No credit read and no key read on any check (issue #1360): the balance was the desktop's
        // 2-second pre-dictation HTTP fetch, now gone entirely.
        var keyFetched = false;
        var balanceFetched = false;
        await Build(TranscriptionMode.DevThrottle,
            onKeyFetch: () => keyFetched = true,
            onBalanceFetch: () => balanceFetched = true).CheckAsync();
        await Build(TranscriptionMode.Byo,
            onKeyFetch: () => keyFetched = true,
            onBalanceFetch: () => balanceFetched = true).CheckAsync();

        Assert.False(keyFetched);
        Assert.False(balanceFetched);
    }

    [Fact]
    public void Constructor_NullDelegates_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new DirectorHostedAiReadiness(null!, _ => Task.FromResult<string?>(null), _ => Task.FromResult<long?>(0)));
        Assert.Throws<ArgumentNullException>(() => new DirectorHostedAiReadiness(() => TranscriptionMode.Byo, null!, _ => Task.FromResult<long?>(0)));
        Assert.Throws<ArgumentNullException>(() => new DirectorHostedAiReadiness(() => TranscriptionMode.Byo, _ => Task.FromResult<string?>(null), null!));
    }

    [Fact]
    public async Task Create_WiresTheModeAndAnswersReady()
    {
        var readiness = DirectorHostedAiReadiness.Create(new HostedAiKeyResolver(), () => TranscriptionMode.DevThrottle);
        Assert.Equal(HostedAiState.Ready, await readiness.CheckAsync());
    }
}
