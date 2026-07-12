using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.ControlApi;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The one reusable Gateway connection panel (design spec section 5). Phase 1 implements Step 1 - Connect:
/// on show it automatically scans for Gateways in the issue-1233 discovery order (this machine, tailnet,
/// local network), teaches with a plain-English intro, and lists every reachable one as a framed one-click
/// pick with a per-kind "when to use this" line. Exactly one pick carries a Recommended badge by the rule
/// This computer &gt; On your network &gt; Over Tailscale (Tailscale only when it is the sole find). Picking a
/// Gateway IS the test (decision 5): it writes the address, re-applies the Gateway config so the Director
/// runs the two-way nonce handshake, and shows live progress until the handshake either proves the
/// connection or fails with a named leg (decision 11, no fallback).
///
/// Verification is NOT rebuilt here (spec section 9): the panel drives the existing
/// <see cref="GatewayConnectionMonitor"/> through <see cref="ControlApiHost.ReapplyGatewayAsync"/> and reads
/// its earned verdict. Green is earned only by a completed handshake (decision 4). The visual language
/// matches the dictation card (spec section 5 visual direction): a #252526 card, a blue count pill, a green
/// Recommended accent, tinted icon tiles, and muted-blue guidance.
///
/// Phase 1 wires this into a temporary menu entry for testing; the Settings tab, the status box, and the
/// onboarding wizard adopt it in later phases (decision 8).
/// </summary>
public partial class GatewayConnectionPanel : UserControl
{
    // How long to wait for a handshake verdict before calling the attempt timed out.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    private static readonly FontFamily MonoFont = new("Cascadia Mono,Consolas,Courier New");

    // Icon geometries (24-unit viewbox, stretched into the 38px tile). A monitor for a local machine, a
    // globe for Tailscale (spec section 5 visual direction).
    private const string MonitorGeometry =
        "M4.5,4 H19.5 A1.5,1.5 0 0 1 21,5.5 V14.5 A1.5,1.5 0 0 1 19.5,16 H4.5 A1.5,1.5 0 0 1 3,14.5 "
        + "V5.5 A1.5,1.5 0 0 1 4.5,4 Z M8,20 H16 M12,16 V20";
    private const string GlobeGeometry =
        "M3,12 A9,9 0 1 1 21,12 A9,9 0 1 1 3,12 Z M3,12 H21 M12,3 C14.6,5.5 14.6,18.5 12,21 "
        + "M12,3 C9.4,5.5 9.4,18.5 12,21";

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

    // Cancels the Step 2 account-status polling loop when the panel is left or the flow restarts.
    private CancellationTokenSource? _pollCts;

    // Enroll is attempted at most once per sign-in flow, and NEVER from the poll loop, so status polling
    // can never mint keys repeatedly (guardrail against the #1136 auto-mint key leak).
    private bool _enrollAttempted;

    // Which step the panel opens on (spec section 6: the status-box click opens the panel on the resolver's
    // current step). Connect (the default) starts the auto-scan; SignIn/Done skip the scan and read the
    // signed-in state directly, because the handshake is already proven in those states.
    private readonly GatewayPanelStep _initialStep;

    public GatewayConnectionPanel() : this(GatewayPanelStep.Connect)
    {
    }

    public GatewayConnectionPanel(GatewayPanelStep initialStep)
    {
        _initialStep = initialStep;
        InitializeComponent();
        FileLog.Write($"[GatewayConnectionPanel] constructed (initialStep={initialStep})");
    }

    /// <summary>
    /// Raised once the two-way handshake proves Connected (Phase 4). Hosts that gate their own flow on
    /// a live connection - the onboarding wizard's Gateway step - listen for this instead of the deleted
    /// Test button's verdict.
    /// </summary>
    public event EventHandler? ConnectionVerified;

