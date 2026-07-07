using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Core.Tests.HostedAi;

/// <summary>
/// Issue #940 (epic #937): the Director/desktop readiness helper. It gathers the balance over HTTP,
/// then defers to the shared <see cref="HostedAiReadiness"/> - so the desktop resolves the identical
/// state the Gateway does. These prove it maps each balance to the correct state, including the
/// add-credits-without-restart flow.
/// </summary>
public sealed class DirectorHostedAiReadinessTests
{
    private static DirectorHostedAiReadiness Build(
        long? balanceMicros = null,
        Action? onBalanceFetch = null)
        => new(_ => { onBalanceFetch?.Invoke(); return Task.FromResult(balanceMicros); });

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task EmptyBalance_NeedsCredits(long balance)
        => Assert.Equal(HostedAiState.NeedsCredits, await Build(balanceMicros: balance).CheckAsync());

    [Fact]
    public async Task PositiveBalance_Ready()
        => Assert.Equal(HostedAiState.Ready, await Build(balanceMicros: 5_000_000).CheckAsync());

    [Fact]
    public async Task UnknownBalance_DoesNotBlock_Ready()
        => Assert.Equal(HostedAiState.Ready, await Build(balanceMicros: null).CheckAsync());

    [Fact]
    public async Task AddCredits_UnlocksWithoutRestart()
    {
        long balance = 0;
        var check = new DirectorHostedAiReadiness(_ => Task.FromResult<long?>(balance));

        Assert.Equal(HostedAiState.NeedsCredits, await check.CheckAsync());
        balance = 5_000_000; // user adds $5
        Assert.Equal(HostedAiState.Ready, await check.CheckAsync());
    }

    [Fact]
    public void Constructor_NullDelegate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DirectorHostedAiReadiness(null!));
    }
}
