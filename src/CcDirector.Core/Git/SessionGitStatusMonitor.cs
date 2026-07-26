using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Keeps every live session's "how many files are changed in my working tree" count up to date
/// (<see cref="Session.UncommittedCount"/>), so the desktop rail, the Cockpit roster and the phone all read
/// ONE number that the Director produced instead of each surface polling git for itself.
///
/// This poll used to live in the desktop window (<c>MainWindow.RefreshSessionGitStatusAsync</c>), which
/// meant the number existed only where it was rendered: it never reached <c>SessionDto</c>, so the Gateway
/// was never told and the Cockpit roster had nothing to show. Moving it here puts it on the Session, which
/// is what the Control API maps onto the wire.
///
/// FAIL CLOSED, NEVER FAIL ZERO (issue 516). A git probe can fail - no git on the path, a permissions
/// problem, a repository mid-rebase - and the count is then UNKNOWN. This monitor publishes only a count a
/// probe actually produced; a failed probe leaves the previous value alone, and a session whose probe has
/// never succeeded keeps a null count. Reporting 0 would tell every reader downstream "this tree is clean",
/// which is the one thing we do not know.
/// </summary>
public sealed class SessionGitStatusMonitor : IDisposable
{
    /// <summary>The default poll interval - the same fifteen seconds the desktop rail used.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    /// <summary>How many repositories are probed at once. Matches the desktop's old limit.</summary>
    private const int MaxConcurrentProbes = 4;

    private readonly SessionManager _sessionManager;
    private readonly Func<string, CancellationToken, Task<GitCountResult>> _probe;
    private readonly TimeSpan _interval;
    private readonly Func<string, bool> _directoryExists;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="sessionManager">The live session list to walk each cycle.</param>
    /// <param name="interval">Poll interval; null uses <see cref="DefaultInterval"/>. A test seam.</param>
    /// <param name="probe">The git probe; null uses a shared <see cref="GitStatusProvider"/>. Its ten-second
    /// cache is keyed by path and shared across instances, so several sessions in ONE working tree cost one
    /// <c>git status</c> call between them, not one each. A test seam.</param>
    /// <param name="directoryExists">Existence check for a session's repository path. A test seam; production
    /// passes null and gets <see cref="Directory.Exists"/>.</param>
    public SessionGitStatusMonitor(
        SessionManager sessionManager,
        TimeSpan? interval = null,
        Func<string, CancellationToken, Task<GitCountResult>>? probe = null,
        Func<string, bool>? directoryExists = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        var provider = new GitStatusProvider();
        _probe = probe ?? provider.GetCountAsync;
        _interval = interval ?? DefaultInterval;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    /// <summary>Start the background loop. Idempotent.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        FileLog.Write($"[SessionGitStatusMonitor] Start: probing every {_interval.TotalSeconds:0} seconds");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshOnceAsync(ct).ConfigureAwait(false);
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception ex)
        {
            // The loop itself dying is the one failure the per-session catch below cannot cover, and it would
            // silently freeze every count for the life of the process - so it is logged as loudly as it gets.
            FileLog.Write("[SessionGitStatusMonitor] the poll loop ENDED unexpectedly; no further uncommitted "
                + $"counts will be reported until this Director restarts: {ex}");
        }
    }

    /// <summary>
    /// Probe every live session once and publish the counts. Internal (not private) so a test can drive one
    /// cycle deterministically instead of waiting on the timer. Returns how many sessions got a fresh count.
    /// </summary>
    internal async Task<int> RefreshOnceAsync(CancellationToken ct = default)
    {
        var sessions = _sessionManager.ListSessions();
        if (sessions.Count == 0) return 0;

        var published = 0;
        using var gate = new SemaphoreSlim(MaxConcurrentProbes);

        var probes = sessions.Select(async session =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var repoPath = session.RepoPath;
                // A session whose folder has been deleted or moved is not "clean" - it is unreadable. Leave
                // whatever the last successful probe said and move on.
                if (string.IsNullOrWhiteSpace(repoPath) || !_directoryExists(repoPath)) return;

                var count = await _probe(repoPath, ct).ConfigureAwait(false);
                if (!count.Success) return;   // unknown, not zero - see the class summary

                session.UncommittedCount = count.Count;
                Interlocked.Increment(ref published);
            }
            catch (OperationCanceledException)
            {
                // Shutdown mid-probe; the loop's handler owns it.
            }
            catch (Exception ex)
            {
                // One unreadable repository must never stop the other sessions being probed.
                FileLog.Write($"[SessionGitStatusMonitor] probe FAILED for session {session.Id}, "
                    + $"leaving the last known count in place: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(probes).ConfigureAwait(false);
        FileLog.Write($"[SessionGitStatusMonitor] RefreshOnceAsync: published {published} of {sessions.Count} session counts");
        return published;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        FileLog.Write("[SessionGitStatusMonitor] Dispose: stopped");
    }
}
