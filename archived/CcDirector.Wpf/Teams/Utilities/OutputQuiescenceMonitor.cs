using System.Timers;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;

namespace CcDirector.Wpf.Teams.Utilities;

/// <summary>
/// Monitors session output and fires an event when output has been quiet
/// for a configurable period AND the session is waiting for input.
/// Used to send "task complete" notifications to Teams.
/// </summary>
public sealed class OutputQuiescenceMonitor : IDisposable
{
    private readonly Session _session;
    private readonly int _quiescenceMs;
    private readonly Action<Session> _onQuiescent;
    private readonly Action<string> _log;

    private System.Timers.Timer? _timer;
    private long _lastBufferPosition;
    private bool _monitoringActive;
    private bool _disposed;

    public OutputQuiescenceMonitor(
        Session session,
        int quiescenceMs,
        Action<Session> onQuiescent,
        Action<string> log)
    {
        _session = session;
        _quiescenceMs = quiescenceMs;
        _onQuiescent = onQuiescent;
        _log = log;
    }

    /// <summary>
    /// Start monitoring for quiescence after user input is sent.
    /// </summary>
    public void StartMonitoring()
    {
        if (_disposed)
            return;

        _monitoringActive = true;
        _lastBufferPosition = _session.Buffer?.TotalBytesWritten ?? 0;

        _timer?.Dispose();
        _timer = new System.Timers.Timer(_quiescenceMs);
        _timer.Elapsed += OnTimerElapsed;
        _timer.AutoReset = false;
        _timer.Start();

        _log($"[QuiescenceMonitor] Started monitoring session {_session.Id}, buffer pos={_lastBufferPosition}");
    }

    /// <summary>
    /// Stop monitoring (e.g., when session is deselected).
    /// </summary>
    public void StopMonitoring()
    {
        _monitoringActive = false;
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_disposed || !_monitoringActive)
            return;

        var currentPosition = _session.Buffer?.TotalBytesWritten ?? 0;

        // Check if output has stopped AND session is waiting for input
        if (currentPosition == _lastBufferPosition &&
            _session.ActivityState == ActivityState.WaitingForInput)
        {
            _log($"[QuiescenceMonitor] Session {_session.Id} is quiescent (buffer={currentPosition}, state={_session.ActivityState})");
            _monitoringActive = false;
            _onQuiescent(_session);
        }
        else
        {
            // Output is still happening, reset timer
            _lastBufferPosition = currentPosition;
            _timer?.Start();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }
}
