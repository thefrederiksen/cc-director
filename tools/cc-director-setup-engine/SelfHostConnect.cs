using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Builds the REAL steps for <see cref="SelfHostOrchestrator"/> out of the engine pieces that
/// already work: the browser sign-in, the Gateway asset placement, the tray installer, and the
/// account enrolment.
///
/// Nothing here invents provisioning. Its whole job is ordering, ownership, and compensation - the
/// parts that decide what a stranger's machine looks like when a provision fails halfway.
///
/// Windows only: self-hosting a Gateway is Windows-only in this product, and the choice state
/// machine already renders the option absent elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SelfHostConnect
{
    /// <summary>Where a locally self-hosted Gateway always answers.</summary>
    public const string LocalGatewayUrl = "http://127.0.0.1:7878";

    /// <summary>
    /// Compose the four steps plus their compensations.
    ///
    /// <paramref name="isAlreadySignedIn"/> and <paramref name="isGatewayRunning"/> are injected
    /// probes rather than inline checks so ownership - the thing that makes rollback safe - is
    /// testable without a machine that happens to be in the right state.
    /// </summary>
    public static SelfHostSteps Build(
        InstallLayout layout,
        Func<bool> isAlreadySignedIn,
        Func<CancellationToken, Task<bool>> isGatewayRunning,
        Func<CancellationToken, Task<AccountSignInResult>> signIn,
        Func<CancellationToken, Task<SelfHostStepResult>> placeGateway,
        Func<CancellationToken, Task<SelfHostStepResult>> startGateway,
        Func<CancellationToken, Task<SelfHostStepResult>> enrollDirector,
        Func<CancellationToken, Task<bool>> probeInferenceReady,
        Func<SelfHostStep, CancellationToken, Task> compensate)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return new SelfHostSteps
        {
            SignIn = async ct =>
            {
                // Already signed in is a SUCCESS that this run does not own. Signing in again would
                // be harmless; rolling that sign-in back later would not be.
                if (isAlreadySignedIn())
                    return SelfHostStepResult.AlreadyThere("Already signed in to DevThrottle.");

                var result = await signIn(ct);
                return result.Succeeded
                    ? SelfHostStepResult.Created(result.Message)
                    : SelfHostStepResult.Failed(result.Message);
            },

            PlaceGatewayAsset = placeGateway,

            StartGateway = async ct =>
            {
                // A Gateway already answering on the local port is not ours to have started, and
                // must not be stopped if a later step fails.
                if (await isGatewayRunning(ct))
                    return SelfHostStepResult.AlreadyThere("A Gateway is already running on this machine.");

                return await startGateway(ct);
            },

            EnrollDirector = enrollDirector,
            ProbeInferenceReady = probeInferenceReady,
            Compensate = compensate,
        };
    }
}
