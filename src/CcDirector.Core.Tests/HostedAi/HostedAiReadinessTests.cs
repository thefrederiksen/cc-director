using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using Xunit;

namespace CcDirector.Core.Tests.HostedAi;

/// <summary>
/// Issue #938 (epic #937), rewritten by the Included AI mission (issue #1360): the shared pre-flight
/// readiness check no longer consults the account balance AT ALL. The internal AI features are
/// included with an entitled account and never billed to credits, so a zero balance says nothing
/// about whether the server will serve the call - a client-side balance gate would block exactly the
/// members the mission exists to serve. The zero-balance-still-Ready and never-reads-the-balance
/// tests are the REVERT-PROOF: put the old balance gate back and they go red with the mission's
/// reported symptom (a zero-balance entitled member blocked before recording).
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
    public async Task LegacyByo_Ready()
    {
        var state = await Build(TranscriptionMode.Byo, key: null).CheckAsync();
        Assert.Equal(HostedAiState.Ready, state);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-500_000L)]
    public async Task DevThrottle_ZeroOrNegativeBalance_StillReady(long balance)
    {
        // The Included AI revert-proof (issue #1360): a zero-balance ENTITLED member is served by the
        // cloud, so the pre-flight must not block them. The old gate answered NeedsCredits here - the
        // exact defect the mission fixes (the acceptance test is a zero-balance trial account
        // completing a dictation round-trip).
        var state = await Build(TranscriptionMode.DevThrottle, balanceMicros: balance).CheckAsync();
        Assert.Equal(HostedAiState.Ready, state);
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
    public async Task DevThrottle_UnknownBalance_Ready()
    {
        var state = await Build(TranscriptionMode.DevThrottle, balanceMicros: null).CheckAsync();
        Assert.Equal(HostedAiState.Ready, state);
    }

    [Fact]
    public async Task BalanceProvider_IsNeverInvoked()
    {
        // The check must not even READ the balance (issue #1360): the read was the desktop's
        // pre-dictation credit fetch, and the balance is a cost fact a normal member's product
        // experience must not depend on. Reverting the readiness change re-invokes this delegate.
        var reads = 0;
        var check = new HostedAiReadiness(
            () => TranscriptionMode.DevThrottle,
            _ => null,
            _ => { reads++; return Task.FromResult<long?>(0); });

        await check.CheckAsync();
        await check.CheckAsync();

        Assert.Equal(0, reads);
    }

    [Fact]
    public void Constructor_NullDelegates_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new HostedAiReadiness(null!, _ => null, _ => Task.FromResult<long?>(0)));
        Assert.Throws<ArgumentNullException>(() => new HostedAiReadiness(() => TranscriptionMode.Byo, null!, _ => Task.FromResult<long?>(0)));
        Assert.Throws<ArgumentNullException>(() => new HostedAiReadiness(() => TranscriptionMode.Byo, _ => null, null!));
    }
}
