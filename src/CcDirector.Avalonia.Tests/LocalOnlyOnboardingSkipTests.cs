using Avalonia.Headless.XUnit;
using CcDirector.Avalonia;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Onboarding;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Regression proof for issue #1809 at the real onboarding Skip seam: a user with no account and no
/// gateway skips first-run, and the Director stays a durable local-only install that never re-nags.
///
/// This drives the PRODUCTION path, not the model layer: <see cref="OnboardingWizardDialog.FinishAsync"/>
/// is exactly what the Skip button (BtnSkip_Click) invokes, and it must persist the onboarding-complete
/// marker (so <see cref="OnboardingModel.ShouldShowOnboarding"/> - the gate MainWindow's
/// MaybeShowOnboardingWizardAsync reads - returns false next launch) WITHOUT writing a gateway.url. If
/// Skip ever stops persisting the marker, or starts writing a gateway, this goes red.
///
/// Constructed headless and never shown, so the embedded gateway panel's on-attach LAN scan never runs.
/// Config is redirected to a temp root via CC_DIRECTOR_ROOT; the assembly runs sequentially
/// (TestParallelization), so the process-global env var is not raced.
/// </summary>
public class LocalOnlyOnboardingSkipTests
{
    [AvaloniaFact]
    public async Task Skip_WithNoGateway_PersistsOnboardingComplete_AndStaysLocalOnly()
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-director-localonly-skip-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            // Fresh install: onboarding is offered because there is no gateway and no completion marker.
            Assert.True(OnboardingModel.ShouldShowOnboarding());

            var dialog = new OnboardingWizardDialog(new AgentOptions());

            // Skip (the BtnSkip_Click path) without ever connecting a gateway.
            await dialog.FinishAsync(wantsNewSession: false);

            // The completion marker is persisted, so a relaunch's ShouldShowOnboarding gate returns
            // false - no re-nag - and Skip routed to no new session.
            Assert.False(dialog.WantsNewSession);
            Assert.True(OnboardingModel.IsOnboardingComplete());
            Assert.False(OnboardingModel.ShouldShowOnboarding());

            // And the Director stays local-only: Skip wrote no gateway.
            Assert.Equal("", GatewayConfig.Load().Url);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