    /// <summary>
    /// Build a panel opened on the resolver's current step (spec section 6), for the three hosts that
    /// embed it (Settings Gateway tab, onboarding Gateway step, and the status-box window). Uses the cheap
    /// synchronous signal - the live handshake state - to choose the opening step: a proven handshake
    /// opens on the signed-in view (which itself reads account status and settles Step 2 vs Done), and
    /// anything else opens on Step 1 (the automatic scan).
    /// </summary>
    public static GatewayConnectionPanel CreateForCurrentState()
    {
        var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
        var step = host?.GatewayMonitor?.Status == GatewayConnectionStatus.Verified
            ? GatewayPanelStep.Done
            : GatewayPanelStep.Connect;
        return new GatewayConnectionPanel(step);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Open on the step the resolver pointed at (spec section 6). When the box already resolved to
        // signed-in or done, the handshake is proven - go straight to the signed-in view rather than
        // re-scanning from Step 1. Otherwise start the automatic scan (there is no Detect button, decision 5).
        if (_initialStep is GatewayPanelStep.SignIn or GatewayPanelStep.Done)
            _ = RefreshSignedInViewAsync();
        else
            StartScan();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopPolling();
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
        StopPolling();
        CountPillText.Text = "SCANNING...";
        ScanningProgress.IsVisible = true;
        ResultsArea.IsVisible = false;
        ShowOnly(ConnectPanel);
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

    private void RenderFound(IReadOnlyList<FoundGateway> found)
    {
        ScanningProgress.IsVisible = false;
        CountPillText.Text = $"{found.Count} FOUND";

        OptionsHost.Children.Clear();
        var recommended = RecommendedIndex(found);
        for (var i = 0; i < found.Count; i++)
            OptionsHost.Children.Add(BuildOptionRow(found[i], recommended == i));

        var any = found.Count > 0;
        NoneFoundText.IsVisible = !any;
        // When nothing was found, open the manual-entry section so the fallback is one step away.
        AdvancedToggle.IsChecked = !any;
        ResultsArea.IsVisible = true;
        ShowOnly(ConnectPanel);
        FileLog.Write($"[GatewayConnectionPanel] scan rendered: {found.Count} pick(s), recommended index {recommended}");
    }

    private void Rescan_Click(object? sender, RoutedEventArgs e) => StartScan();

    // The Recommended rule (spec section 5): the most stable LOCAL name reachable, in priority order This
    // computer > On your network > Over Tailscale. Tailscale wins only when it is the sole find, which the
    // priority ranking already yields (a local kind, if present, always outranks it).
    private static int RecommendedIndex(IReadOnlyList<FoundGateway> found)
    {
        var best = -1;
        var bestRank = int.MaxValue;
        for (var i = 0; i < found.Count; i++)
        {
            var rank = KindRank(found[i].Kind);
            if (rank < bestRank)
            {
                bestRank = rank;
                best = i;
            }
        }
        return best;
    }

    private static int KindRank(GatewayLocationKind kind) => kind switch
    {
        GatewayLocationKind.ThisMachine => 0,
        GatewayLocationKind.LocalNetwork => 1,
        GatewayLocationKind.Tailnet => 2,
        _ => 3,
    };

    // ---- Option row construction (built in code to match the mockup's mixed-emphasis copy) ------

    private Control BuildOptionRow(FoundGateway gateway, bool recommended)
    {
        var copy = CopyFor(gateway);

        var icon = new global::Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(copy.IconGeometry),
            Stroke = Brush(copy.StrokeColor),
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var iconTile = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(8),
            Background = Brush(copy.TileColor),
            Child = icon,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        Grid.SetColumn(iconTile, 0);

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameRow.Children.Add(new TextBlock
        {
            Text = copy.Name,
            Foreground = Brush("#E6E6E6"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        nameRow.Children.Add(new TextBlock
        {
            Text = copy.Address,
            FontFamily = MonoFont,
            Foreground = Brush("#888888"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (recommended)
            nameRow.Children.Add(BuildRecommendedBadge());

        var when = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#888888") };
        when.Inlines!.Add(new Run(copy.WhenPrefix));
        when.Inlines.Add(new Run(copy.WhenBold) { Foreground = Brush("#AAAAAA"), FontWeight = FontWeight.SemiBold });
        when.Inlines.Add(new Run(copy.WhenSuffix));

        var info = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(nameRow);
        info.Children.Add(when);
        Grid.SetColumn(info, 1);

        var chevron = new TextBlock
        {
            Text = ">",
            FontSize = 18,
            Foreground = Brush(recommended ? "#34D06E" : "#666666"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(chevron, 2);

        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        inner.Children.Add(iconTile);
        inner.Children.Add(info);
        inner.Children.Add(chevron);

        var card = new Border
        {
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(recommended ? "#2E7D50" : "#3C3C3C"),
            Background = recommended ? RecommendedBackground() : Brush("#252526"),
            Padding = new Thickness(15, 14),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = inner,
            Tag = gateway,
        };
        card.PointerPressed += Pick_PointerPressed;
        WireHover(card, recommended);

        // A green left-accent bar for the recommended row, overlaid on the card's left edge.
        var outer = new Grid();
        outer.Children.Add(card);
        if (recommended)
        {
            outer.Children.Add(new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(3),
                Background = Brush("#34D06E"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 8, 0, 8),
                IsHitTestVisible = false,
            });
        }
        return outer;
    }

    private static Border BuildRecommendedBadge() => new()
    {
        Background = Brush("#1B3A2A"),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = "RECOMMENDED",
            Foreground = Brush("#34D06E"),
            FontSize = 9.5,
            FontWeight = FontWeight.Bold,
        },
    };

    // Non-recommended cards get a subtle hover; the recommended card keeps its green wash.
    private static void WireHover(Border card, bool recommended)
    {
        if (recommended) return;
        card.PointerEntered += (_, _) =>
        {
            card.Background = Brush("#2E2E30");
            card.BorderBrush = Brush("#4A4A4A");
        };
        card.PointerExited += (_, _) =>
        {
            card.Background = Brush("#252526");
            card.BorderBrush = Brush("#3C3C3C");
        };
    }

    private static IBrush RecommendedBackground() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#242E7D50"), 0),
            new GradientStop(Color.Parse("#E6252526"), 0.34),
            new GradientStop(Color.Parse("#FF252526"), 1),
        },
    };

    // The per-kind name, address, and "when to use this" copy (spec section 5 - exact strings).
    private static OptionCopy CopyFor(FoundGateway gateway) => gateway.Kind switch
    {
        GatewayLocationKind.ThisMachine => new OptionCopy(
            Name: "This computer",
            Address: Environment.MachineName,
            WhenPrefix: "The Gateway runs on this machine. ",
            WhenBold: "Fastest and always available",
            WhenSuffix: " - the computer name does not change, so this keeps working.",
            TileColor: "#1F2A22",
            StrokeColor: "#34D06E",
            IconGeometry: MonitorGeometry),

        GatewayLocationKind.LocalNetwork => new OptionCopy(
            Name: "On your network",
            Address: SafeHost(gateway.Url),
            WhenPrefix: "The Gateway is on another computer on your network. The computer name rarely changes, so this keeps working - ",
            WhenBold: "best when you are on the same network",
            WhenSuffix: ".",
            TileColor: "#172433",
            StrokeColor: "#5AA9F0",
            IconGeometry: MonitorGeometry),

        _ => new OptionCopy(
            Name: "Over Tailscale",
            Address: SafeHost(gateway.Url),
            WhenPrefix: "Reaches the Gateway ",
            WhenBold: "from any network",
            WhenSuffix: ". Use this on a laptop you travel with, or from a remote office - anywhere you are not on the same network as the Gateway.",
            TileColor: "#172433",
            StrokeColor: "#5AA9F0",
            IconGeometry: GlobeGeometry),
    };

    private static string SafeHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    private readonly record struct OptionCopy(
        string Name, string Address, string WhenPrefix, string WhenBold, string WhenSuffix,
        string TileColor, string StrokeColor, string IconGeometry);

    // ---- Step 1b: pick / manual entry ----------------------------------------------------------

    private void Pick_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Border)?.Tag is FoundGateway pick)
            ConnectTo(pick.Url, pick.Label);
    }

    private void AdvancedToggle_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var open = AdvancedToggle.IsChecked == true;
        // Guard: this can fire during control construction, before the sibling fields are assigned.
        if (ManualEntryPanel is not null) ManualEntryPanel.IsVisible = open;
        if (AdvancedCaret is not null) AdvancedCaret.Text = open ? "^" : "v";
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
                OnHandshakeVerified();
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
            OnHandshakeVerified();
            return;
        }

        FileLog.Write("[GatewayConnectionPanel] connect timed out awaiting a handshake verdict");
        ShowFailure(
            "The Gateway did not finish the two-way connection in time. It may be unreachable, or it could "
            + "not reach this Director back (the callback leg).",
            "Check that the Gateway is running and reachable, or set the Director public URL under Advanced.");
    }

