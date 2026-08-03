using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Remove-the-network-port mission, phase 3: watches the directory a Claude SessionStart hook drops
/// its CURRENT session id and transcript path into, replacing <c>POST /sessions/{sid}/claude-hook</c>.
///
/// Claude mints a NEW session id and a new transcript file on <c>/clear</c> and on auto-compaction.
/// The Director only knows the FIRST id (it preassigns it with <c>--session-id</c>), so without this
/// report its pointer goes stale and everything built on the transcript - session history, and the
/// Gateway voice mode above it - quietly goes empty. That is the whole reason the route existed, and
/// it is why the replacement has to be as reliable as the route was.
///
/// THE FILE NAME CARRIES THE SESSION. A drop is applied to the session its FILE is named after, not to
/// a session named inside the body. The Director hands each session the exact path to write, so a
/// session cannot report a pointer for another one - the same limit the route's session-bound
/// credential gave, achieved by the shape of the drop box rather than by a check.
///
/// APPLYING A DROP IS IDEMPOTENT, so nothing depends on each event being seen exactly once. Files are
/// not deleted after reading; a session's file is removed when the session is. And because
/// <see cref="FileSystemWatcher"/> can genuinely lose events when its buffer overflows - a documented
/// property of the operating system facility, not a defect to fix - the loss is SIGNALLED on its Error
/// event, and this answers that signal with a full sweep of the directory. A dropped event would
/// otherwise cost a stale transcript pointer silently, which is exactly the failure this class exists
/// to prevent.
/// </summary>
public sealed class SessionPointerWatcher : IDisposable
{
    private readonly SessionManager _sessions;
    private readonly string _directory;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <param name="sessions">The roster a drop is applied to.</param>
    /// <param name="directory">Tests pin the drop box; production uses the storage root.</param>
    public SessionPointerWatcher(SessionManager sessions, string? directory = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _directory = string.IsNullOrWhiteSpace(directory) ? Storage.CcStorage.SessionPointers() : directory;
    }

    /// <summary>The directory being watched.</summary>
    public string Directory => _directory;

    /// <summary>
    /// Create the drop box, discard anything left in it, and start watching.
    ///
    /// The purge is not housekeeping - it is correctness. Sessions do not survive a Director restart,
    /// so every file present at startup belongs to a session that no longer exists. Left in place they
    /// would be re-applied by the first sweep, pointing a NEW session id at a DEAD session's transcript.
    /// </summary>
    public void Start()
    {
        System.IO.Directory.CreateDirectory(_directory);
        Purge();

        _watcher = new FileSystemWatcher(_directory, "*" + SessionHookFiles.DropExtension)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        _watcher.Created += OnDropped;
        _watcher.Changed += OnDropped;
        _watcher.Renamed += OnDropped;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;

        FileLog.Write($"[SessionPointerWatcher] watching {_directory} for session-pointer drops");
    }

    /// <summary>
    /// Read and apply every drop currently in the box. The answer to a signalled event loss, and the
    /// deterministic entry point a test drives so it never has to wait on watcher timing.
    /// </summary>
    /// <returns>How many drops were applied.</returns>
    public int Sweep()
    {
        if (!System.IO.Directory.Exists(_directory))
            return 0;

        var applied = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*" + SessionHookFiles.DropExtension))
        {
            if (Apply(path))
                applied++;
        }
        return applied;
    }

    /// <summary>
    /// Apply one drop file. Returns false when it names no live session, holds no valid event, or could
    /// not be read - each of which is logged, never silently ignored.
    /// </summary>
    public bool Apply(string path)
    {
        // Belt to the watcher's own filter: the atomic write leaves a ".tmp" beside the real file, and
        // Windows filename filters have historically matched more than they appear to.
        if (!string.Equals(Path.GetExtension(path), SessionHookFiles.DropExtension, StringComparison.OrdinalIgnoreCase))
            return false;

        var sessionId = SessionHookFiles.SessionIdFromDropPath(path);
        if (sessionId is null)
        {
            FileLog.Write($"[SessionPointerWatcher] ignoring {path}: its name is not a session id");
            return false;
        }

        var session = _sessions.GetSession(sessionId.Value);
        if (session is null)
        {
            FileLog.Write($"[SessionPointerWatcher] ignoring {path}: session {sessionId} is not on the roster");
            return false;
        }

        string body;
        try
        {
            body = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionPointerWatcher] could not read {path}: {ex.Message}");
            return false;
        }

        var evt = ClaudeHookEventParser.Parse(body);
        if (evt is null)
        {
            FileLog.Write($"[SessionPointerWatcher] ignoring {path}: not a valid hook event");
            return false;
        }

        FileLog.Write($"[SessionPointerWatcher] drop for {sessionId}: event={evt.HookEvent} source={evt.Source} " +
                      $"claudeId={evt.ClaudeSessionId} transcript={evt.TranscriptPath}");
        session.UpdateClaudeSessionPointer(evt.ClaudeSessionId, evt.TranscriptPath, evt.Source);

        // Keep the SessionManager's claude-id routing map in sync with the new id, exactly as the
        // deleted route did.
        if (!string.IsNullOrWhiteSpace(evt.ClaudeSessionId))
            _sessions.RelinkClaudeSession(sessionId.Value, evt.ClaudeSessionId!);

        return true;
    }

    /// <summary>Delete a session's drop file. Called when the session is removed.</summary>
    public void Forget(Guid sessionId)
    {
        var path = SessionHookFiles.PointerPathFor(sessionId, _directory);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionPointerWatcher] could not delete {path}: {ex.Message}");
        }
    }

    private void OnDropped(object sender, FileSystemEventArgs e)
    {
        // The handler runs on a thread pool thread the watcher owns; an escaping exception there takes
        // the process down, so this boundary catches. Applying a pointer is the only work.
        try { Apply(e.FullPath); }
        catch (Exception ex) { FileLog.Write($"[SessionPointerWatcher] applying {e.FullPath} FAILED: {ex.Message}"); }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // The operating system told us it lost events. Say so and read the whole box, rather than
        // leaving a session pointing at a transcript that no longer exists.
        FileLog.Write($"[SessionPointerWatcher] the watcher lost events ({e.GetException().Message}); sweeping the drop box");
        try
        {
            var applied = Sweep();
            FileLog.Write($"[SessionPointerWatcher] sweep after event loss applied {applied} drop(s)");
        }
        catch (Exception ex) { FileLog.Write($"[SessionPointerWatcher] sweep after event loss FAILED: {ex.Message}"); }
    }

    private void Purge()
    {
        var removed = 0;
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(_directory))
            {
                try { File.Delete(path); removed++; }
                catch (Exception ex) { FileLog.Write($"[SessionPointerWatcher] could not purge {path}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionPointerWatcher] could not enumerate {_directory} to purge it: {ex.Message}");
        }
        if (removed > 0)
            FileLog.Write($"[SessionPointerWatcher] purged {removed} drop(s) left by a previous Director");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnDropped;
            _watcher.Changed -= OnDropped;
            _watcher.Renamed -= OnDropped;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
