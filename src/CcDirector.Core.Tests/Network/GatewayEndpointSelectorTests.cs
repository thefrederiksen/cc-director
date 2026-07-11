using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Core.Tests.Network;

/// <summary>
/// Tests for <see cref="GatewayEndpointSelector"/> - the priority-order walker that picks the
/// first reachable gateway address from an ordered candidate list (issue #1233). The probe is
/// injected, so these exercise the ordering and fall-through policy with no live gateway.
/// </summary>
public class GatewayEndpointSelectorTests
{
    // A probe that reports the given set of URLs reachable (null reason) and everything else
    // unreachable, and records the order in which it was asked so tests can assert that later
    // candidates were never probed once an earlier one won.
    private static Func<string, CancellationToken, Task<string?>> ProbeThatAccepts(
        List<string> probedOrder, params string[] reachable)
    {
        var ok = new HashSet<string>(reachable, StringComparer.OrdinalIgnoreCase);
        return (url, _) =>
        {
            probedOrder.Add(url);
            return Task.FromResult<string?>(ok.Contains(url) ? null : $"unreachable: {url}");
        };
    }

    [Fact]
    public async Task SelectAsync_FirstCandidateReachable_ChoosesItAndDoesNotProbeRest()
    {
        var probed = new List<string>();
        var candidates = new[] { "http://MACHINE:7878", "https://machine.tail.ts.net:7878", "http://192.168.1.20:7878" };

        var result = await GatewayEndpointSelector.SelectAsync(
            candidates, ProbeThatAccepts(probed, "http://MACHINE:7878"));

        Assert.True(result.Found);
        Assert.Equal("http://MACHINE:7878", result.ChosenUrl);
        Assert.Single(probed);                              // stopped at the first
        Assert.Equal("http://MACHINE:7878", probed[0]);
    }

    [Fact]
    public async Task SelectAsync_FallsThroughToTailscale_WhenMachineNameUnreachable()
    {
        var probed = new List<string>();
        var candidates = new[] { "http://MACHINE:7878", "https://machine.tail.ts.net:7878", "http://192.168.1.20:7878" };

        var result = await GatewayEndpointSelector.SelectAsync(
            candidates, ProbeThatAccepts(probed, "https://machine.tail.ts.net:7878"));

        Assert.True(result.Found);
        Assert.Equal("https://machine.tail.ts.net:7878", result.ChosenUrl);
        // Machine name was tried first, then Tailscale won - IP is never reached.
        Assert.Equal(new[] { "http://MACHINE:7878", "https://machine.tail.ts.net:7878" }, probed);
    }

    [Fact]
    public async Task SelectAsync_FallsThroughToIp_WhenMachineNameAndTailscaleUnreachable()
    {
        var probed = new List<string>();
        var candidates = new[] { "http://MACHINE:7878", "https://machine.tail.ts.net:7878", "http://192.168.1.20:7878" };

        var result = await GatewayEndpointSelector.SelectAsync(
            candidates, ProbeThatAccepts(probed, "http://192.168.1.20:7878"));

        Assert.True(result.Found);
        Assert.Equal("http://192.168.1.20:7878", result.ChosenUrl);
        Assert.Equal(candidates, probed);                   // all three tried, in order
    }

    [Fact]
    public async Task SelectAsync_NoCandidateReachable_ReturnsNotFoundWithEveryAttempt()
    {
        var probed = new List<string>();
        var candidates = new[] { "http://MACHINE:7878", "https://machine.tail.ts.net:7878", "http://192.168.1.20:7878" };

        var result = await GatewayEndpointSelector.SelectAsync(
            candidates, ProbeThatAccepts(probed /* accepts nothing */));

        Assert.False(result.Found);
        Assert.Null(result.ChosenUrl);
        Assert.Equal(3, result.Attempts.Count);
        Assert.All(result.Attempts, a => Assert.False(a.Reachable));
        Assert.All(result.Attempts, a => Assert.NotNull(a.Reason));
    }

    [Fact]
    public async Task SelectAsync_EmptyList_ReturnsNotFound()
    {
        var result = await GatewayEndpointSelector.SelectAsync(
            Array.Empty<string>(), (_, _) => Task.FromResult<string?>(null));

        Assert.False(result.Found);
        Assert.Empty(result.Attempts);
    }

    [Fact]
    public async Task SelectAsync_BlankCandidates_AreRecordedAndSkippedNeverProbed()
    {
        var probed = new List<string>();
        var candidates = new[] { "", "   ", "http://MACHINE:7878" };

        var result = await GatewayEndpointSelector.SelectAsync(
            candidates, ProbeThatAccepts(probed, "http://MACHINE:7878"));

        Assert.True(result.Found);
        Assert.Equal("http://MACHINE:7878", result.ChosenUrl);
        Assert.Single(probed);                              // blanks were never probed
        Assert.Equal("http://MACHINE:7878", probed[0]);
        Assert.Equal(3, result.Attempts.Count);             // both blanks recorded as failed attempts
        Assert.False(result.Attempts[0].Reachable);
        Assert.False(result.Attempts[1].Reachable);
    }

    [Fact]
    public async Task SelectAsync_CandidateIsTrimmed_BeforeProbing()
    {
        var probed = new List<string>();
        var result = await GatewayEndpointSelector.SelectAsync(
            new[] { "  http://MACHINE:7878  " }, ProbeThatAccepts(probed, "http://MACHINE:7878"));

        Assert.True(result.Found);
        Assert.Equal("http://MACHINE:7878", result.ChosenUrl);
        Assert.Equal("http://MACHINE:7878", probed[0]);
    }

    [Fact]
    public async Task SelectAsync_ProbeThatThrows_IsTreatedAsFailedAttemptAndWalkContinues()
    {
        var candidates = new[] { "http://MACHINE:7878", "https://machine.tail.ts.net:7878" };
        var result = await GatewayEndpointSelector.SelectAsync(candidates, (url, _) =>
        {
            if (url == "http://MACHINE:7878") throw new HttpRequestException("connection refused");
            return Task.FromResult<string?>(null);          // Tailscale answers
        });

        Assert.True(result.Found);
        Assert.Equal("https://machine.tail.ts.net:7878", result.ChosenUrl);
        Assert.False(result.Attempts[0].Reachable);
        Assert.Contains("probe threw", result.Attempts[0].Reason);
    }

    [Fact]
    public async Task SelectAsync_CancellationRequested_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            GatewayEndpointSelector.SelectAsync(
                new[] { "http://MACHINE:7878" },
                (_, _) => Task.FromResult<string?>(null),
                cts.Token));
    }

    [Fact]
    public async Task SelectAsync_NullArguments_Throw()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            GatewayEndpointSelector.SelectAsync(null!, (_, _) => Task.FromResult<string?>(null)));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            GatewayEndpointSelector.SelectAsync(Array.Empty<string>(), null!));
    }
}