    // ---- Step 2: sign in with DevThrottle -------------------------------------------------------

    // The handshake proved Connected. Route to Step 2 (sign in) or the Done view based on the current
    // signed-in state, resolved via the same resolver the status box uses (spec section 4).
    private async void OnHandshakeVerified()
    {
        _connecting = false;
        FileLog.Write("[GatewayConnectionPanel] connected (handshake verified); resolving sign-in state");
        ConnectionVerified?.Invoke(this, EventArgs.Empty);
        await RefreshSignedInViewAsync();
    }

    // Read the account status + device-token presence, resolve, and show Step 2 or the Done view.
    private async Task RefreshSignedInViewAsync()
    {
        var config = GatewayConfig.Load();
        var account = await SafeStatusAsync(config, CancellationToken.None);

        var inputs = new GatewayConnectionInputs(
            GatewayConfigured: config.IsEnabled,
            Connection: GatewayConnectionVerification.Connected,
            FailedLeg: GatewayConnectionFailedLeg.None,
            WasEverConnected: true,
            DeviceKeyPresent: HasDeviceToken(config),
            Account: MapAccount(account));

        if (GatewayConnectionStateResolver.ResolveState(inputs) == GatewayConnectionState.AllGreen)
            ShowDone(config, account);
        else
            ShowSignIn();
    }

