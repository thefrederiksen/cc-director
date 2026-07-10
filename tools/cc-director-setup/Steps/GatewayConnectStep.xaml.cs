using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

/// <summary>
/// The mandatory gateway-join step for a Workstation install (issues #646, #1198, #1206). A machine that
/// joins an existing fleet must connect to its gateway before the install can finish - the gateway is the
/// account authority, so a Workstation with no gateway connection is useless.
///
/// Joining is DevThrottle account sign-in, and the person never types a gateway address (issue #1206): they
/// click "Sign in to DevThrottle", which opens the system browser to sign in, then the installer reads the
/// account's devices and discovers the gateway's own published front-door URL. With exactly one gateway it
/// connects automatically; with more than one it shows a NAME-based chooser (never a raw URL); with none it
/// shows a clear "start and sign in your gateway first" message. The chosen gateway's URL is used to
/// register this machine as an account device, exchange its key at the gateway for a local per-device key,
/// and persist the gateway URL + key to config.json (all in <see cref="GatewayAccountEnrollRunner"/>).
///
/// Until the join succeeds the step stays unverified, so the wizard keeps Next/Finish disabled (mirrors the
/// forced Sign-in gate, issue #657). A cancelled sign-in, no reachable gateway, a different account, or an
/// unreachable gateway shows a clear message and does NOT mark the step verified, so the install cannot
/// complete. The step raises <see cref="Connected"/> exactly once, when a local device key is issued and
/// persisted.
///
/// This step is only shown on the Workstation path; the Gateway role IS the gateway and signs in on its
/// own dedicated step instead.
/// </summary>
public partial class GatewayConnectStep : UserControl
{
    private readonly GatewayAccountEnrollRunner _runner;
    private readonly string _deviceId;
    private readonly string _machineName;
    private CancellationTokenSource? _cts;

    /// <summary>True once the gateway has issued a local device key and it was persisted. The wizard gates
    /// Next on this; once true it stays true so returning via Back keeps the state.</summary>
    public bool IsVerified { get; private set; }

    /// <summary>Raised once when the join succeeds, so the wizard can enable Next.</summary>
    public event EventHandler? Connected;

    public GatewayConnectStep() : this(runner: null)
    {
    }

