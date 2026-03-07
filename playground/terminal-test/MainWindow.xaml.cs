using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Terminal;
using CcDirector.Terminal.Rendering;

namespace TerminalTest;

public partial class MainWindow : Window
{
    // Live mode: uses real TerminalControl + Session (same as cc-director)
    private TerminalControl? _terminalControl;
    private Session? _session;

    // Test mode: uses standalone TerminalView with synthetic data
    private TerminalView? _terminalView;

    // Current mode
    private string _currentMode = "Live";

    // Auto-capture: --capture --delay N --output path.png
    private bool _autoCapture;
    private int _autoCaptureDelay = 10;
    private string _autoCaptureOutput = "capture.png";

    public MainWindow()
    {
        InitializeComponent();
        ParseArgs();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void ParseArgs()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--test":
                    _currentMode = "Test";
                    break;
                case "--capture":
                    _autoCapture = true;
                    break;
                case "--delay" when i + 1 < args.Length:
                    int.TryParse(args[++i], out _autoCaptureDelay);
                    break;
                case "--output" when i + 1 < args.Length:
                    _autoCaptureOutput = args[++i];
                    break;
            }
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentMode == "Test")
                ActivateTestMode();
            else
                ActivateLiveMode();

            if (_autoCapture)
            {
                TxtStatus.Text = $"Auto-capture in {_autoCaptureDelay}s...";
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(_autoCaptureDelay)
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    DoCapture(_autoCaptureOutput);
                    TxtStatus.Text = $"Captured: {_autoCaptureOutput}";
                    Close();
                };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"FAILED: {ex.Message}";
        }
    }

    /// <summary>
    /// Live mode: Uses the REAL TerminalControl with a Session backed by ConPtyBackend.
    /// Creates the backend + session the same way cc-director's SessionManager does,
    /// matching the exact Attach() code path.
    /// </summary>
    private void ActivateLiveMode()
    {
        CleanupTestMode();
        _currentMode = "Live";

        // Create TerminalControl exactly like cc-director does
        _terminalControl = new TerminalControl();
        _terminalControl.RendererBackgroundChanged += color =>
        {
            TerminalArea.Background = new SolidColorBrush(color);
        };

        // Add to visual tree BEFORE attach (same as cc-director)
        TerminalArea.Child = _terminalControl;

        // Set renderer (same as cc-director's ApplyRendererToTerminal)
        _terminalControl.SetRenderer(new ProRenderer());

        // Create ConPtyBackend + Session directly (same as SessionManager.CreateSession does)
        string exe = FindClaude();
        string args = "--dangerously-skip-permissions";
        string workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var backend = new ConPtyBackend();
        var envVars = new Dictionary<string, string>
        {
            ["CC_SESSION_ID"] = Guid.NewGuid().ToString()
        };
        backend.Start(exe, args, workDir, 120, 30, envVars);

        _session = new Session(
            Guid.NewGuid(), workDir, workDir, args,
            backend, SessionBackendType.ConPty);
        _session.MarkRunning();

        // Attach -- this is the EXACT call that triggers the deferred attach path
        _terminalControl.Attach(_session);

        TxtStatus.Text = $"Live | PID: {_session.ProcessId}";

        // Auto-send Enter after 2s to get past the trust prompt
        var enterTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        enterTimer.Tick += (_, _) =>
        {
            enterTimer.Stop();
            _session?.Backend.Write(new byte[] { 0x0D }); // Enter
        };
        enterTimer.Start();

        // Diagnostic: show buffer state
        var diagTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        diagTimer.Tick += (_, _) =>
        {
            if (_session?.Buffer == null) return;
            var (data, pos) = _session.Buffer.GetWrittenSince(0);
            var status = _session.Backend.IsRunning ? "Running" : "Exited";
            TxtStatus.Text = $"Live | PID: {_session.ProcessId} | {status} | buf={data.Length}B pos={pos}";
        };
        diagTimer.Start();
    }

    /// <summary>
    /// Test mode: Uses standalone TerminalView with synthetic ANSI data.
    /// Good for testing rendering/colors, but does NOT test the Attach() path.
    /// </summary>
    private void ActivateTestMode()
    {
        CleanupLiveMode();
        _currentMode = "Test";

        _terminalView = new TerminalView();
        _terminalView.RendererBackgroundChanged += color =>
        {
            TerminalArea.Background = new SolidColorBrush(color);
        };
        TerminalArea.Child = _terminalView;
        _terminalView.SetRenderer(new ProRenderer());

        FeedTestData();
        TxtStatus.Text = "Test mode | Synthetic data";
    }

    private void FeedTestData()
    {
        if (_terminalView == null) return;

        var testData = new StringBuilder();

        // Normal text
        testData.Append("Hello Terminal! Normal text on line 1\r\n");

        // Bold green, red, blue on white
        testData.Append("\x1b[1;32mBold Green Text\x1b[0m  ");
        testData.Append("\x1b[31mRed Text\x1b[0m  ");
        testData.Append("\x1b[34;47mBlue on White\x1b[0m\r\n");

        // Reverse video (status bar test)
        testData.Append("\x1b[7m Status Bar (reverse video) \x1b[0m\r\n");

        // 256-color test
        testData.Append("256-color: ");
        for (int i = 0; i < 16; i++)
            testData.Append($"\x1b[38;5;{i}m#{i:D2} ");
        testData.Append("\x1b[0m\r\n");

        // True color test
        testData.Append("TrueColor: ");
        for (int i = 0; i < 12; i++)
        {
            int r = (int)(255 * Math.Sin(0.3 * i + 0) * 0.5 + 127);
            int g = (int)(255 * Math.Sin(0.3 * i + 2) * 0.5 + 127);
            int b = (int)(255 * Math.Sin(0.3 * i + 4) * 0.5 + 127);
            testData.Append($"\x1b[38;2;{r};{g};{b}m*");
        }
        testData.Append("\x1b[0m\r\n");

        // Box drawing
        testData.Append("\u250c\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2510\r\n");
        testData.Append("\u2502 Box Drawing  \u2502\r\n");
        testData.Append("\u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2518\r\n");

        // Prompt-like
        testData.Append("\x1b[1;34m~/project\x1b[0m \x1b[33m(main)\x1b[0m $ ");

        var bytes = Encoding.UTF8.GetBytes(testData.ToString());
        _terminalView.Feed(bytes);
    }

    private void CleanupLiveMode()
    {
        if (_session != null)
        {
            _session.Dispose();
            _session = null;
        }
        _terminalControl = null;
    }

    private void CleanupTestMode()
    {
        _terminalView = null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    private static string FindClaude()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Anthropic", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "claude", "claude.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "claude";
    }

    private void BtnMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var mode = btn.Tag?.ToString() ?? "Live";

        if (mode == _currentMode) return;

        BtnLive.FontWeight = mode == "Live" ? FontWeights.Bold : FontWeights.Normal;
        BtnTest.FontWeight = mode == "Test" ? FontWeights.Bold : FontWeights.Normal;

        if (mode == "Live")
            ActivateLiveMode();
        else
            ActivateTestMode();
    }

    private void BtnRenderer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var mode = btn.Tag?.ToString() ?? "ORG";

        ITerminalRenderer renderer = mode switch
        {
            "PRO" => new ProRenderer(),
            "LITE" => new LiteRenderer(),
            _ => new OriginalRenderer(),
        };

        if (_currentMode == "Live" && _terminalControl != null)
            _terminalControl.SetRenderer(renderer);
        else if (_currentMode == "Test" && _terminalView != null)
            _terminalView.SetRenderer(renderer);
    }

    private void BtnCapture_Click(object sender, RoutedEventArgs e)
    {
        var outputDir = AppContext.BaseDirectory;
        var path = Path.Combine(outputDir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        DoCapture(path);
        TxtStatus.Text = $"Captured: {path}";
    }

    private void DoCapture(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (_currentMode == "Test" && _terminalView != null)
        {
            _terminalView.CaptureScreenshot(path);
        }
        else if (_currentMode == "Live" && _terminalControl != null)
        {
            CaptureFrameworkElement(_terminalControl, path);
        }
    }

    private void CaptureFrameworkElement(FrameworkElement element, string outputPath)
    {
        var dpi = 96.0;
        var source = PresentationSource.FromVisual(element);
        if (source?.CompositionTarget != null)
            dpi = 96.0 * source.CompositionTarget.TransformToDevice.M11;

        int width = (int)element.ActualWidth;
        int height = (int)element.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(width * dpi / 96.0),
            (int)(height * dpi / 96.0),
            dpi, dpi,
            System.Windows.Media.PixelFormats.Pbgra32);

        rtb.Render(element);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));

        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CleanupLiveMode();
        CleanupTestMode();
    }
}