    private void ShowSignIn()
    {
        StopPolling();
        _enrollAttempted = false;
        SignInWaitRow.IsVisible = false;
        SignInErrorText.IsVisible = false;
        SignInButton.IsEnabled = true;
        ShowOnly(SignInPanel);
        FileLog.Write("[GatewayConnectionPanel] showing Step 2 (sign in)");
    }

    private void SignIn_Click(object? sender, RoutedEventArgs e) => _ = StartSignInAsync();

    private async Task StartSignInAsync()
    {
        SignInErrorText.IsVisible = false;
        SignInButton.IsEnabled = false;
        SignInWaitRow.IsVisible = true;
        SignInWaitText.Text = "Opening the DevThrottle sign-in in your browser...";

        try
        {
            await OpenDevThrottleSignInAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] sign-in launch failed: {ex.Message}");
            ShowSignInError("Could not open the sign-in page. " + ex.Message);
            return;
        }

        // Watch for the Gateway reporting signed in, then settle to Done. Enroll runs at most once and
        // NEVER from the poll loop itself (guardrail against the #1136 key leak).
        SignInWaitText.Text = "Waiting for you to sign in...";
        StartPolling();
    }

    // Open the DevThrottle sign-in front door in the system browser. The Gateway itself decides the
    // loopback-versus-remote redirect (AccountSignInStartEndpoint).
    private async Task OpenDevThrottleSignInAsync()
    {
        var config = GatewayConfig.Load();
        var baseUrl = global::CcDirector.Avalonia.CockpitUrlResolver.ResolveCockpitBase(config);
        var url = baseUrl.TrimEnd('/') + "/account/sign-in-start";
        await Task.Run(() => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }));
        FileLog.Write($"[GatewayConnectionPanel] opened DevThrottle sign-in: {url}");
    }

    private void ShowSignInError(string message)
    {
        StopPolling();
        SignInWaitRow.IsVisible = false;
        SignInButton.IsEnabled = true;
        SignInErrorText.Text = message;
        SignInErrorText.IsVisible = true;
    }

    private void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        _ = PollAccountAsync(_pollCts.Token);
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    // Poll GET /account/status until the Gateway reports signed in; then ensure this Director holds its
    // own device token (enrolling once through the Gateway) and settle to the Done view. The loop only
    // READS status - it never mints a key on its own.
    private async Task PollAccountAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var config = GatewayConfig.Load();
            var account = await SafeStatusAsync(config, ct);
            if (ct.IsCancellationRequested) return;

            if (account.SignedIn)
            {
                if (!HasDeviceToken(config) && !_enrollAttempted)
                {
                    _enrollAttempted = true;
                    await EnrollThroughGatewayOnceAsync();
                    config = GatewayConfig.Load();
                    account = await SafeStatusAsync(config, ct);
                }

                if (HasDeviceToken(config) && account.SignedIn)
                {
                    var finalConfig = config;
                    var finalAccount = account;
                    await Dispatcher.UIThread.InvokeAsync(() => ShowDone(finalConfig, finalAccount));
                    return;
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // Enroll THIS co-located Director through its Gateway now that the account is signed in: the Gateway
    // mints (or returns) this Director's own per-device token, which is stored locally. Called at most once
    // per sign-in flow and never from the poll loop's read path (guardrail against the #1136 key leak).
    private async Task EnrollThroughGatewayOnceAsync()
    {
        try
        {
            var host = (global::Avalonia.Application.Current as App)?.ControlApiHost;
            if (host is null)
            {
                FileLog.Write("[GatewayConnectionPanel] enroll-through-Gateway: Control API not running");
                return;
            }

            var config = GatewayConfig.Load();
            var result = await GatewayEnrollmentClient.EnrollSignedInAsync(
                config.Url, config.Token, host.DirectorId, Environment.MachineName, "windows");

            if (!result.Success || result.Value is null)
            {
                FileLog.Write($"[GatewayConnectionPanel] enroll-through-Gateway failed: {result.ErrorMessage}");
                return;
            }

            // Store this device's own token, then re-apply so the running client authenticates with it.
            await Task.Run(() => GatewayCredentialStore.SaveEnrolledKey(config.Url, result.Value.DeviceKey));
            await host.ReapplyGatewayAsync();
            FileLog.Write("[GatewayConnectionPanel] enroll-through-Gateway: this Director's token stored");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] enroll-through-Gateway error: {ex.Message}");
        }
    }

    private static async Task<GatewayAccountStatus> SafeStatusAsync(GatewayConfig config, CancellationToken ct)
    {
        try { return await new GatewayAccountStatusClient().GetStatusAsync(config, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayConnectionPanel] account status read failed: {ex.Message}");
            return GatewayAccountStatus.NotConfigured();
        }
    }

    private void ShowDone(GatewayConfig config, GatewayAccountStatus account)
    {
        StopPolling();
        var host = SafeHost(config.Url);
        DoneConnectedText.Text = string.IsNullOrWhiteSpace(host)
            ? "Connected to the Gateway"
            : $"Connected to Gateway on {host}";
        DoneSignedInText.Text = string.IsNullOrWhiteSpace(account.Email)
            ? "Signed in"
            : $"Signed in as {account.Email}";
        DoneMaskedToken.Text = MaskToken(config.Token);
        DoneGatewayUrl.Text = config.Url;
        ShowOnly(DonePanel);
        FileLog.Write("[GatewayConnectionPanel] showing Done (both checks green)");
    }

    private void DoneAdvancedToggle_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var open = DoneAdvancedToggle.IsChecked == true;
        if (DoneAdvancedPanel is not null) DoneAdvancedPanel.IsVisible = open;
        if (DoneAdvancedCaret is not null) DoneAdvancedCaret.Text = open ? "^" : "v";
    }

    // The token is shown masked, never in the clear (decision 7) - same masking as the old pairing dialog.
    private static string MaskToken(string? token)
        => !string.IsNullOrEmpty(token) && token.Length > 8
            ? token[..4] + "..." + token[^4..]
            : "********";

    // True when this device already holds its per-device token. GatewayConfig.Load resolves it from the
    // local credential file for a same-machine Gateway, so the config token is the complete check.
    private static bool HasDeviceToken(GatewayConfig config) => !string.IsNullOrWhiteSpace(config.Token);

    private static GatewayAccountSignInState MapAccount(GatewayAccountStatus status)
    {
        if (!status.GatewayConfigured) return GatewayAccountSignInState.Unknown;
        if (!status.Reachable) return GatewayAccountSignInState.Unavailable;
        return status.SignedIn ? GatewayAccountSignInState.SignedIn : GatewayAccountSignInState.SignedOut;
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
        ConnectPanel.IsVisible = ReferenceEquals(panel, ConnectPanel);
        ConnectingPanel.IsVisible = ReferenceEquals(panel, ConnectingPanel);
        SignInPanel.IsVisible = ReferenceEquals(panel, SignInPanel);
        DonePanel.IsVisible = ReferenceEquals(panel, DonePanel);
        FailedPanel.IsVisible = ReferenceEquals(panel, FailedPanel);
    }
}
