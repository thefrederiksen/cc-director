using System.Runtime.Versioning;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Ownership at the seams. These pin the two decisions that decide what a stranger's machine looks
/// like after a failed provision: an existing sign-in and an already-running Gateway are SUCCESSES
/// this run does not own, and therefore must never be rolled back.
/// </summary>
[SupportedOSPlatform("windows")]
public class SelfHostConnectTests
{
    private static SelfHostSteps Build(
        bool alreadySignedIn = false,
        bool gatewayRunning = false,
        AccountSignInResult? signInResult = null,
        SelfHostStepResult? startResult = null)
        => SelfHostConnect.Build(
            InstallLayout.Default(),
            isAlreadySignedIn: () => alreadySignedIn,
            isGatewayRunning: _ => Task.FromResult(gatewayRunning),
            signIn: _ => Task.FromResult(signInResult
                ?? new AccountSignInResult(AccountSignInOutcome.SignedIn, "signed in")),
            placeGateway: _ => Task.FromResult(SelfHostStepResult.Created("placed")),
            startGateway: _ => Task.FromResult(startResult ?? SelfHostStepResult.Created("started")),
            enrollDirector: _ => Task.FromResult(SelfHostStepResult.Created("enrolled")),
            probeInferenceReady: _ => Task.FromResult(true),
            compensate: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task SignIn_AlreadySignedIn_SucceedsButIsNotOwned()
    {
        var result = await Build(alreadySignedIn: true).SignIn(CancellationToken.None);

        Assert.True(result.Success);
        // The whole point: a later failure must not sign the user out of an account this run did
        // not sign into.
        Assert.False(result.Owned);
    }

    [Fact]
    public async Task SignIn_FreshSignIn_IsOwned()
    {
        var result = await Build(alreadySignedIn: false).SignIn(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Owned);
    }

    [Fact]
    public async Task SignIn_UserCancelsTheBrowser_IsAFailureAndOwnsNothing()
    {
        var steps = Build(signInResult: new AccountSignInResult(AccountSignInOutcome.Cancelled, "cancelled"));

        var result = await steps.SignIn(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Owned);
    }

    [Fact]
    public async Task StartGateway_OneAlreadyRunning_SucceedsButIsNotOwned()
    {
        var result = await Build(gatewayRunning: true).StartGateway(CancellationToken.None);

        Assert.True(result.Success);
        // Stopping a Gateway the user was already running - possibly serving their whole fleet -
        // because our enrolment failed afterwards would be the worst thing this flow could do.
        Assert.False(result.Owned);
    }

    [Fact]
    public async Task StartGateway_NoneRunning_StartsAndOwnsIt()
    {
        var result = await Build(gatewayRunning: false).StartGateway(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Owned);
    }

    [Fact]
    public async Task StartGateway_AlreadyRunning_DoesNotEvenAttemptToStartAnother()
    {
        var started = false;
        var steps = SelfHostConnect.Build(
            InstallLayout.Default(),
            isAlreadySignedIn: () => true,
            isGatewayRunning: _ => Task.FromResult(true),
            signIn: _ => Task.FromResult(new AccountSignInResult(AccountSignInOutcome.SignedIn, "x")),
            placeGateway: _ => Task.FromResult(SelfHostStepResult.Created("placed")),
            startGateway: _ => { started = true; return Task.FromResult(SelfHostStepResult.Created("started")); },
            enrollDirector: _ => Task.FromResult(SelfHostStepResult.Created("enrolled")),
            probeInferenceReady: _ => Task.FromResult(true),
            compensate: (_, _) => Task.CompletedTask);

        await steps.StartGateway(CancellationToken.None);

        // A second Gateway would fight the first for the port.
        Assert.False(started);
    }

    [Fact]
    public void LocalGatewayUrl_IsLoopback()
    {
        // Self-host means on THIS machine; a self-host enrolment must never point at a remote host.
        Assert.StartsWith("http://127.0.0.1:", SelfHostConnect.LocalGatewayUrl);
    }
}
