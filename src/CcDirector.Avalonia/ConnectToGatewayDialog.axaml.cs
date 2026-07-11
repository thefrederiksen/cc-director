using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Avalonia;

/// <summary>
/// The Director's "Connect to Gateway" dialog. The flow is: enter the Gateway URL, optionally
/// Test that it is reachable, then "Sign in &amp; Connect" - which opens the system browser to sign
/// in to the DevThrottle account and enrolls this machine at the Gateway (the same
/// <see cref="GatewayAccountEnrollRunner"/> the installer uses). On success the per-device key and
/// <c>streamMode</c> are written to config.json, so the Director joins over the live stream once the
/// caller re-applies the gateway. No 4-digit pairing code - the account sign-in replaces it.
/// </summary>
public partial class ConnectToGatewayDialog : Window
{
    private readonly string _deviceId;
    private readonly string _machineName;
    private CancellationTokenSource? _signInCts;

    private static readonly IBrush OkBrush = Brush.Parse("#22C55E");
    private static readonly IBrush ErrBrush = Brush.Parse("#F14C4C");
    private static readonly IBrush DimBrush = Brush.Parse("#AAAAAA");

    public ConnectToGatewayDialog() : this("", "") { }

    public ConnectToGatewayDialog(string deviceId, string prefillUrl)
    {
        _deviceId = deviceId ?? "";
        _machineName = Environment.MachineName;
        InitializeComponent();
        FileLog.Write($"[ConnectToGatewayDialog] open: deviceId={_deviceId}, machine={_machineName}");

        if (!string.IsNullOrWhiteSpace(prefillUrl))
            UrlBox.Text = prefillUrl;
    }

    private string Url => (UrlBox.Text ?? "").Trim();

