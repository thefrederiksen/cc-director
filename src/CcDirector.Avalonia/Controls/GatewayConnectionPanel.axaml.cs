using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The one reusable Gateway connection panel (design spec section 5). Phase 1 implements Step 1 - Connect:
/// on show it automatically scans for Gateways in the issue-1233 discovery order (this machine, tailnet,
/// local network), lists every reachable one as a one-click pick, and offers manual entry under a collapsed
/// Advanced section. Picking a Gateway IS the test (decision 5): it writes the address, re-applies the
/// Gateway config so the Director runs the two-way nonce handshake, and shows live progress until the
/// handshake either proves the connection or fails with a named leg (decision 11, no fallback).
///
/// Verification is NOT rebuilt here (spec section 9): the panel drives the existing
/// <see cref="GatewayConnectionMonitor"/> through <see cref="ControlApiHost.ReapplyGatewayAsync"/> and reads
/// its earned verdict. Green is earned only by a completed handshake (decision 4).
///
/// Phase 1 wires this into a temporary menu entry for testing; the Settings tab, the status box, and the
/// onboarding wizard adopt it in later phases (decision 8).
/// </summary>
public partial class GatewayConnectionPanel : UserControl
{
    // How long to wait for a handshake verdict before calling the attempt timed out.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    private readonly GatewayScanService _scan = new();

    private GatewayConnectionMonitor? _monitor;
    private bool _subscribed;

    // True only while an initiated connect is awaiting a verdict, so background monitor changes never
    // yank the panel off another view.
    private bool _connecting;

    // Distinguishes verdicts of the current attempt from a stale one; bumped on every connect.
    private int _attemptId;

    // The last address tried, so "Try again" repeats it.
    private (string Url, string Label)? _lastAttempt;

