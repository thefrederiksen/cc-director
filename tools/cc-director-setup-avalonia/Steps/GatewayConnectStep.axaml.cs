using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

/// <summary>
/// The OPTIONAL gateway-join step for the cross-platform (macOS) wizard - the Avalonia sibling of
/// the Windows wizard's mandatory GatewayConnectStep. It drives the same platform-neutral
/// <see cref="GatewayAccountEnrollRunner"/>: sign in with the DevThrottle account, discover the
/// account's gateways automatically, or type an address manually and prove it reachable with a
/// mandatory Test before Connect is allowed. On success the gateway address and the issued local
/// device key are persisted to config.json, where the Director AND the launcher both read them.
///
/// Unlike the Windows step this one is SKIPPABLE (the CC Launcher mission, Architect ruling
/// 2026-07-11): a machine being set up without a gateway must still complete the install cleanly,
/// so the wizard's Next button stays enabled and reads "Skip" until the join succeeds.
/// </summary>
public partial class GatewayConnectStep : UserControl
{
    private readonly GatewayAccountEnrollRunner _runner;
    private readonly string _deviceId;
    private readonly string _machineName;
    private CancellationTokenSource? _cts;

    // The address the person typed AND that passed the reachability Test. Null until a Test
    // succeeds; cleared whenever the address fields change, so Connect is only ever enabled for
    // an address proven reachable in the current field state (same contract as the Windows step).
    private string? _manualTestedUrl;

    /// <summary>True once the gateway issued a local device key and it was persisted, or the
    /// machine was already connected when the step opened. Once true it stays true.</summary>
    public bool IsVerified { get; private set; }

    /// <summary>Raised when the join succeeds, so the wizard can relabel Skip to Next.</summary>
    public event EventHandler? Connected;

    public GatewayConnectStep() : this(runner: null)
    {
    }

    /// <summary>Constructor seam so a test or proof harness can inject a runner (for example one
    /// with a fake sign-in and a fake HTTP handler).</summary>
    public GatewayConnectStep(GatewayAccountEnrollRunner? runner)
    {
        InitializeComponent();
        _runner = runner ?? new GatewayAccountEnrollRunner();
        // The installer has no running Director yet, so it mints a stable device id for this
        // machine, used both as the account-registration install id and the enroll device id.
        _deviceId = Guid.NewGuid().ToString();
        _machineName = Environment.MachineName;
        SetupLog.Write($"[GatewayConnectStep] Created: machine={_machineName}");

        // An update or repair run on an already-connected machine has nothing to do here.
        var existing = GatewayConfig.Load();
        if (existing.IsEnabled)
        {
            SetupLog.Write($"[GatewayConnectStep] Already connected: {existing.Url}");
            AlreadyConnectedText.Text = $"This machine is already connected to its gateway ({existing.Url}).";
            AlreadyConnectedPanel.IsVisible = true;
            ActionPanel.IsVisible = false;
            MarkVerified();
        }
    }

