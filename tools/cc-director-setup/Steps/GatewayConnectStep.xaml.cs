using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

/// <summary>
/// The mandatory gateway-join step for a Workstation install (issues #646, #1198, #1206, #1233). A machine
/// that joins an existing fleet must connect to its gateway before the install can finish - the gateway is
/// the account authority, so a Workstation with no gateway connection is useless.
///
/// Joining is DevThrottle account sign-in. The step first tries to find the gateway automatically: the person
/// clicks "Sign in to DevThrottle", the system browser opens, and the installer reads the account's devices
/// and discovers the gateway's own published front-door address (issue #1206). With exactly one gateway it
/// connects automatically; with more than one it shows a NAME-based chooser.
///
/// Issue #1233 adds a manual path and a mandatory Test gate. If auto-detect cannot reach a gateway it is NOT
/// treated as an error - the manual entry is revealed calmly so the person can type the gateway's computer
/// name and port (or paste a full address). Either way, an address must pass a real reachability Test (GET
/// /healthz) before Connect is allowed, so the install can never register this machine against an address it
/// has not proven reachable.
///
/// Until the join succeeds the step stays unverified, so the wizard keeps Next/Finish disabled. A cancelled
/// sign-in, no reachable gateway, a different account, or an unreachable gateway shows a clear message and
/// does NOT mark the step verified. The step raises <see cref="Connected"/> exactly once, when a local device
/// key is issued and persisted. This step is only shown on the Workstation path.
/// </summary>
public partial class GatewayConnectStep : UserControl
{
    private readonly GatewayAccountEnrollRunner _runner;
    private readonly string _deviceId;
    private readonly string _machineName;
    private CancellationTokenSource? _cts;

    // The gateway address the person entered manually AND that passed the reachability Test (issue #1233).
    // Null until a Test succeeds; cleared whenever the address fields change, so Connect is only ever enabled
    // for an address proven reachable in the current field state.
    private string? _manualTestedUrl;

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
    /// real browser sign-in, discovers the account's gateways, tests reachability, registers the account
    /// device, enrolls at the chosen gateway, and persists the issued local key to config.json.</summary>
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
                // Auto-detect could not find a reachable gateway. Issue #1233: this is NOT an error - offer
                // the manual entry calmly so the person can type the address themselves.
                RevealManualEntry(discovered.ErrorMessage
                    ?? "We could not find your gateway automatically. Enter its address below.");
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
            ShowRetryable("Connecting to the gateway failed unexpectedly. Please try again, or enter the address below.");
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

    // ===== Manual entry + Test gate (issue #1233) =====

    /// <summary>Reveal the manual-entry panel so the person can type the gateway address. Auto-detect stays
    /// available alongside it (the Sign-in button is re-enabled). An optional note explains why it appeared
    /// (for example, "no reachable gateway found") in a neutral tone - never as an error.</summary>
    private void RevealManualEntry(string? note)
    {
        if (!string.IsNullOrWhiteSpace(note))
            ManualNoteText.Text = note;
        WaitingPanel.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        ManualPanel.Visibility = Visibility.Visible;
        SignInButton.IsEnabled = true;
        ManualToggleButton.IsEnabled = true;
        TestButton.IsEnabled = true;
        ManualConnectButton.IsEnabled = _manualTestedUrl is not null;
    }

