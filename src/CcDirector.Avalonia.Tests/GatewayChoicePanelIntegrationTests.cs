using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Configuration;
using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Headless integration guards for the #1808a gateway-choice panel (rework R2). The G1-G5 revert-proofs
/// were Core-state only and did not guard the panel wiring where the real risk is - the "a dead test looks
/// like coverage" trap. These drive the REAL panel: the remote-Join transaction (the security + data-loss
/// boundary), the rendered choice cards' actionability and Mac omission, the per-consumer Skip route, and
/// the terminal outcome emission plus onboarding's advance gate. The enrollment seam is injected so the
/// transaction runs with no live Gateway, browser, or network.
///
/// The assembly runs sequentially (TestParallelization), so the process-global CC_DIRECTOR_ROOT redirect
/// and env are not raced.
/// </summary>
public class GatewayChoicePanelIntegrationTests
{
    private const string OldUrl = "https://old-gateway.example:7878";
    private const string OldToken = "old-device-token-DO-NOT-SEND";
    private const string SelectedUrl = "https://new-gateway.example:7878";

    private static void WithTempRoot(Action body)
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-1808a-panel-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // The remote-Join transaction is genuinely async (the real path awaits the enrollment runner and the
    // re-apply). Tests AWAIT it on the Avalonia UI thread rather than blocking, so a real async hop never
    // deadlocks against a synchronous wait.
    private static async Task WithTempRootAsync(Func<Task> body)
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-1808a-panel-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try { await body(); }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Pre-write a saved connection to a PREVIOUS Gateway, so a repair/reconnect Join has an old token that
    // must NOT bypass the enrollment seam or be sent to the newly-selected URL.
    private static void SaveOldGatewayConnection()
        => CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["gateway"] = new JsonObject { ["url"] = OldUrl, ["token"] = OldToken },
        });

    // ---- R2(a): the remote-Join transaction boundary --------------------------------------------

    [AvaloniaFact]
    public async Task RemoteJoin_AlwaysCallsEnrollSeam_WithSelectedUrl_EvenWhenAnOldTokenIsSaved()
    {
        await WithTempRootAsync(async () =>
        {
            SaveOldGatewayConnection();
            Assert.Equal(OldToken, GatewayConfig.Load().Token); // an old token is present

            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            string? seenUrl = null, seenDeviceId = null;
            var calls = 0;
            panel.DirectorIdOverride = "test-director-id";
            panel.RemoteEnrollSeam = (url, deviceId, _, _) =>
            {
                calls++;
                seenUrl = url;
                seenDeviceId = deviceId;
                return Task.FromResult(OperationResult<MobileEnrollmentResponse>.Fail("not this time"));
            };

            await panel.ConnectToAsync(SelectedUrl, SelectedUrl, remote: true);

            // The runner IS called even though an old token exists (the old bug bypassed it), and it is
            // called with the SELECTED url and this device's id - never the old Gateway or its token.
            Assert.Equal(1, calls);
            Assert.Equal(SelectedUrl, seenUrl);
            Assert.Equal("test-director-id", seenDeviceId);
        });
    }

    [AvaloniaFact]
    public async Task RemoteJoin_FailedEnroll_DoesNotMutateConfig_AndDoesNotReapply()
    {
        await WithTempRootAsync(async () =>
        {
            SaveOldGatewayConnection();

            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            var reapplied = false;
            panel.DirectorIdOverride = "test-director-id";
            panel.ReapplyGatewaySeam = () => { reapplied = true; return Task.CompletedTask; };
            panel.RemoteEnrollSeam = (_, _, _, _) =>
                Task.FromResult(OperationResult<MobileEnrollmentResponse>.Fail("verification failed"));

            await panel.ConnectToAsync(SelectedUrl, SelectedUrl, remote: true);

            // Verification failed, so the previously-saved connection is untouched (no pre-write corrupts it)
            // and the client is NOT re-applied with any credential.
            var config = GatewayConfig.Load();
            Assert.Equal(OldUrl, config.Url);
            Assert.Equal(OldToken, config.Token);
            Assert.False(reapplied);
        });
    }

    [AvaloniaFact]
    public async Task RemoteJoin_SuccessfulEnroll_ReAppliesTheVerifiedCredential()
    {
        await WithTempRootAsync(async () =>
        {
            SaveOldGatewayConnection();

            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            var reapplied = false;
            panel.DirectorIdOverride = "test-director-id";
            panel.ReapplyGatewaySeam = () => { reapplied = true; return Task.CompletedTask; };
            // Simulate the runner's verified-success persistence (it owns the atomic url+key write).
            panel.RemoteEnrollSeam = (url, _, _, _) =>
            {
                CcDirectorConfigService.MergePatch(new JsonObject
                {
                    ["gateway"] = new JsonObject { ["url"] = url, ["token"] = "new-verified-key" },
                });
                return Task.FromResult(OperationResult<MobileEnrollmentResponse>.Ok(
                    new MobileEnrollmentResponse { DeviceKey = "new-verified-key" }));
            };

            await panel.ConnectToAsync(SelectedUrl, SelectedUrl, remote: true);

            // On verified success the panel re-applies so the client authenticates with the NEW credential.
            Assert.True(reapplied);
            Assert.Equal(SelectedUrl, GatewayConfig.Load().Url);
        });
    }

    // ---- R2(b): rendered card actionability + Mac omission ---------------------------------------

    [AvaloniaFact]
    public void Choice_OnWindows_RendersDisabledSelfHost_HostedJoinAndSkipActionable()
    {
        var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Onboarding);
        // Force a self-host-capable (Windows) context regardless of the test host.
        panel.SetChoiceContextForTests(new GatewayChoiceContext(GatewayChoiceConsumer.Onboarding, SelfHostSupported: true));
        panel.ShowChoiceForTests();

        // Self-host is still a disabled "coming" card; hosted is now a live, actionable card.
        Assert.False(CardFor(panel, GatewayChoiceAction.SelfHost).IsEnabled);
        Assert.True(CardFor(panel, GatewayChoiceAction.UseHosted).IsEnabled);
        Assert.True(CardFor(panel, GatewayChoiceAction.JoinExisting).IsEnabled);
        Assert.True(CardFor(panel, GatewayChoiceAction.Skip).IsEnabled);
    }

    // ---- Hosted choice: the enabled card runs the SAME proven hosted enroll the CLI uses ----------

    [AvaloniaFact]
    public async Task HostedChoice_EnabledCard_RunsHostedEnroll_WithThisDeviceId()
    {
        await WithTempRootAsync(async () =>
        {
            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            panel.SetChoiceContextForTests(new GatewayChoiceContext(GatewayChoiceConsumer.Settings, SelfHostSupported: true));
            panel.ShowChoiceForTests();
            panel.DirectorIdOverride = "test-director-id";

            string? seenDeviceId = null;
            var calls = 0;
            // Injected hosted-enroll seam: no browser, no network. Fail so nothing re-applies or handshakes.
            panel.HostedEnrollSeam = (deviceId, _, _) =>
            {
                calls++;
                seenDeviceId = deviceId;
                return Task.FromResult(OperationResult<MobileEnrollmentResponse>.Fail("not this time"));
            };

            // The hosted card is enabled, so activating it EXACTLY as a click would must fire the enroll.
            Assert.True(CardFor(panel, GatewayChoiceAction.UseHosted).IsEnabled);
            await panel.HostedEnrollAndHandshakeAsync();

            Assert.Equal(1, calls);
            Assert.Equal("test-director-id", seenDeviceId);
        });
    }

    [AvaloniaFact]
    public async Task HostedChoice_FailedEnroll_DoesNotReapply_AndDoesNotMutateConfig()
    {
        await WithTempRootAsync(async () =>
        {
            SaveOldGatewayConnection();

            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            var reapplied = false;
            panel.DirectorIdOverride = "test-director-id";
            panel.ReapplyGatewaySeam = () => { reapplied = true; return Task.CompletedTask; };
            panel.HostedEnrollSeam = (_, _, _) =>
                Task.FromResult(OperationResult<MobileEnrollmentResponse>.Fail("hosted enroll failed"));

            await panel.HostedEnrollAndHandshakeAsync();

            // A failed hosted enroll persists nothing and never re-applies: the old connection is untouched.
            var config = GatewayConfig.Load();
            Assert.Equal(OldUrl, config.Url);
            Assert.Equal(OldToken, config.Token);
            Assert.False(reapplied);
        });
    }

    [AvaloniaFact]
    public async Task HostedChoice_SuccessfulEnroll_ReAppliesTheVerifiedCredential()
    {
        await WithTempRootAsync(async () =>
        {
            const string hostedUrl = "https://devthrottle-gw.azurewebsites.net";
            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            var reapplied = false;
            panel.DirectorIdOverride = "test-director-id";
            panel.ReapplyGatewaySeam = () => { reapplied = true; return Task.CompletedTask; };
            // Mirror the runner's verified-success persistence (it owns the atomic hosted-url+key write).
            panel.HostedEnrollSeam = (_, _, _) =>
            {
                CcDirectorConfigService.MergePatch(new JsonObject
                {
                    ["gateway"] = new JsonObject { ["url"] = hostedUrl, ["token"] = "hosted-verified-key" },
                });
                return Task.FromResult(OperationResult<MobileEnrollmentResponse>.Ok(
                    new MobileEnrollmentResponse { DeviceKey = "hosted-verified-key" }));
            };

            await panel.HostedEnrollAndHandshakeAsync();

            Assert.True(reapplied);
            Assert.Equal(hostedUrl, GatewayConfig.Load().Url);
        });
    }

    // ---- Change gateway: disconnect clears the stored connection, then re-shows the choice --------

    [AvaloniaFact]
    public async Task ChangeGateway_Disconnect_ClearsTheStoredConnection_ReAppliesAndShowsChoice()
    {
        await WithTempRootAsync(async () =>
        {
            SaveOldGatewayConnection();
            Assert.True(GatewayConfig.Load().IsEnabled); // connected to a gateway

            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            var reapplied = false;
            panel.ReapplyGatewaySeam = () => { reapplied = true; return Task.CompletedTask; };

            await panel.DisconnectAndShowChoiceAsync();

            // The stored connection is cleared: url + token gone, so the Director is local-only again.
            var config = GatewayConfig.Load();
            Assert.False(config.IsEnabled);
            Assert.Equal("", config.Url);
            Assert.Equal("", config.Token);
            // The running client was re-applied so it drops the old connection, and the choice is shown so a
            // different gateway can be connected.
            Assert.True(reapplied);
            Assert.NotNull(CardFor(panel, GatewayChoiceAction.JoinExisting));
            Assert.NotNull(CardFor(panel, GatewayChoiceAction.UseHosted));
        });
    }

    [AvaloniaFact]
    public void Choice_OnMac_OmitsSelfHostCardEntirely()
    {
        var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Onboarding);
        // A Mac cannot self-host: SelfHostSupported=false.
        panel.SetChoiceContextForTests(new GatewayChoiceContext(GatewayChoiceConsumer.Onboarding, SelfHostSupported: false));
        panel.ShowChoiceForTests();

        Assert.DoesNotContain(panel.ChoiceCardsForTests,
            c => c is Border { Tag: GatewayChoiceAction.SelfHost });
        // The other three actions are still offered.
        Assert.NotNull(CardFor(panel, GatewayChoiceAction.UseHosted));
        Assert.NotNull(CardFor(panel, GatewayChoiceAction.JoinExisting));
        Assert.NotNull(CardFor(panel, GatewayChoiceAction.Skip));
    }

    // ---- R2(c): the per-consumer Skip route ------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(GatewayChoiceConsumer.Onboarding, GatewaySkipBehavior.CompleteOnboardingLocalOnly)]
    [InlineData(GatewayChoiceConsumer.Settings, GatewaySkipBehavior.ReturnToChoice)]
    [InlineData(GatewayChoiceConsumer.StatusWindow, GatewaySkipBehavior.ReturnToChoice)]
    public void Skip_ActivatingTheCard_RaisesSkipRequested_WithThePerConsumerBehavior(
        GatewayChoiceConsumer consumer, GatewaySkipBehavior expected)
    {
        var panel = GatewayConnectionPanel.CreateForCurrentState(consumer);
        panel.ShowChoiceForTests();
        GatewaySkipBehavior? raised = null;
        panel.SkipRequested += (_, behavior) => raised = behavior;

        panel.ActivateChoiceForTests(GatewayChoiceAction.Skip);

        Assert.Equal(expected, raised);
    }

    // ---- R2(d): terminal outcome emission + onboarding advance gate ------------------------------

    [AvaloniaFact]
    public void TerminalOutcome_IsEmitted_Connected_SignedIn_AndNotReadyInThisSlice()
    {
        WithTempRoot(() =>
        {
            var panel = GatewayConnectionPanel.CreateForCurrentState(GatewayChoiceConsumer.Settings);
            GatewayConnectionOutcome? emitted = null;
            panel.ConnectionSettled += (_, outcome) => emitted = outcome;

            panel.EmitTerminalForTests();

            Assert.NotNull(emitted);
            Assert.True(emitted!.Connected);
            Assert.True(emitted.SignedIn);
            Assert.Equal(GatewayInferenceReadiness.NotReady, emitted.Inference);
        });
    }

    [AvaloniaFact]
    public void Onboarding_DoesNotAdvance_UntilTheTerminalOutcome_TransportAloneCannot()
    {
        WithTempRoot(() =>
        {
            var dialog = new OnboardingWizardDialog(new AgentOptions());
            Assert.Equal(0, dialog.CurrentStepForTests); // on the gateway step

            // No terminal outcome yet: Next must NOT advance (there is no transport-only signal that could).
            dialog.ClickNextForTests();
            Assert.Equal(0, dialog.CurrentStepForTests);

            // The panel emits its terminal settled outcome (connected AND signed in) -> now Next advances.
            dialog.GatewayPanelForTests.EmitTerminalForTests();
            dialog.ClickNextForTests();
            Assert.Equal(1, dialog.CurrentStepForTests);
        });
    }

    private static Border CardFor(GatewayConnectionPanel panel, GatewayChoiceAction action)
    {
        foreach (var child in panel.ChoiceCardsForTests)
            if (child is Border { Tag: GatewayChoiceAction tag } card && tag == action)
                return card;
        throw new Xunit.Sdk.XunitException($"No choice card was rendered for {action}.");
    }
}
