using CcDirector.Core.Configuration;
using CcDirector.Core.Onboarding;
using CcDirector.Core.Settings;
using Xunit;

namespace CcDirector.Core.Tests.Onboarding;

/// <summary>
/// Regression proof for issue #1809: a freshly installed Director is fully usable with NO account and
/// NO gateway. Together with the shipped Director-only installer (#1807), this gates the website's
/// "no account to install" messaging, so the local-only first-run behaviour is pinned here so it can
/// never quietly regress into requiring a sign-in or a gateway.
///
/// This half covers the first-run onboarding contract: a fresh install offers onboarding, but skipping
/// it (which is what a user with no account/gateway does) leaves the Director in a legitimate local-only
/// state - no gateway is written, and the wizard never nags again on relaunch. Connecting a gateway
/// still needs a DevThrottle login, but that is a later, opt-in action and is out of scope here. The
/// companion session-creation half lives in <c>LocalOnlyDirectorSessionTests</c>.
///
/// Config-touching tests redirect CcStorage to a temp root via CC_DIRECTOR_ROOT and are serialized with
/// the other config-env tests.
/// </summary>
[Collection("ConfigEnvSerial")]
public class LocalOnlyFirstRunTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "cc-director-localonly-tests", Guid.NewGuid().ToString("N"));

    private static void WithRoot(Action body)
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = NewRoot();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FreshInstall_WithNoAccountAndNoGateway_OffersOnboarding()
    {
        // A brand-new install (no gateway.url, no completion marker) is exactly the no-account /
        // no-gateway state; the first-run wizard is offered so the user CAN connect a gateway - but
        // is never forced to.
        WithRoot(() => Assert.True(OnboardingModel.ShouldShowOnboarding()));
    }

    [Fact]
    public void SkipWithNoGateway_LeavesADurableLocalOnlyState_WithNoGatewayWritten()
    {
        WithRoot(() =>
        {
            // The user skips first-run without connecting a gateway (BtnSkip_Click -> MarkComplete,
            // and crucially NO PersistGatewayUrl). This is the local-only, no-account path.
            OnboardingModel.MarkComplete();

            // No gateway was written, so the Director stays local-only - it did not silently acquire a
            // gateway or an account just because onboarding was dismissed.
            Assert.Equal("", GatewayConfig.Load().Url);
            Assert.True(OnboardingModel.IsOnboardingComplete());
        });
    }

    [Fact]
    public void SkipWithNoGateway_DoesNotRenagOnRelaunch()
    {
        WithRoot(() =>
        {
            Assert.True(OnboardingModel.ShouldShowOnboarding());

            // Skip without connecting a gateway.
            OnboardingModel.MarkComplete();

            // Every subsequent launch reads config.json fresh; the completion marker keeps the wizard
            // from auto-opening again even though no gateway is configured. Assert across repeated
            // reads to model relaunch-after-relaunch: a local-only Director is never re-nagged.
            Assert.False(OnboardingModel.ShouldShowOnboarding());
            Assert.False(OnboardingModel.ShouldShowOnboarding());
            Assert.False(OnboardingModel.ShouldShowOnboarding());

            // And it is still local-only - no gateway crept in.
            Assert.Equal("", GatewayConfig.Load().Url);
        });
    }
}
