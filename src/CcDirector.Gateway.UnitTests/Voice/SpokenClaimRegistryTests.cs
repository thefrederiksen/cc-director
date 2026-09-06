using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;
using static CcDirector.Gateway.Voice.SpokenClaimRegistry;

namespace CcDirector.Gateway.UnitTests.Voice;

/// <summary>
/// The spoken-claim registry's lifecycle (final inspection finding F-07): a claim is FREE after the Gateway
/// transcribes, RESERVED while a prompt carrying it is being delivered, and SPENT only once the Director has
/// accepted that prompt. A delivery that never enters a session releases the claim, so the retry of the
/// same spoken words is still spoken. Before this, the claim was spent the moment the route saw it.
/// </summary>
public sealed class SpokenClaimRegistryTests
{
    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");
    private const string Words = "deploy the gateway and tell me when it is up";

    private static (SpokenClaimRegistry Registry, Func<DateTime> Now, Action<TimeSpan> Advance) Clocked()
    {
        var now = new DateTime(2026, 9, 5, 16, 0, 0, DateTimeKind.Utc);
        var registry = new SpokenClaimRegistry(() => now);
        return (registry, () => now, by => now = now.Add(by));
    }

    [Fact]
    public void AReleasedReservation_LeavesTheClaimFree_SoTheRetryIsStillSpoken()
    {
        var (registry, _, _) = Clocked();
        registry.Register(Alice, "u1", Words);

        Assert.True(registry.TryReserve(Alice, "u1", Words, out _, out var first));
        // The prompt never entered a session: released, not spent.
        registry.Release(first);

        Assert.True(registry.TryReserve(Alice, "u1", Words, out var refusal, out var second));
        Assert.Equal(Refusal.None, refusal);
        registry.Commit(second);

        // Spent for good after the accepted delivery.
        Assert.False(registry.TryReserve(Alice, "u1", Words, out refusal, out _));
        Assert.Equal(Refusal.AlreadySpent, refusal);
    }

    [Fact]
    public void ACommittedReservation_SpendsTheClaim_AndAReplayIsRefused()
    {
        var (registry, _, _) = Clocked();
        registry.Register(Alice, "u1", Words);
        Assert.True(registry.TryReserve(Alice, "u1", Words, out _, out var held));
        registry.Commit(held);

        Assert.False(registry.TryReserve(Alice, "u1", Words, out var refusal, out _));
        Assert.Equal(Refusal.AlreadySpent, refusal);
    }

    [Fact]
    public void AReservedClaim_RefusesAConcurrentSecondPrompt_UntilItIsReleased()
    {
        // Two prompts in flight with one id: the second is typed while the first holds the reservation. If
        // the first then fails and releases, a later prompt may still claim it - once.
        var (registry, _, _) = Clocked();
        registry.Register(Alice, "u1", Words);
        Assert.True(registry.TryReserve(Alice, "u1", Words, out _, out var first));

        Assert.False(registry.TryReserve(Alice, "u1", Words, out var refusal, out _));
        Assert.Equal(Refusal.AlreadySpent, refusal);

        registry.Release(first);
        Assert.True(registry.TryReserve(Alice, "u1", Words, out _, out _));
    }

    [Fact]
    public void OnlyOneOfManyConcurrentReservations_Wins()
    {
        var (registry, _, _) = Clocked();
        registry.Register(Alice, "u1", Words);
        var wins = 0;
        Parallel.For(0, 64, i =>
        {
            if (registry.TryReserve(Alice, "u1", Words, out _, out _)) Interlocked.Increment(ref wins);
        });
        Assert.Equal(1, wins);
    }

    [Fact]
    public void CommitOrRelease_OfAClaimThatIsNotReserved_Throws_RatherThanSilentlyDoingNothing()
    {
        var (registry, _, _) = Clocked();
        registry.Register(Alice, "u1", Words);
        var unheld = new Reservation(Alice, "u1");
        Assert.Throws<InvalidOperationException>(() => registry.Commit(unheld));
        Assert.Throws<InvalidOperationException>(() => registry.Release(unheld));

        Assert.True(registry.TryReserve(Alice, "u1", Words, out _, out var held));
        registry.Commit(held);
        Assert.Throws<InvalidOperationException>(() => registry.Release(held));
        Assert.Throws<InvalidOperationException>(() => registry.Commit(held));
        Assert.Throws<InvalidOperationException>(() => registry.Commit(new Reservation(Alice, "never-registered")));
        Assert.Throws<ArgumentException>(() => registry.Release(default));
    }

    [Fact]
    public void TheFourConditions_TenantIdYouthAndWords_StillGateTheReservation()
    {
        var (registry, _, advance) = Clocked();
        registry.Register(Alice, "u1", "  deploy   the gateway and tell me when it is up ");

        Assert.False(registry.TryReserve(Bob, "u1", Words, out var refusal, out _));
        Assert.Equal(Refusal.Unknown, refusal);
        Assert.False(registry.TryReserve(Alice, "u2", Words, out refusal, out _));
        Assert.Equal(Refusal.Unknown, refusal);
        Assert.False(registry.TryReserve(Alice, "", Words, out refusal, out _));
        Assert.Equal(Refusal.BlankId, refusal);
        Assert.False(registry.TryReserve(Alice, "u1", Words + " and then restart it", out refusal, out _));
        Assert.Equal(Refusal.TextDiffers, refusal);

        // Whitespace does not make it a different utterance; time does.
        advance(ClaimLifetime + TimeSpan.FromSeconds(1));
        Assert.False(registry.TryReserve(Alice, "u1", Words, out refusal, out _));
        Assert.Equal(Refusal.Expired, refusal);
    }

    [Fact]
    public void AReservationInFlight_IsNotSweptByALaterRegistration()
    {
        var (registry, _, advance) = Clocked();
        registry.Register(Alice, "u1", Words);
        Assert.True(registry.TryReserve(Alice, "u1", Words, out _, out var held));
        advance(ClaimLifetime + TimeSpan.FromSeconds(1));
        registry.Register(Alice, "u2", "other words");
        // The held claim is still known, so the outcome can still be recorded on it.
        registry.Release(held);
    }
}
