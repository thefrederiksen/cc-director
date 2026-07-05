using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Core.Tests.HostedAi;

/// <summary>
/// Issue #938 (epic #937): the shared pre-flight readiness check. Every (mode, key, balance)
/// combination must resolve to the one correct <see cref="HostedAiState"/>, an unknown balance must
/// NOT block, and - the criterion the whole gate exists for - adding $5 must flip a check from
/// <see cref="HostedAiState.NeedsCredits"/> to <see cref="HostedAiState.Ready"/> with no restart,
/// proving the balance is re-read fresh on every call (no cache).
/// </summary>
public sealed class HostedAiReadinessTests
{
    private static HostedAiReadiness Build(
        TranscriptionMode mode, string? key = null, long? balanceMicros = null)
        => new(
            () => mode,
            _ => key,
            _ => Task.FromResult(balanceMicros));

    [Fact]
    public async Task Byo_NoKey_NeedsKey()
    {
        var state = await Build(TranscriptionMode.Byo, key: null).CheckAsync();
        Assert.Equal(HostedAiState.NeedsKey, state);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Byo_BlankKey_NeedsKey(string key)
    {
        var state = await Build(TranscriptionMode.Byo, key: key).CheckAsync();
        Assert.Equal(HostedAiState.NeedsKey, state);
    }

    [Fact]
    public async Task Byo_WithKey_Ready()
    {
        var state = await Build(TranscriptionMode.Byo, key: "sk-abc123").CheckAsync();
        Assert.Equal(HostedAiState.Ready, state);
    }

    [Fact]
    public async Task Byo_NeverReadsBalance()
    {
        var balanceRead = false;
        var check = new HostedAiReadiness(
            () => TranscriptionMode.Byo,
            _ => "sk-abc123",
            _ => { balanceRead = true; return Task.FromResult<long?>(0); });

        await check.CheckAsync();

        Assert.False(balanceRead); // BYO is a local key read only - no cloud call
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-500_000L)]
    public async Task DevThrottle_EmptyOrNegativeBalance_NeedsCredits(long balance)
    {
        var state = await Build(TranscriptionMode.DevThrottle, balanceMicros: balance).CheckAsync();
        Assert.Equal(HostedAiState.NeedsCredits, state);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(5_000_000L)]
    public async Task DevThrottle_PositiveBalance_Ready(long balance)
    {
        var state = await Build(TranscriptionMode.DevThrottle, balanceMicros: balance).CheckAsync();
        Assert.Equal(HostedAiState.Ready, state);
    }

    [Fact]
    public async Task DevThrottle_UnknownBalance_DoesNotBlock_Ready()
    {
        // Signed out or the cloud is unreachable -> balance null -> the pre-flight check must not block;
        // the runtime 402 is the authoritative gate and reports the identical state.
        var state = await Build(TranscriptionMode.DevThrottle, balanceMicros: null).CheckAsync();
        Assert.Equal(HostedAiState.Ready, state);
    }

    [Fact]
    public async Task DevThrottle_AddCredits_UnlocksWithoutRestart()
    {
        // The balance the check reads changes underneath the SAME instance (a $5 top-up). Because the
        // balance is re-read fresh each call, the very next check flips NeedsCredits -> Ready with no
        // restart and no new object - the epic's headline acceptance criterion.
        long balance = 0;
        var check = new HostedAiReadiness(
            () => TranscriptionMode.DevThrottle,
            _ => null,
            _ => Task.FromResult<long?>(balance));

        Assert.Equal(HostedAiState.NeedsCredits, await check.CheckAsync());

        balance = 5_000_000; // user adds $5

        Assert.Equal(HostedAiState.Ready, await check.CheckAsync());
    }

    [Fact]
    public async Task DevThrottle_ReadsBalanceFreshEveryCall()
    {
        var reads = 0;
        var check = new HostedAiReadiness(
            () => TranscriptionMode.DevThrottle,
            _ => null,
            _ => { reads++; return Task.FromResult<long?>(5_000_000); });

        await check.CheckAsync();
        await check.CheckAsync();
        await check.CheckAsync();

        Assert.Equal(3, reads); // no caching between calls
    }

    [Fact]
    public void Constructor_NullDelegates_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new HostedAiReadiness(null!, _ => null, _ => Task.FromResult<long?>(0)));
        Assert.Throws<ArgumentNullException>(() => new HostedAiReadiness(() => TranscriptionMode.Byo, null!, _ => Task.FromResult<long?>(0)));
        Assert.Throws<ArgumentNullException>(() => new HostedAiReadiness(() => TranscriptionMode.Byo, _ => null, null!));
    }
}