    private void ManualToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] ManualToggleButton_Click");
        RevealManualEntry(null);
        AddressBox.Focus();
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] TestButton_Click");
        try
        {
            var address = (AddressBox.Text ?? string.Empty).Trim();
            var isFullUrl = address.Contains("://", StringComparison.Ordinal);

            // A pasted full address carries its own port; a bare computer name needs a valid port here.
            var port = 0;
            if (!isFullUrl && !int.TryParse((PortBox.Text ?? string.Empty).Trim(), out port))
            {
                _manualTestedUrl = null;
                ManualConnectButton.IsEnabled = false;
                ShowTestResult("Enter a valid port number (for example 7878).", ok: false);
                return;
            }

            TestButton.IsEnabled = false;
            ShowTestResult("Testing...", ok: null);
            _cts = new CancellationTokenSource();

            var result = await _runner.TestGatewayAddressAsync(address, port, _cts.Token);
            if (result.Success && result.Value is not null)
            {
                _manualTestedUrl = result.Value;
                ManualConnectButton.IsEnabled = true;
                ShowTestResult("Reachable. Click Connect to continue.", ok: true);
            }
            else
            {
                _manualTestedUrl = null;
                ManualConnectButton.IsEnabled = false;
                ShowTestResult(result.ErrorMessage ?? "Could not reach a gateway at that address.", ok: false);
            }
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[GatewayConnectStep] TestButton_Click FAILED: {ex}");
            _manualTestedUrl = null;
            ManualConnectButton.IsEnabled = false;
            ShowTestResult("The test failed unexpectedly. Please try again.", ok: false);
        }
        finally
        {
            TestButton.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void ManualConnectButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] ManualConnectButton_Click");
        if (_manualTestedUrl is null)
        {
            ShowTestResult("Click Test first to check the address.", ok: false);
            return;
        }
        try
        {
            _cts = new CancellationTokenSource();
            EnterWaitingState("Connecting to your gateway...", cancellable: false);
            var result = await _runner.VerifyAndSaveAsync(_manualTestedUrl, _deviceId, _machineName, _cts.Token);
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[GatewayConnectStep] ManualConnectButton_Click FAILED: {ex}");
            ShowRetryable("Connecting to the gateway failed unexpectedly. Please try again.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Editing the address or port invalidates any prior successful Test: the person must Test again
    /// before Connect, so the install can never enroll against an address that has not been proven reachable
    /// in its CURRENT text. Guards null controls: this fires while XAML sets the default port during init.</summary>
    private void ManualField_TextChanged(object sender, TextChangedEventArgs e)
    {
        _manualTestedUrl = null;
        if (ManualConnectButton is not null) ManualConnectButton.IsEnabled = false;
        if (TestResultText is not null) TestResultText.Visibility = Visibility.Collapsed;
    }

    private void ShowTestResult(string message, bool? ok)
    {
        TestResultText.Text = message;
        TestResultText.Foreground = ok switch
        {
            true => (Brush)FindResource("SuccessBrush"),
            false => new SolidColorBrush(Color.FromRgb(0xE0, 0x56, 0x56)),
            _ => (Brush)FindResource("DimText"),
        };
        TestResultText.Visibility = Visibility.Visible;
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
    /// the action buttons, and enable Cancel only when the wait is cancellable (the sign-in wait).</summary>
    private void EnterWaitingState(string message, bool cancellable)
    {
        SignInButton.IsEnabled = false;
        ManualToggleButton.IsEnabled = false;
        TestButton.IsEnabled = false;
        ManualConnectButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Collapsed;
        SuccessPanel.Visibility = Visibility.Collapsed;
        ChooserPanel.Visibility = Visibility.Collapsed;
        WaitingText.Text = message;
        CancelButton.IsEnabled = cancellable;
        CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        WaitingPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Shows the name-based gateway chooser (issue #1206), hiding the waiting row. The manual panel is
    /// hidden here to keep the choice clear, but the "enter manually" link stays available.</summary>
    private void ShowChooser(IReadOnlyList<DiscoveredGateway> gateways)
    {
        SetupLog.Write($"[GatewayConnectStep] ShowChooser: {gateways.Count} gateways");
        WaitingPanel.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        ManualPanel.Visibility = Visibility.Collapsed;
        SignInButton.IsEnabled = false;
        ManualToggleButton.IsEnabled = true;
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
            ManualToggleButton.Visibility = Visibility.Collapsed;
            ChooserPanel.Visibility = Visibility.Collapsed;
            ManualPanel.Visibility = Visibility.Collapsed;
            SuccessText.Text = "Connected to the gateway. Click Next to continue.";
            SuccessPanel.Visibility = Visibility.Visible;
            Connected?.Invoke(this, EventArgs.Empty);
            return;
        }

        SetupLog.Write($"[GatewayConnectStep] ApplyResult: blocked - {result.ErrorMessage}");
        ShowRetryable(result.ErrorMessage ?? "The gateway did not accept the sign-in.");
    }

    /// <summary>Returns the step to a retryable state with a message: the chooser and waiting row are hidden,
    /// the Sign-in button is re-enabled, and the manual entry is offered (revealed) so the person can try a
    /// different address. The step stays UNVERIFIED, so the wizard keeps Next disabled and the install cannot
    /// complete.</summary>
    private void ShowRetryable(string message)
    {
        WaitingPanel.Visibility = Visibility.Collapsed;
        ChooserPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        SignInButton.IsEnabled = true;
        ManualToggleButton.IsEnabled = true;
        ManualPanel.Visibility = Visibility.Visible;
        TestButton.IsEnabled = true;
        ManualConnectButton.IsEnabled = _manualTestedUrl is not null;
    }
}