    public GatewayConnectionPanel()
    {
        InitializeComponent();
        FileLog.Write("[GatewayConnectionPanel] constructed");
    }

    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Automatic scan on show - there is no Detect button (decision 5).
        StartScan();
    }

    protected override void OnDetachedFromVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed && _monitor is not null)
        {
            _monitor.Changed -= OnMonitorChanged;
            _subscribed = false;
        }
    }

    // ---- Step 1a: scan --------------------------------------------------------------------------

    private async void StartScan()
    {
        _connecting = false;
        ShowOnly(ScanningPanel);
        try
        {
            var found = await _scan.ScanAsync();
            RenderFound(found);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] scan failed: {ex.Message}");
            RenderFound(Array.Empty<FoundGateway>());
        }
    }

    private void RenderFound(System.Collections.Generic.IReadOnlyList<FoundGateway> found)
    {
        FoundList.ItemsSource = found;
        var any = found.Count > 0;
        FoundListSection.IsVisible = any;
        NoneFoundText.IsVisible = !any;
        // When nothing was found, open the manual-entry section so the fallback is one step away.
        AdvancedExpander.IsExpanded = !any;
        ShowOnly(FoundPanel);
        FileLog.Write($"[GatewayConnectionPanel] scan rendered: {found.Count} pick(s)");
    }

    private void Rescan_Click(object? sender, RoutedEventArgs e) => StartScan();

    // ---- Step 1b: pick / manual entry ----------------------------------------------------------

    private void Pick_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is FoundGateway pick)
            ConnectTo(pick.Url, pick.Label);
    }

    private void ManualConnect_Click(object? sender, RoutedEventArgs e)
    {
        ManualErrorText.IsVisible = false;
        var raw = ManualUrlBox.Text ?? string.Empty;
        if (!GatewayAddress.TryNormalize(raw, out var url, out var error))
        {
            ManualErrorText.Text = error ?? "That is not a valid address.";
            ManualErrorText.IsVisible = true;
            return;
        }
        ConnectTo(url, url);
    }

    // ---- Step 1c/d/e: connect (the click IS the test) ------------------------------------------

    private async void ConnectTo(string url, string label)
    {
        var attempt = ++_attemptId;
        _lastAttempt = (url, label);
        _connecting = true;

        ConnectingTitle.Text = $"Connecting to {label}...";
        LegReachMarker.Text = "[.]";
        LegCallbackMarker.Text = "[.]";
        ShowOnly(ConnectingPanel);

        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        if (host is null)
        {
            ShowFailure("The Control API is not running yet, so this Director cannot connect.",
                "Wait for the Director to finish starting, then try again.");
            return;
        }

        try
        {
            // Write the chosen address, then re-apply so the Director runs a fresh handshake against it.
            await Task.Run(() => CcDirectorConfigService.MergePatch(new JsonObject
            {
                ["gateway"] = new JsonObject { ["url"] = url },
            }));

            EnsureSubscribed(host.GatewayMonitor);
            await host.ReapplyGatewayAsync();
            _ = TimeoutAsync(attempt);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] ConnectTo FAILED to start: {ex.Message}");
            ShowFailure($"Could not start connecting: {ex.Message}", null);
        }
    }

    private void EnsureSubscribed(GatewayConnectionMonitor monitor)
    {
        _monitor = monitor;
        if (_subscribed) return;
        monitor.Changed += OnMonitorChanged;
        _subscribed = true;
    }

    private void OnMonitorChanged()
    {
        var monitor = _monitor;
        if (monitor is null) return;
        // Changed may fire on any thread; settle the UI on the UI thread.
        Dispatcher.UIThread.Post(() => ApplyVerdict(monitor));
    }

    private void ApplyVerdict(GatewayConnectionMonitor monitor)
    {
        // Only an initiated attempt drives the view; ignore background heartbeat churn otherwise.
        if (!_connecting) return;

        switch (monitor.Status)
        {
            case GatewayConnectionStatus.Verified:
                ShowConnected(_lastAttempt?.Label);
                break;
            case GatewayConnectionStatus.Failed:
            case GatewayConnectionStatus.NoTailnetIdentity:
                ShowFailure(monitor.FailureSummary ?? "The connection could not be completed.", DeriveFix(monitor));
                break;
            case GatewayConnectionStatus.Connecting:
            case GatewayConnectionStatus.NotConfigured:
                // Stay on the connecting view until a final verdict arrives (or the timeout fires).
                break;
        }
    }

    private async Task TimeoutAsync(int attempt)
    {
        try { await Task.Delay(ConnectTimeout); }
        catch { /* ignored */ }

        if (attempt != _attemptId || !_connecting) return; // superseded or already settled

        var monitor = _monitor;
        if (monitor?.Status == GatewayConnectionStatus.Verified)
        {
            ShowConnected(_lastAttempt?.Label);
            return;
        }

        FileLog.Write("[GatewayConnectionPanel] connect timed out awaiting a handshake verdict");
        ShowFailure(
            "The Gateway did not finish the two-way connection in time. It may be unreachable, or it could "
            + "not reach this Director back (the callback leg).",
            "Check that the Gateway is running and reachable, or set the Director public URL under Advanced.");
    }

    private void ShowConnected(string? label)
    {
        _connecting = false;
        ConnectedText.Text = string.IsNullOrWhiteSpace(label)
            ? "Connected to the Gateway"
            : $"Connected to {label}";
        ShowOnly(ConnectedPanel);
        FileLog.Write("[GatewayConnectionPanel] connected (handshake verified)");
    }

    private void ShowFailure(string summary, string? fix)
    {
        _connecting = false;
        FailureSummaryText.Text = summary;
        if (string.IsNullOrWhiteSpace(fix))
        {
            FailureFixText.IsVisible = false;
        }
        else
        {
            FailureFixText.Text = "Fix: " + fix;
            FailureFixText.IsVisible = true;
        }
        ShowOnly(FailedPanel);
        FileLog.Write($"[GatewayConnectionPanel] connect failed: {summary}");
    }

    private void TryAgain_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastAttempt is { } attempt)
            ConnectTo(attempt.Url, attempt.Label);
        else
            StartScan();
    }

    // Name the fix for the failing leg (decision 11) from the monitor's already-named failure summary.
    private static string? DeriveFix(GatewayConnectionMonitor monitor)
    {
        if (monitor.Status == GatewayConnectionStatus.NoTailnetIdentity)
            return "Start Tailscale on this machine, or set the Director public URL under Advanced.";

        var summary = monitor.FailureSummary ?? string.Empty;
        if (summary.Contains("callback", StringComparison.OrdinalIgnoreCase))
            return "Make sure this Director's port is reachable from the Gateway host, or set the Director "
                 + "public URL under Advanced.";
        if (summary.Contains("Cannot reach the Gateway", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("reach the Gateway", StringComparison.OrdinalIgnoreCase))
            return "Make sure the Gateway is running and reachable at that address.";
        return null;
    }

    // Show exactly one of the step sub-panels.
    private void ShowOnly(Control panel)
    {
        ScanningPanel.IsVisible = ReferenceEquals(panel, ScanningPanel);
        FoundPanel.IsVisible = ReferenceEquals(panel, FoundPanel);
        ConnectingPanel.IsVisible = ReferenceEquals(panel, ConnectingPanel);
        ConnectedPanel.IsVisible = ReferenceEquals(panel, ConnectedPanel);
        FailedPanel.IsVisible = ReferenceEquals(panel, FailedPanel);
    }
}
