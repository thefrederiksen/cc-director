using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The router's outcome line must not blame the network for a Gateway-side refusal.
///
/// A null result from the send has TWO causes and the router cannot see which: the Director is not
/// tunnel-connected, or the Gateway refused the send before it ever left (the hosted no-tenant-scope deny in
/// GatewayHost.SendCommandAsync). The line used to assert the first one - "director not tunnel-connected
/// (unroutable)".
///
/// That single word cost a day. When the voice-mode sweep lost its tenant scope, every dropped command was
/// logged as an unreachable Director - on a Director that answered a different verb 70 milliseconds later, in
/// the same log. A Gateway bug dressed as an infrastructure flake reads as something to wait out rather than
/// something to fix, and the log actively argued against the true diagnosis for anyone who went looking.
///
/// The message is therefore the deliverable here, not an incidental string, so it is pinned like any other
/// behaviour. These assertions are positive on purpose: an absence-only check ("does not say
/// tunnel-connected") would still pass if the line were deleted altogether, which would be a worse outcome
/// than the wrong wording.
/// </summary>
public sealed class DirectorCommandRouterOutcomeWordingTests
{
    [Fact]
    public void NoResult_namesBothCauses_andAssertsNeither()
    {
        var outcome = DirectorCommandRouter.DescribeSendOutcome(null);

        // It says what was actually observed: nothing came back.
        Assert.Contains("no result", outcome);
        // Both causes are offered...
        Assert.Contains("not tunnel-connected", outcome);
        Assert.Contains("refused the send", outcome);
        // ...as alternatives, never as a settled verdict, and it points at the line that does know.
        Assert.Contains("OR", outcome);
        Assert.Contains("[GatewayHost]", outcome);

        // The exact phrasing that made the Gateway bug look like a network fault is gone. Paired with the
        // positive assertions above, this cannot pass by the line having disappeared.
        Assert.DoesNotContain("unroutable", outcome);
    }

    [Theory]
    [InlineData(DirectorCommandStatus.Ok)]
    [InlineData(DirectorCommandStatus.NotFound)]
    [InlineData(DirectorCommandStatus.Timeout)]
    [InlineData(DirectorCommandStatus.TunnelDropped)]
    public void AResult_reportsItsStatus_andNeverSpeculatesAboutTheTunnel(DirectorCommandStatus status)
    {
        // A result of ANY status means the send reached the stream and came back. The two-cause hedge belongs
        // only to the null case; repeating it here would make every ordinary outcome read like a failure.
        var outcome = DirectorCommandRouter.DescribeSendOutcome(
            status == DirectorCommandStatus.Ok
                ? DirectorCommandResult.Success("{}")
                : DirectorCommandResult.Fail(status, "some reason"));

        Assert.Equal($"stream status={status}", outcome);
        Assert.DoesNotContain("no result", outcome);
        Assert.DoesNotContain("refused the send", outcome);
    }
}