    /// <summary>Constructor seam so a test or proof harness can inject a runner (e.g. one with a fake
    /// sign-in and a fake HTTP handler). When no runner is supplied a default one is built that drives the
    /// real browser sign-in, discovers the account's gateways, registers the account device, enrolls at the
    /// chosen gateway, and persists the issued local key to config.json.</summary>
    public GatewayConnectStep(GatewayAccountEnrollRunner? runner)
    {
        InitializeComponent();
        _runner = runner ?? new GatewayAccountEnrollRunner();
        // The installer has no running Director yet, so it mints a stable device id for this machine. It is
        // used both as the account-registration install id and as the /m/enroll device id, so the gateway's
        // local record maps to the same cloud roster row. The credential the Director presents at runtime is
        // the issued local per-device KEY (persisted to config.json), not this id.
        _deviceId = Guid.NewGuid().ToString();
        _machineName = Environment.MachineName;

        SetupLog.Write($"[GatewayConnectStep] Created: machine={_machineName}");
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] SignInButton_Click");
        try
        {
            EnterWaitingState("Waiting for you to sign in in your browser...", cancellable: true);
            _cts = new CancellationTokenSource();

            // Sign in and discover the account's gateways. The browser opens and we await the loopback
            // hand-back, then list the account devices - all async, so the UI stays responsive and Cancel works.
            var discovered = await _runner.SignInAndDiscoverGatewaysAsync(_cts.Token);
            if (!discovered.Success || discovered.Value is null)
            {
                ShowRetryable(discovered.ErrorMessage ?? "Could not find your gateway. Please try again.");
                return;
            }

            var gateways = discovered.Value;
            if (gateways.Count == 1)
            {
                // Exactly one gateway: connect automatically, no address to type and no chooser.
                await ConnectToGatewayAsync(gateways[0]);
                return;
            }

            // More than one: let the person choose by name (a raw URL is never shown).
            ShowChooser(gateways);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[GatewayConnectStep] SignInButton_Click FAILED: {ex}");
            ShowRetryable("Connecting to the gateway failed unexpectedly. Please try again.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void ChooseConnectButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] ChooseConnectButton_Click");
        try
        {
            if (GatewayList.SelectedItem is not DiscoveredGateway selected)
                return;

            _cts = new CancellationTokenSource();
            await ConnectToGatewayAsync(selected);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[GatewayConnectStep] ChooseConnectButton_Click FAILED: {ex}");
            ShowRetryable("Connecting to the gateway failed unexpectedly. Please try again.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Registers this machine on the account and enrolls it at the discovered gateway, then applies
    /// the outcome. The connect call is not cancellable (the sign-in wait already completed), so the waiting
    /// row hides Cancel here.</summary>
    private async Task ConnectToGatewayAsync(DiscoveredGateway gateway)
    {
        SetupLog.Write($"[GatewayConnectStep] ConnectToGatewayAsync: gateway={gateway.Name}");
        EnterWaitingState("Connecting to your gateway...", cancellable: false);

        // _cts is set by the caller; the connect call honors it but the Cancel button is hidden here.
        var token = _cts?.Token ?? CancellationToken.None;
        var result = await _runner.EnrollWithDiscoveredGatewayAsync(gateway.EndpointUrl, _deviceId, _machineName, token);
        ApplyResult(result);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] CancelButton_Click: cancelling the sign-in wait");
        // Cancelling the wait makes the sign-in return a cancelled result, which ShowRetryable turns into a
        // retryable state. The button itself stays disabled until the wait unwinds.
        CancelButton.IsEnabled = false;
        _cts?.Cancel();
    }

    /// <summary>Shows the waiting row with the given message: hide any prior chooser/message/success, disable
    /// the Sign-in button, and enable Cancel only when the wait is cancellable (the sign-in wait).</summary>
    private void EnterWaitingState(string message, bool cancellable)
    {
        SignInButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Collapsed;
        SuccessPanel.Visibility = Visibility.Collapsed;
        ChooserPanel.Visibility = Visibility.Collapsed;
        WaitingText.Text = message;
        CancelButton.IsEnabled = cancellable;
        CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        WaitingPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Shows the name-based gateway chooser (issue #1206), hiding the Sign-in button and the waiting
    /// row. The Connect button enables once a gateway is selected.</summary>
    private void ShowChooser(IReadOnlyList<DiscoveredGateway> gateways)
    {
        SetupLog.Write($"[GatewayConnectStep] ShowChooser: {gateways.Count} gateways");
        WaitingPanel.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        SignInButton.IsEnabled = false;
        GatewayList.ItemsSource = gateways;
        ChooseConnectButton.IsEnabled = false;
        ChooserPanel.Visibility = Visibility.Visible;
    }

    private void GatewayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ChooseConnectButton.IsEnabled = GatewayList.SelectedItem is DiscoveredGateway;
    }

    private void ApplyResult(OperationResult<MobileEnrollmentResponse> result)
    {
        WaitingPanel.Visibility = Visibility.Collapsed;

        if (result.Success && result.Value is not null)
        {
            SetupLog.Write("[GatewayConnectStep] ApplyResult: connected");
            IsVerified = true;
            SignInButton.Visibility = Visibility.Collapsed;
            ChooserPanel.Visibility = Visibility.Collapsed;
            SuccessText.Text = "Connected to the gateway. Click Next to continue.";
            SuccessPanel.Visibility = Visibility.Visible;
            Connected?.Invoke(this, EventArgs.Empty);
            return;
        }

        SetupLog.Write($"[GatewayConnectStep] ApplyResult: blocked - {result.ErrorMessage}");
        ShowRetryable(result.ErrorMessage ?? "The gateway did not accept the sign-in.");
    }

    /// <summary>Returns the step to a retryable state with a message - the chooser and waiting row are
    /// hidden and the Sign-in button is re-enabled so the user can try again. The step stays UNVERIFIED, so
    /// the wizard keeps Next disabled and the install cannot complete.</summary>
    private void ShowRetryable(string message)
    {
        WaitingPanel.Visibility = Visibility.Collapsed;
        ChooserPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        SignInButton.IsEnabled = true;
    }
}
