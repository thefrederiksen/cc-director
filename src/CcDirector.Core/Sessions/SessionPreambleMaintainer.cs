using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Remove-the-network-port mission, phase 3: keeps every live session's SessionStart hook-output file
/// CURRENT for as long as the session runs.
///
/// WHY THIS EXISTS AT ALL, AND WHY A LAUNCH SNAPSHOT WOULD HAVE BEEN WRONG. The obvious replacement
/// for the deleted preamble routes is to write the file once when the session launches. That would
/// ship stale text. The preamble renders from three stores that are all LIVE and all Gateway-owned -
/// the user's own injected text, which they edit in Settings while sessions are running, plus the
/// workflow index and the skill index - and from the session's own name and workflow seat, which
/// change too. The hook fires again on every resume, clear and compact, possibly hours after launch.
/// A snapshot would serve a user their OLD text after they had edited it and would hide newly
/// published skills and workflows, and NOTHING WOULD LOOK BROKEN. It would simply be wrong.
///
/// So the file is maintained. Two kinds of input change it, and each has its own trigger:
///   - The three shared stores. The Director already re-downloads all three on an interval, precisely
///     so an injected-text change needs no restart. <see cref="RewriteAll"/> is called at the end of
///     that refresh, which makes the file EXACTLY as fresh as the cache the deleted routes read: they
///     rendered on demand, but from that same cache, so there was never anything fresher to read.
///   - The per-session inputs - the display name, the explicit role, the workflow seat. Those have
///     their own events, so the one affected file is rewritten the moment it changes.
///
/// A rewrite that produces identical content does not touch the file. The rewrite runs across every
/// live session on a timer, and rewriting unchanged files would churn the disk and fire a directory
/// watcher for no reason.
/// </summary>
public sealed class SessionPreambleMaintainer : IDisposable
{
    private readonly SessionManager _sessions;
    private readonly Func<SignedInUser?> _user;
    private readonly string? _directory;
    private readonly string _machine;
    private readonly InjectedTextStore? _store;
    private readonly HashSet<Guid> _subscribed = new();
    private readonly object _gate = new();
    private bool _started;
    private bool _disposed;

    /// <param name="sessions">The session roster this maintainer follows.</param>
    /// <param name="user">
    /// Reads the signed-in DevThrottle user (issue #1357). A FUNCTION, not a value, and read on every
    /// rewrite: the account is resolved from the Gateway through a cache that warms after startup, so a
    /// value captured when this was constructed would name nobody forever.
    /// </param>
    /// <param name="directory">Tests pin the output directory; production uses the storage root.</param>
    /// <param name="machine">Tests pin the machine name; production uses this machine's.</param>
    /// <param name="store">
    /// Tests pin the injected-text store so they never read what the machine running the suite has
    /// cached; production passes null and reads the real cache, which is the one the Director's refresh
    /// writes. Held, not resolved per rewrite, because a test's whole point may be to CHANGE its contents
    /// between two rewrites and see the difference reach the file.
    /// </param>
    public SessionPreambleMaintainer(
        SessionManager sessions, Func<SignedInUser?> user, string? directory = null, string? machine = null,
        InjectedTextStore? store = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _directory = directory;
        _machine = string.IsNullOrWhiteSpace(machine) ? Environment.MachineName : machine;
        _store = store;
    }

    /// <summary>
    /// Begin following the roster, and write a file for every session already on it. Idempotent.
    ///
    /// The initial pass is not redundant with the launch-path write: sessions restored from persistence
    /// at Director startup were never launched by this process, so nothing else would ever write theirs.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        _sessions.OnSessionCreated += OnCreated;
        _sessions.OnSessionRemoved += OnRemoved;
        _sessions.OnSessionRenamed += OnRenamed;

        foreach (var session in _sessions.ListSessions())
        {
            Follow(session);
            Rewrite(session);
        }

        FileLog.Write("[SessionPreambleMaintainer] following the session roster");
    }

    /// <summary>
    /// Rewrite every live session's file. Called at the end of the Director's shared-store refresh -
    /// the injected text, the workflow index and the skill index have just been re-downloaded, so
    /// whatever the next hook fire delivers must reflect them.
    /// </summary>
    public void RewriteAll()
    {
        if (_disposed) return;

        var changed = 0;
        foreach (var session in _sessions.ListSessions())
        {
            if (Rewrite(session))
                changed++;
        }

        // Logged only when something moved. This runs on a timer, and a line per quiet tick would bury
        // the one that matters.
        if (changed > 0)
            FileLog.Write($"[SessionPreambleMaintainer] RewriteAll: {changed} session preamble(s) changed");
    }

    /// <summary>
    /// Write one session's file now, whether or not this maintainer is following the roster. This is
    /// the launch path's entry point: the file has to exist BEFORE the agent process starts, because
    /// its startup hook fires within moments of it.
    /// </summary>
    /// <returns>True when the file's content changed (or it did not exist yet).</returns>
    public bool Rewrite(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        try
        {
            var rendered = SessionPreambleFile.Render(session, _machine, _user(), _store);
            var path = SessionHookFiles.PreamblePathFor(session.Id, _directory);

            // Compare before writing: a rewrite that changes nothing must not touch the file.
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), rendered, StringComparison.Ordinal))
                return false;

            SessionHookFiles.WriteAtomic(path, rendered);
            FileLog.Write($"[SessionPreambleMaintainer] wrote {path} ({rendered.Length} characters)");
            return true;
        }
        catch (Exception ex)
        {
            // A preamble that cannot be written must never take a session down - the agent starts and
            // simply has no preamble. Said out loud, because the whole failure mode of this channel is
            // that an agent quietly knows nothing about the fleet and nothing reports it.
            FileLog.Write($"[SessionPreambleMaintainer] FAILED to write the preamble for {session.Id}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Subscribe to the per-session inputs that change a preamble. Idempotent per session.</summary>
    private void Follow(Session session)
    {
        lock (_gate)
        {
            if (!_subscribed.Add(session.Id))
                return;
        }
        session.OnPreambleInputsChanged += () => Rewrite(session);
    }

    private void OnCreated(Session session)
    {
        Follow(session);
        Rewrite(session);
    }

    private void OnRenamed(Session session, string? _) => Rewrite(session);

    private void OnRemoved(Session session)
    {
        lock (_gate) { _subscribed.Remove(session.Id); }
        SessionPreambleFile.DeleteFor(session.Id, _directory);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sessions.OnSessionCreated -= OnCreated;
        _sessions.OnSessionRemoved -= OnRemoved;
        _sessions.OnSessionRenamed -= OnRenamed;
        lock (_gate) { _subscribed.Clear(); }
    }
}
