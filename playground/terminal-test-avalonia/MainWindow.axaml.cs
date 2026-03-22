using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Threading;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Terminal.Avalonia;
using CcDirector.Terminal.Avalonia.Rendering;

namespace TerminalTestAvalonia;

/// <summary>
/// Mirrors the EXACT code path from playground/terminal-test (WPF) ActivateLiveMode().
/// Creates ConPtyBackend directly (not through SessionManager).
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string OutputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cc-director", "terminal-test");

    private SessionManager? _sessionManager;
    private Session? _session;
    private TerminalControl? _terminal;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(OutputDir);
        Loaded += (_, _) => LaunchSession();
        Closing += (_, _) => Cleanup();
    }

    private void LaunchSession()
    {
        try
        {
            Log("=== Starting terminal test (WPF-style direct backend) ===");

            _terminal = new TerminalControl();
            _terminal.RendererBackgroundChanged += color => { Background = new Avalonia.Media.SolidColorBrush(color); };
            TerminalHost.Children.Add(_terminal);
            _terminal.SetRenderer(new ProRenderer());

            // Use SessionManager (same as real app) but with home dir to avoid repo conflicts
            var options = new AgentOptions
            {
                ClaudePath = "claude",
                DefaultClaudeArgs = "--dangerously-skip-permissions",
                DefaultBufferSizeBytes = 2_097_152
            };
            // Use the cc-director repo itself (must be a git repo for Claude Code)
            string workDir = @"D:\ReposFred\cc-director";
            _sessionManager = new SessionManager(options, msg => Log(msg));
            _session = _sessionManager.CreateSession(workDir);

            // Attach (same as real app)
            _terminal.Attach(_session);

            Log($"Session started: PID={_session.ProcessId}");
            StatusText.Text = $"PID {_session.ProcessId} - waiting...";

            // Auto-send Enter after 2s to bypass trust prompt (same as WPF test line 146-152)
            var enterTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            enterTimer.Tick += (_, _) =>
            {
                enterTimer.Stop();
                Log("Sending Enter via SendInput");
                _session?.SendInput(new byte[] { 0x0D });
            };
            enterTimer.Start();

            // Log progress every 2 seconds
            var progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            progressTimer.Tick += (_, _) =>
            {
                if (_session?.Buffer == null) return;
                long bytes = _session.Buffer.TotalBytesWritten;
                int lines = _terminal?.ContentLineCount ?? 0;
                var running = _session.Status.ToString();
                var msg = $"{running} | bytes={bytes}, lines={lines}";
                StatusText.Text = msg;
                Log(msg);
            };
            progressTimer.Start();

            // Capture after 15 seconds
            var captureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            captureTimer.Tick += (_, _) =>
            {
                captureTimer.Stop();
                Capture();
            };
            captureTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"FAILED: {ex.Message}";
            Log($"Launch failed: {ex}");
        }
    }

    private void Capture()
    {
        try
        {
            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            if (_terminal != null && _terminal.Bounds.Width > 0)
            {
                var pngPath = Path.Combine(OutputDir, $"capture-{ts}.png");
                var pixelSize = new Avalonia.PixelSize((int)_terminal.Bounds.Width, (int)_terminal.Bounds.Height);
                var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(pixelSize);
                rtb.Render(_terminal);
                rtb.Save(pngPath);
                Log($"Screenshot: {pngPath}");
            }

            if (_session?.Buffer != null)
            {
                var rawBytes = _session.Buffer.DumpAll();
                var binPath = Path.Combine(OutputDir, $"capture-{ts}.bin");
                File.WriteAllBytes(binPath, rawBytes);
                Log($"Raw bytes: {rawBytes.Length} -> {binPath}");
            }

            StatusText.Text = "CAPTURED";
        }
        catch (Exception ex) { Log($"Capture error: {ex}"); }
    }

    private void Cleanup()
    {
        _terminal?.Detach();
        try
        {
            if (_session != null)
                _sessionManager?.KillSessionAsync(_session.Id).Wait(TimeSpan.FromSeconds(3));
            _sessionManager?.Dispose();
        }
        catch { }
    }

    private static void Log(string msg)
    {
        var logPath = Path.Combine(OutputDir, "test.log");
        File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
    }
}
