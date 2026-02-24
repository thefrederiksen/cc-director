using System.Diagnostics;
using System.Text.RegularExpressions;
using CcDirector.Wpf.Teams.Models;

namespace CcDirector.Wpf.Teams;

/// <summary>
/// Manages the Azure Dev Tunnel process for exposing the bot endpoint via HTTPS.
/// Prerequisites (manual setup required):
/// 1. Run 'devtunnel user login' once
/// 2. Run 'devtunnel create [tunnel-name]' once to create a persistent tunnel
/// </summary>
public sealed class DevTunnelManager : IDisposable
{
    private static readonly Regex UrlPattern = new(@"https://[a-zA-Z0-9\-]+\.devtunnels\.ms[^\s]*", RegexOptions.Compiled);

    private readonly TeamsBotConfig _config;
    private readonly Action<string> _log;
    private Process? _tunnelProcess;
    private bool _disposed;

    /// <summary>Public URL assigned by Dev Tunnel (e.g., https://xxx.devtunnels.ms).</summary>
    public string? PublicUrl { get; private set; }

    /// <summary>Whether the tunnel process is running.</summary>
    public bool IsRunning => _tunnelProcess != null && !_tunnelProcess.HasExited;

    /// <summary>Fires when the public URL becomes available.</summary>
    public event Action<string>? OnUrlAvailable;

    /// <summary>Fires when the tunnel process exits unexpectedly.</summary>
    public event Action<int>? OnProcessExited;

    public DevTunnelManager(TeamsBotConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Start the dev tunnel host process.
    /// </summary>
    public async Task StartAsync()
    {
        _log($"[DevTunnel] Starting tunnel: {_config.TunnelName} on port {_config.Port}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "devtunnel",
            Arguments = $"host --tunnel-name {_config.TunnelName} --port {_config.Port} --allow-anonymous",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _tunnelProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _tunnelProcess.OutputDataReceived += OnOutputReceived;
        _tunnelProcess.ErrorDataReceived += OnErrorReceived;
        _tunnelProcess.Exited += OnProcessExitedHandler;

        try
        {
            _tunnelProcess.Start();
            _tunnelProcess.BeginOutputReadLine();
            _tunnelProcess.BeginErrorReadLine();
            _log($"[DevTunnel] Process started, PID={_tunnelProcess.Id}");

            // Wait for URL to appear (with timeout)
            var timeout = TimeSpan.FromSeconds(30);
            var startTime = DateTime.UtcNow;
            while (string.IsNullOrEmpty(PublicUrl) && DateTime.UtcNow - startTime < timeout)
            {
                await Task.Delay(100);
            }

            if (string.IsNullOrEmpty(PublicUrl))
            {
                _log("[DevTunnel] WARNING: Timed out waiting for public URL");
            }
        }
        catch (Exception ex)
        {
            _log($"[DevTunnel] Start FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Stop the dev tunnel process.
    /// </summary>
    public async Task StopAsync()
    {
        if (_tunnelProcess == null || _tunnelProcess.HasExited)
        {
            _log("[DevTunnel] Process already stopped");
            return;
        }

        _log("[DevTunnel] Stopping tunnel process");

        try
        {
            _tunnelProcess.Kill(entireProcessTree: true);
            await Task.Run(() => _tunnelProcess.WaitForExit(5000));
            _log("[DevTunnel] Process stopped");
        }
        catch (Exception ex)
        {
            _log($"[DevTunnel] Stop FAILED: {ex.Message}");
        }
    }

    private void OnOutputReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
            return;

        _log($"[DevTunnel] {e.Data}");

        // Extract public URL from output
        var match = UrlPattern.Match(e.Data);
        if (match.Success && string.IsNullOrEmpty(PublicUrl))
        {
            PublicUrl = match.Value.TrimEnd('/');
            _log($"[DevTunnel] Public URL: {PublicUrl}");
            OnUrlAvailable?.Invoke(PublicUrl);
        }
    }

    private void OnErrorReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
            return;

        _log($"[DevTunnel] ERROR: {e.Data}");
    }

    private void OnProcessExitedHandler(object? sender, EventArgs e)
    {
        var exitCode = _tunnelProcess?.ExitCode ?? -1;
        _log($"[DevTunnel] Process exited with code {exitCode}");
        OnProcessExited?.Invoke(exitCode);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_tunnelProcess != null)
        {
            try
            {
                if (!_tunnelProcess.HasExited)
                {
                    _tunnelProcess.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited between check and kill - safe to ignore
            }
            catch (SystemException)
            {
                // Process termination failed - nothing more we can do during dispose
            }

            _tunnelProcess.OutputDataReceived -= OnOutputReceived;
            _tunnelProcess.ErrorDataReceived -= OnErrorReceived;
            _tunnelProcess.Exited -= OnProcessExitedHandler;
            _tunnelProcess.Dispose();
            _tunnelProcess = null;
        }
    }
}
