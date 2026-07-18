using CcDirector.Core.GatewayConnection;
using Xunit;

namespace CcDirector.Core.Tests.GatewayConnection;

/// <summary>
/// Pins the one common TERMINAL result the panel raises when it settles (architecture two-step-install v4,
/// section 2, #1808a). The result must carry the WHOLE picture - connected + signed in + inference
/// readiness - so a consumer advances on the settled outcome, not on the transport handshake alone. In this
/// slice inference readiness is an honest NotReady placeholder (a connected, healthy Gateway does NOT imply
/// inference is usable); the real readiness contract is #1810.
/// </summary>
public class GatewayConnectionOutcomeTests
{
    [Fact]
    public void ConnectedAndSignedIn_CarriesConnectedSignedIn_AndTheGivenInferenceReadiness()
    {
        var outcome = GatewayConnectionOutcome.ConnectedAndSignedIn(GatewayInferenceReadiness.NotReady);

        Assert.True(outcome.Connected);
        Assert.True(outcome.SignedIn);
        // The panel reports NotReady in this slice - Gateway health is NOT read as inference readiness.
        Assert.Equal(GatewayInferenceReadiness.NotReady, outcome.Inference);
    }

    [Fact]
    public void Outcome_PreservesEachFieldItIsGiven()
    {
        var outcome = new GatewayConnectionOutcome(
            Connected: true, SignedIn: false, Inference: GatewayInferenceReadiness.Unknown);

        Assert.True(outcome.Connected);
        Assert.False(outcome.SignedIn);
        Assert.Equal(GatewayInferenceReadiness.Unknown, outcome.Inference);
    }
}