    private async void SignInButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] SignInButton_Click");
        try
        {
            EnterWaitingState("Waiting for you to sign in in your browser...", cancellable: true);
            _cts = new CancellationTokenSource();

            var discovered = await _runner.SignInAndDiscoverGatewaysAsync(_cts.Token);
            if (!discovered.Success || discovered.Value is null)
            {
                // Auto-detect could not find a reachable gateway: not an error - reveal the
                // manual entry calmly so the person can type the address themselves.
                RevealManualEntry(discovered.ErrorMessage
                    ?? "We could not find your gateway automatically. Enter its address below.");
                return;
            }

            var gateways = discovered.Value;
            if (gateways.Count == 1)
            {
                await ConnectToGatewayAsync(gateways[0]);
                return;
            }

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

    private async void ChooseConnectButton_Click(object? sender, RoutedEventArgs e)
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

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] CancelButton_Click");
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* the wait already ended */ }
    }

    private void ManualToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] ManualToggleButton_Click");
        RevealManualEntry(null);
        AddressBox.Focus();
    }

    private async void TestButton_Click(object? sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] TestButton_Click");
        try
        {
            var address = (AddressBox.Text ?? string.Empty).Trim();
            var isFullUrl = address.Contains("://", StringComparison.Ordinal);

            // A pasted full address carries its own port; a bare computer name needs a valid port.
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

    private async void ManualConnectButton_Click(object? sender, RoutedEventArgs e)
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

    /// <summary>Editing the address or port invalidates any prior successful Test, so Connect is
    /// only ever enabled for an address proven reachable in its current text. Guards null
    /// controls: this fires while the markup sets the default port during initialization.</summary>
    private void ManualField_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (ManualConnectButton is null) return;
        _manualTestedUrl = null;
        ManualConnectButton.IsEnabled = false;
        if (TestResultText is not null) TestResultText.Text = "";
    }

    /// <summary>Registers this machine on the account and enrolls it at the discovered gateway.</summary>
    private async Task ConnectToGatewayAsync(DiscoveredGateway gateway)
    {
        SetupLog.Write($"[GatewayConnectStep] ConnectToGatewayAsync: gateway={gateway.Name}");
        EnterWaitingState("Connecting to your gateway...", cancellable: false);

        var token = _cts?.Token ?? CancellationToken.None;
        var result = await _runner.EnrollWithDiscoveredGatewayAsync(gateway.EndpointUrl, _deviceId, _machineName, token);
        ApplyResult(result);
    }

    private void ApplyResult(OperationResult<MobileEnrollmentResponse> result)
    {
        WaitingPanel.IsVisible = false;
        if (result.Success)
        {
            SetupLog.Write("[GatewayConnectStep] ApplyResult: connected");
            ActionPanel.IsVisible = false;
            ChooserPanel.IsVisible = false;
            ManualPanel.IsVisible = false;
            StatusText.Text = "Connected. This machine is now registered with your gateway.";
            StatusText.Foreground = SolidColorBrush.Parse("#22C55E");
            StatusText.IsVisible = true;
            MarkVerified();
        }
        else
        {
            SetupLog.Write($"[GatewayConnectStep] ApplyResult: FAILED: {result.ErrorMessage}");
            ShowRetryable(result.ErrorMessage ?? "The gateway did not accept this machine.");
        }
    }

    private void MarkVerified()
    {
        if (IsVerified) return;
        IsVerified = true;
        Connected?.Invoke(this, EventArgs.Empty);
    }

    private void EnterWaitingState(string message, bool cancellable)
    {
        WaitingText.Text = message;
        WaitingPanel.IsVisible = true;
        CancelButton.IsVisible = cancellable;
        StatusText.IsVisible = false;
        ChooserPanel.IsVisible = false;
        SignInButton.IsEnabled = false;
        ManualToggleButton.IsEnabled = false;
    }

    private void ShowChooser(IReadOnlyList<DiscoveredGateway> gateways)
    {
        SetupLog.Write($"[GatewayConnectStep] ShowChooser: {gateways.Count} gateways");
        WaitingPanel.IsVisible = false;
        GatewayList.ItemsSource = gateways;
        GatewayList.SelectedIndex = 0;
        ChooserPanel.IsVisible = true;
        SignInButton.IsEnabled = true;
        ManualToggleButton.IsEnabled = true;
    }

    /// <summary>Reveal the manual-entry panel. Auto-detect stays available alongside it. The
    /// optional note explains why it appeared, in a neutral tone - never as an error.</summary>
    private void RevealManualEntry(string? note)
    {
        if (!string.IsNullOrWhiteSpace(note))
            ManualNoteText.Text = note;
        WaitingPanel.IsVisible = false;
        StatusText.IsVisible = false;
        ManualPanel.IsVisible = true;
        SignInButton.IsEnabled = true;
        ManualToggleButton.IsEnabled = true;
        ManualConnectButton.IsEnabled = _manualTestedUrl is not null;
    }

    private void ShowRetryable(string message)
    {
        WaitingPanel.IsVisible = false;
        StatusText.Text = message;
        StatusText.Foreground = SolidColorBrush.Parse("#F87171");
        StatusText.IsVisible = true;
        SignInButton.IsEnabled = true;
        ManualToggleButton.IsEnabled = true;
    }

    private void ShowTestResult(string message, bool? ok)
    {
        TestResultText.Text = message;
        TestResultText.Foreground = ok switch
        {
            true => SolidColorBrush.Parse("#22C55E"),
            false => SolidColorBrush.Parse("#F87171"),
            null => SolidColorBrush.Parse("#888888"),
        };
    }
}
