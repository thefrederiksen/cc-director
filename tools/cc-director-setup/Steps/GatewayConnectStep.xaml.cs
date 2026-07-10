using System.Windows;
using System.Windows.Controls;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup.Steps;

/// <summary>
/// The mandatory gateway-join step for a Workstation install (issues #646, #1198). A machine that joins
/// an existing fleet must connect to its gateway before the install can finish - the gateway is the
/// account authority, so a Workstation with no gateway connection is useless.
///
/// Joining is now DevThrottle account sign-in, not a 4-digit pairing code: the user enters the gateway
/// URL and clicks "Sign in to DevThrottle", which opens the system browser to sign in, registers this
/// machine as an account device, exchanges its key at the gateway for a local per-device key, and
/// persists the gateway URL + key to config.json (all in <see cref="GatewayAccountEnrollRunner"/>). The
/// URL stays because the cloud never learns a fleet's private network address; only the authorization
/// changed from a code to account sign-in (the same model the Gateway install already uses - epic #1069).
///
/// Until the join succeeds the step stays unverified, so the wizard keeps Next/Finish disabled (mirrors
/// the forced Sign-in gate, issue #657). A bad URL, a cancelled sign-in, a different account, or an
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
    /// real browser sign-in, registers the account device, enrolls at the gateway, and persists the issued
    /// local key to config.json.</summary>
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

        UrlBox.TextChanged += (_, _) => UpdateSignInEnabled();
        UpdateSignInEnabled();
        SetupLog.Write($"[GatewayConnectStep] Created: machine={_machineName}");
    }

    /// <summary>Sign in enables only once the gateway URL is a valid http/https address - there is nothing
    /// to sign in against without knowing which gateway to join.</summary>
    private void UpdateSignInEnabled()
    {
        var url = (UrlBox.Text ?? "").Trim();
        SignInButton.IsEnabled = Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] SignInButton_Click");
        try
        {
            EnterWaitingState();

            var url = (UrlBox.Text ?? "").Trim();
            _cts = new CancellationTokenSource();

            // The runner opens the browser and awaits the loopback hand-back, then does the HTTP calls and
            // the config.json write - all async, so the UI thread stays responsive and Cancel works.
            var result = await _runner.VerifyAndSaveAsync(url, _deviceId, _machineName, _cts.Token);

            ApplyResult(result);
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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SetupLog.Write("[GatewayConnectStep] CancelButton_Click: cancelling the sign-in wait");
        // Cancelling the wait makes VerifyAndSaveAsync return a cancelled result, which ApplyResult turns
        // into a retryable state. The button itself stays disabled until the wait unwinds.
        CancelButton.IsEnabled = false;
        _cts?.Cancel();
    }

    /// <summary>Shows the "waiting for sign-in..." state: hide any prior message/success, show the waiting
    /// row with an enabled Cancel, and disable the inputs while the call is in flight.</summary>
    private void EnterWaitingState()
    {
        SignInButton.IsEnabled = false;
        UrlBox.IsEnabled = false;
        StatusText.Visibility = Visibility.Collapsed;
        SuccessPanel.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        WaitingPanel.Visibility = Visibility.Visible;
    }

    private void ApplyResult(OperationResult<MobileEnrollmentResponse> result)
    {
        WaitingPanel.Visibility = Visibility.Collapsed;

        if (result.Success && result.Value is not null)
        {
            SetupLog.Write("[GatewayConnectStep] ApplyResult: connected");
            IsVerified = true;
            SignInButton.Visibility = Visibility.Collapsed;
            UrlBox.IsEnabled = false;
            SuccessText.Text = "Connected to the gateway. Click Next to continue.";
            SuccessPanel.Visibility = Visibility.Visible;
            Connected?.Invoke(this, EventArgs.Empty);
            return;
        }

        SetupLog.Write($"[GatewayConnectStep] ApplyResult: blocked - {result.ErrorMessage}");
        ShowRetryable(result.ErrorMessage ?? "The gateway did not accept the sign-in.");
    }

    /// <summary>Returns the step to a retryable state with a message - the URL box and Sign-in button are
    /// re-enabled so the user can correct the URL and try again. The step stays UNVERIFIED, so the wizard
    /// keeps Next disabled and the install cannot complete.</summary>
    private void ShowRetryable(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        UrlBox.IsEnabled = true;
        UpdateSignInEnabled();
    }
}
