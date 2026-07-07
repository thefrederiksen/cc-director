using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Core.Tests.HostedAi;

/// <summary>
/// Issue #938 (epic #937): the shared pre-flight readiness check. All AI is DevThrottle-hosted, so
/// readiness is purely the account balance: a known balance at or below zero is
/// <see cref="HostedAiState.NeedsCredits"/>, anything else (including an unknown balance) is
/// <see cref="HostedAiState.Ready"/>. An unknown balance must NOT block, and - the criterion the whole
/// gate exists for - adding $5 must flip a check from NeedsCredits to Ready with no restart, proving the
/// balance is re-read fresh on every call (no cache).
/// </summary>
public sealed class HostedAiReadinessTests
{
    private static HostedAiReadiness Build(long? balanceMicros = null)
        => new(_ => Task.FromResult(balanceMicros));

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-500_000L)]
    public async Task EmptyOrNegativeBalance_NeedsCredits(long balance)
    {
        var state = await Build(balanceMicros: balance).CheckAsync();
        Assert.Equal(HostedAiState.NeedsCredits, state);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(5_000_000L)]
    public async Task PositiveBalance_Ready(long balance)
    {
        var state = await Build(balanceMicros: balance).CheckAsync();
        Assert.Equal(HostedAiState.Ready, state);
    }

    [Fact]
    public async Task UnknownBalance_DoesNotBlock_Ready()
    {
        // Signed out or the cloud is unreachable -> balance null -> the pre-flight check must not block;
        // the runtime 402 is the authoritative gate and reports the identical state.
        var state = await Build(balanceMicros: null).CheckAsync();
        Assert.Equal(HostedAiState.Ready, state);
    }

    [Fact]
    public async Task AddCredits_UnlocksWithoutRestart()
    {
        // The balance the check reads changes underneath the SAME instance (a $5 top-up). Because the
        // balance is re-read fresh each call, the very next check flips NeedsCredits -> Ready with no
        // restart and no new object - the epic's headline acceptance criterion.
        long balance = 0;
        var check = new HostedAiReadiness(_ => Task.FromResult<long?>(balance));

        Assert.Equal(HostedAiState.NeedsCredits, await check.CheckAsync());

        balance = 5_000_000; // user adds $5

        Assert.Equal(HostedAiState.Ready, await check.CheckAsync());
    }

    [Fact]
    public async Task ReadsBalanceFreshEveryCall()
    {
        var reads = 0;
        var check = new HostedAiReadiness(_ => { reads++; return Task.FromResult<long?>(5_000_000); });

        await check.CheckAsync();
        await check.CheckAsync();
        await check.CheckAsync();

        Assert.Equal(3, reads); // no caching between calls
    }

    [Fact]
    public void Constructor_NullDelegate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new HostedAiReadiness(null!));
    }
}