    /// <summary>Test button: probe the Gateway's <c>/healthz</c> so the user confirms the URL is a
    /// reachable Gateway before signing in. Purely informational; it does not gate the sign-in.</summary>
    private async void BtnTest_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ConnectToGatewayDialog] Test clicked");
        try
        {
            if (!IsValidUrl(Url, out var reason))
            {
                ShowTestResult(false, reason);
                return;
            }

            TestButton.IsEnabled = false;
            ShowTestResult(null, "Testing the connection...");
            var (ok, detail) = await ProbeGatewayAsync(Url);
            ShowTestResult(ok, detail);
            FileLog.Write($"[ConnectToGatewayDialog] Test result: ok={ok}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectToGatewayDialog] Test FAILED: {ex.Message}");
            ShowTestResult(false, "The test could not run. See the logs.");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>Sign in &amp; Connect: run the full account-sign-in enrollment (browser sign-in -&gt;
    /// register device -&gt; Gateway <c>/m/enroll</c> -&gt; persist url + per-device key + streamMode). The
    /// browser opens; the user completes the sign-in there. Cancellable while in flight.</summary>
    private async void BtnSignIn_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ConnectToGatewayDialog] Sign in & Connect clicked");
        try
        {
            if (!IsValidUrl(Url, out var reason))
            {
                ShowStatus(reason, ErrBrush);
                return;
            }

            SignInButton.IsEnabled = false;
            TestButton.IsEnabled = false;
            UrlBox.IsEnabled = false;
            CancelButton.Content = "Cancel sign-in";
            ShowStatus("Opening your browser to sign in to DevThrottle. Complete the sign-in there, then return here.", DimBrush);

            _signInCts = new CancellationTokenSource();
            var runner = new GatewayAccountEnrollRunner();
            var result = await runner.VerifyAndSaveAsync(Url, _deviceId, _machineName, _signInCts.Token);

            if (!result.Success)
            {
                FileLog.Write($"[ConnectToGatewayDialog] enrollment failed: {result.ErrorMessage}");
                ShowStatus(result.ErrorMessage ?? "Connection failed.", ErrBrush);
                ResetAfterFailure();
                return;
            }

            ShowSuccess();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConnectToGatewayDialog] Sign in FAILED: {ex}");
            ShowStatus("Connecting failed unexpectedly. See the logs.", ErrBrush);
            ResetAfterFailure();
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        // A Cancel while a sign-in is in flight aborts the sign-in rather than closing the dialog,
        // so an abandoned browser sign-in is never a dead end.
        if (_signInCts is not null && !_signInCts.IsCancellationRequested)
        {
            FileLog.Write("[ConnectToGatewayDialog] sign-in cancel requested");
            _signInCts.Cancel();
            return;
        }
        FileLog.Write("[ConnectToGatewayDialog] cancelled");
        Close(false);
    }

    private void BtnDone_Click(object? sender, RoutedEventArgs e) => Close(true);

    // ---- helpers (no try/catch except the network probe, whose failure IS the result) ----

    private static bool IsValidUrl(string url, out string reason)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            reason = "Enter the Gateway URL.";
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            reason = "Enter a valid URL like https://your-gateway.ts.net:7878";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>GET <c>{url}/healthz</c> with a short timeout. Any answer (2xx, or even a 401 on a
    /// locked-down build) proves a Gateway is responding at that URL. A transport failure is the
    /// expected "not reachable" outcome, so it is caught and returned as the result - mirroring
    /// <c>GatewayEnrollmentClient</c>'s own transport handling, not hiding a bug.</summary>
    private static async Task<(bool ok, string detail)> ProbeGatewayAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        try
        {
            using var resp = await http.GetAsync(url.TrimEnd('/') + "/healthz");
            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Unauthorized)
                return (true, "Gateway is reachable. Sign in to connect.");
            return (false, $"The gateway answered HTTP {(int)resp.StatusCode}. Check the URL.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not reach a gateway at that URL: {ex.Message}");
        }
    }

    // null = in-progress (neutral), true = reachable (green), false = problem (red).
    private void ShowTestResult(bool? ok, string text)
    {
        TestResultBorder.IsVisible = true;
        if (ok is null)
        {
            TestResultBorder.Background = Brush.Parse("#1E1E1E");
            TestResultBorder.BorderBrush = Brush.Parse("#3C3C3C");
            TestResultIcon.Text = "…"; // ellipsis
            TestResultIcon.Foreground = DimBrush;
            TestResultText.Foreground = DimBrush;
        }
        else if (ok.Value)
        {
            TestResultBorder.Background = Brush.Parse("#1B3A2A");
            TestResultBorder.BorderBrush = OkBrush;
            TestResultIcon.Text = "✓"; // check
            TestResultIcon.Foreground = OkBrush;
            TestResultText.Foreground = Brush.Parse("#C6F0D0");
        }
        else
        {
            TestResultBorder.Background = Brush.Parse("#3A1E1E");
            TestResultBorder.BorderBrush = ErrBrush;
            TestResultIcon.Text = "✕"; // cross
            TestResultIcon.Foreground = ErrBrush;
            TestResultText.Foreground = Brush.Parse("#F0C6C6");
        }
        TestResultText.Text = text;
    }

    private void ShowStatus(string text, IBrush brush)
    {
        StatusText.IsVisible = true;
        StatusText.Foreground = brush;
        StatusText.Text = text;
    }

    private void ResetAfterFailure()
    {
        _signInCts?.Dispose();
        _signInCts = null;
        SignInButton.IsEnabled = true;
        TestButton.IsEnabled = true;
        UrlBox.IsEnabled = true;
        CancelButton.Content = "Cancel";
    }

    private void ShowSuccess()
    {
        FileLog.Write($"[ConnectToGatewayDialog] connected as {_machineName}");
        _signInCts?.Dispose();
        _signInCts = null;
        FormPanel.IsVisible = false;
        SuccessPanel.IsVisible = true;
        SignInButton.IsVisible = false;
        CancelButton.IsVisible = false;
        DoneButton.IsVisible = true;
        SuccessTitleText.Text = $"Connected as {_machineName}";
    }
}
