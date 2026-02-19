using System.Collections.Concurrent;
using System.Diagnostics;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Manages all active sessions. Creates, tracks, and kills sessions.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, Guid> _claudeSessionMap = new();
    private readonly AgentOptions _options;
    private readonly Action<string>? _log;

    public AgentOptions Options => _options;

    public SessionManager(AgentOptions options, Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
    }

    /// <summary>Create a new ConPty session that spawns claude.exe in the given repo path.</summary>
    public Session CreateSession(string repoPath, string? claudeArgs = null)
    {
        return CreateSession(repoPath, claudeArgs, SessionBackendType.ConPty, resumeSessionId: null);
    }

    /// <summary>Create a new session with the specified backend type.</summary>
    public Session CreateSession(string repoPath, string? claudeArgs, SessionBackendType backendType)
    {
        return CreateSession(repoPath, claudeArgs, backendType, resumeSessionId: null);
    }

    /// <summary>Create a session, optionally resuming a previous Claude session.</summary>
    public Session CreateSession(string repoPath, string? claudeArgs, SessionBackendType backendType, string? resumeSessionId)
    {
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

        var id = Guid.NewGuid();
        string args = claudeArgs ?? _options.DefaultClaudeArgs ?? string.Empty;

        // Add --resume flag if resuming a previous session
        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            args = $"{args} --resume {resumeSessionId}".Trim();
            _log?.Invoke($"Resuming Claude session {resumeSessionId}");
        }

        ISessionBackend backend = backendType switch
        {
            SessionBackendType.ConPty => new ConPtyBackend(_options.DefaultBufferSizeBytes),
            SessionBackendType.Pipe => new PipeBackend(_options.DefaultBufferSizeBytes),
            SessionBackendType.Embedded => throw new InvalidOperationException(
                "Use CreateEmbeddedSession for embedded mode - requires WPF backend."),
            _ => throw new ArgumentOutOfRangeException(nameof(backendType))
        };

        var session = new Session(id, repoPath, repoPath, claudeArgs, backend, backendType);

        try
        {
            // Get initial terminal dimensions (default 120x30)
            backend.Start(_options.ClaudePath, args, repoPath, 120, 30);
            session.MarkRunning();

            _sessions[id] = session;

            // Pre-populate ClaudeSessionId if resuming - ensures it's saved for crash recovery
            // even if Claude exits before sending SessionStart event.
            // Also register in _claudeSessionMap so GetSessionByClaudeId can find it and
            // prevent orphaned Claude processes from stealing this session ID.
            if (!string.IsNullOrEmpty(resumeSessionId))
            {
                session.ClaudeSessionId = resumeSessionId;
                _claudeSessionMap[resumeSessionId] = id;
            }
            var resumeInfo = !string.IsNullOrEmpty(resumeSessionId) ? $", Resume={resumeSessionId[..8]}..." : "";
            _log?.Invoke($"Session {id} created for repo {repoPath} (PID {backend.ProcessId}, Backend={backendType}{resumeInfo}).");

            return session;
        }
        catch (Exception ex)
        {
            session.MarkFailed();
            _log?.Invoke($"Failed to create session for {repoPath}: {ex.Message}");
            session.Dispose();
            throw;
        }
    }

    /// <summary>Create a new pipe mode session for the given repo path.
    /// No process is spawned until the user sends a prompt.</summary>
    public Session CreatePipeModeSession(string repoPath, string? claudeArgs = null)
    {
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

        var id = Guid.NewGuid();
        string args = claudeArgs ?? _options.DefaultClaudeArgs ?? string.Empty;

        var backend = new PipeBackend(_options.DefaultBufferSizeBytes);
        backend.Start(_options.ClaudePath, args, repoPath, 120, 30);

        var session = new Session(id, repoPath, repoPath, claudeArgs, backend, SessionBackendType.Pipe);
        session.MarkRunning();

        _sessions[id] = session;
        _log?.Invoke($"Pipe mode session {id} created for repo {repoPath}.");

        return session;
    }

    /// <summary>
    /// Create an embedded mode session. The WPF layer must provide the backend
    /// since EmbeddedBackend depends on WPF components.
    /// </summary>
    public Session CreateEmbeddedSession(string repoPath, string? claudeArgs, ISessionBackend embeddedBackend)
    {
        if (!Directory.Exists(repoPath))
            throw new DirectoryNotFoundException($"Repository path not found: {repoPath}");

        var id = Guid.NewGuid();

        var session = new Session(id, repoPath, repoPath, claudeArgs, embeddedBackend, SessionBackendType.Embedded);
        session.MarkRunning();

        _sessions[id] = session;
        _log?.Invoke($"Embedded session {id} created for repo {repoPath}.");

        return session;
    }

    /// <summary>Get a session by ID.</summary>
    public Session? GetSession(Guid id) => _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>List all sessions.</summary>
    public IReadOnlyCollection<Session> ListSessions() => _sessions.Values.ToList().AsReadOnly();

    /// <summary>Kill a session by ID.</summary>
    public async Task KillSessionAsync(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var session))
            throw new KeyNotFoundException($"Session {id} not found.");

        await session.KillAsync(_options.GracefulShutdownTimeoutSeconds * 1000);
    }

    /// <summary>Return PIDs of all tracked embedded sessions.</summary>
    public HashSet<int> GetTrackedProcessIds()
        => _sessions.Values
            .Where(s => s.BackendType == SessionBackendType.Embedded && s.ProcessId > 0)
            .Select(s => s.ProcessId)
            .ToHashSet();

    /// <summary>Scan for orphaned claude.exe processes on startup.</summary>
    public void ScanForOrphans()
    {
        try
        {
            var claudeProcesses = Process.GetProcessesByName("claude");
            if (claudeProcesses.Length > 0)
            {
                _log?.Invoke(
                    $"Found {claudeProcesses.Length} orphaned claude.exe process(es). " +
                    "Cannot re-attach ConPTY. Consider killing them manually if they are from a previous run.");

                foreach (var proc in claudeProcesses)
                {
                    _log?.Invoke($"  Orphan PID {proc.Id}, started {proc.StartTime}");
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Error scanning for orphaned claude.exe processes: {ex.Message}");
        }
    }

    /// <summary>Remove a session from tracking (dispose and clean up).</summary>
    public void RemoveSession(Guid id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            // Remove any Claude session mapping
            if (session.ClaudeSessionId != null)
                _claudeSessionMap.TryRemove(session.ClaudeSessionId, out _);

            session.Dispose();
            _log?.Invoke($"Session {id} removed.");
        }
    }

    /// <summary>Kill all sessions (used during graceful shutdown).</summary>
    public async Task KillAllSessionsAsync()
    {
        var tasks = _sessions.Values
            .Where(s => s.Status is SessionStatus.Running or SessionStatus.Starting)
            .Select(s => s.KillAsync(_options.GracefulShutdownTimeoutSeconds * 1000))
            .ToArray();

        if (tasks.Length > 0)
        {
            _log?.Invoke($"Killing {tasks.Length} active session(s)...");
            await Task.WhenAll(tasks);
        }
    }

    /// <summary>Fires when a Claude session is registered to a Director session.</summary>
    public event Action<Session, string>? OnClaudeSessionRegistered;

    /// <summary>Register a Claude session_id -> Director session mapping.</summary>
    public void RegisterClaudeSession(string claudeSessionId, Guid directorSessionId)
    {
        // Check if this Claude session ID is already assigned to a different Director session
        if (_claudeSessionMap.TryGetValue(claudeSessionId, out var existingId) && existingId != directorSessionId)
        {
            _log?.Invoke($"WARNING: Claude session {claudeSessionId} is already registered to Director session {existingId}, ignoring registration for {directorSessionId}.");
            return;
        }

        _claudeSessionMap[claudeSessionId] = directorSessionId;
        if (_sessions.TryGetValue(directorSessionId, out var session))
        {
            session.ClaudeSessionId = claudeSessionId;
            // Refresh Claude metadata now that we have the session ID
            session.RefreshClaudeMetadata();
            // Verify the session file exists
            session.VerifyClaudeSession();
            // If file verification passed, also mark terminal verification as matched
            // This handles the case where terminal matching found the right session
            if (session.VerificationStatus == Claude.SessionVerificationStatus.Verified)
            {
                session.MarkAsPreVerified();
            }
            // Notify listeners
            OnClaudeSessionRegistered?.Invoke(session, claudeSessionId);
        }
        _log?.Invoke($"Registered Claude session {claudeSessionId} -> Director session {directorSessionId}.");
    }

    /// <summary>Manually re-link a Director session to a different Claude session ID.</summary>
    public void RelinkClaudeSession(Guid directorSessionId, string newClaudeSessionId)
    {
        if (!_sessions.TryGetValue(directorSessionId, out var session))
        {
            _log?.Invoke($"RelinkClaudeSession: Director session {directorSessionId} not found.");
            return;
        }

        // Remove old mapping if present
        if (session.ClaudeSessionId != null)
        {
            _claudeSessionMap.TryRemove(session.ClaudeSessionId, out _);
            _log?.Invoke($"RelinkClaudeSession: Removed old mapping {session.ClaudeSessionId}.");
        }

        // Set new mapping
        session.ClaudeSessionId = newClaudeSessionId;
        _claudeSessionMap[newClaudeSessionId] = directorSessionId;

        // Refresh metadata and verify
        session.RefreshClaudeMetadata();
        session.VerifyClaudeSession();

        // If file verification passed, also mark terminal verification as matched
        if (session.VerificationStatus == Claude.SessionVerificationStatus.Verified)
        {
            session.MarkAsPreVerified();
        }

        // Notify listeners
        OnClaudeSessionRegistered?.Invoke(session, newClaudeSessionId);
        _log?.Invoke($"RelinkClaudeSession: Linked {directorSessionId} to Claude session {newClaudeSessionId}.");
    }

    /// <summary>Look up a Director session by its Claude session_id.</summary>
    public Session? GetSessionByClaudeId(string claudeSessionId)
    {
        if (_claudeSessionMap.TryGetValue(claudeSessionId, out var id))
            return GetSession(id);
        return null;
    }

    /// <summary>
    /// Save state of sessions that can be resumed.
    /// Includes: running sessions, and ANY session with ClaudeSessionId (can resume with --resume).
    /// </summary>
    public void SaveCurrentState(SessionStateStore store)
    {
        LogSessionsForDebug("SaveCurrentState");
        var persisted = BuildPersistedSessions();
        store.Save(persisted);
        _log?.Invoke($"[SaveCurrentState] Saved {persisted.Count} session(s) to state store.");
    }

    /// <summary>
    /// Save state of sessions to the store (used when keeping sessions on exit).
    /// The getHwnd delegate maps session ID -> console HWND (as long), for Embedded mode only.
    /// Saves ALL sessions that can be resumed: running sessions and any session with ClaudeSessionId.
    /// </summary>
    public void SaveSessionState(SessionStateStore store, Func<Guid, long> getHwnd)
    {
        LogSessionsForDebug("SaveSessionState");
        var persisted = BuildPersistedSessions(getHwnd);
        store.Save(persisted);
        _log?.Invoke($"[SaveSessionState] Saved {persisted.Count} session(s) to state store.");
    }

    private void LogSessionsForDebug(string caller)
    {
        _log?.Invoke($"[{caller}] Total sessions in manager: {_sessions.Count}");
        foreach (var s in _sessions.Values)
            _log?.Invoke($"  Session {s.Id}: Status={s.Status}, ClaudeSessionId={s.ClaudeSessionId ?? "(null)"}, Repo={s.RepoPath}");
    }

    private List<PersistedSession> BuildPersistedSessions(Func<Guid, long>? getHwnd = null)
    {
        return _sessions.Values
            .Where(s => s.Status == SessionStatus.Running ||
                       !string.IsNullOrEmpty(s.ClaudeSessionId))
            .OrderBy(s => s.SortOrder)
            .Select(s => new PersistedSession
            {
                Id = s.Id,
                RepoPath = s.RepoPath,
                WorkingDirectory = s.WorkingDirectory,
                ClaudeArgs = s.ClaudeArgs,
                CustomName = s.CustomName,
                CustomColor = s.CustomColor,
                PendingPromptText = s.PendingPromptText,
                EmbeddedProcessId = s.ProcessId,
                ConsoleHwnd = getHwnd != null && s.BackendType == SessionBackendType.Embedded ? getHwnd(s.Id) : 0,
                ClaudeSessionId = s.ClaudeSessionId,
                ActivityState = s.ActivityState,
                CreatedAt = s.CreatedAt,
                SortOrder = s.SortOrder,
                ExpectedFirstPrompt = s.ExpectedFirstPrompt ?? s.VerifiedFirstPrompt,
            })
            .ToList();
    }

    /// <summary>Restore a single persisted embedded session into tracking.
    /// The WPF layer must provide the reattached backend.</summary>
    public Session RestoreEmbeddedSession(PersistedSession ps, ISessionBackend embeddedBackend)
    {
        var session = new Session(
            ps.Id, ps.RepoPath, ps.WorkingDirectory, ps.ClaudeArgs,
            embeddedBackend, ps.ClaudeSessionId, ps.ActivityState, ps.CreatedAt,
            ps.CustomName, ps.CustomColor, ps.PendingPromptText);

        // Set expected first prompt BEFORE verification so it can be compared
        session.ExpectedFirstPrompt = ps.ExpectedFirstPrompt;

        _sessions[session.Id] = session;

        if (ps.ClaudeSessionId != null)
        {
            // Check for duplicate ClaudeSessionId - if another session already has this ID,
            // clear it from this session to force auto-registration of a new ID
            if (_claudeSessionMap.TryGetValue(ps.ClaudeSessionId, out var existingId))
            {
                _log?.Invoke($"WARNING: ClaudeSessionId {ps.ClaudeSessionId[..8]}... already used by session {existingId}, clearing from {session.Id}");
                session.ClaudeSessionId = null;
            }
            else
            {
                _claudeSessionMap[ps.ClaudeSessionId] = session.Id;
                // Verify session file exists AND content matches expected prompt
                session.VerifyClaudeSession();
                if (session.VerificationStatus == Claude.SessionVerificationStatus.ContentMismatch)
                {
                    _log?.Invoke($"WARNING: Session {session.Id} ClaudeSessionId {ps.ClaudeSessionId[..8]}... content mismatch - session file doesn't match expected prompt");
                }
            }
        }

        _log?.Invoke($"Restored session {session.Id} (PID {session.ProcessId}).");
        return session;
    }

    /// <summary>
    /// Load persisted sessions from the store. Returns a RestoreSessionsResult containing
    /// PersistedSession records for the WPF layer to restore, plus any load errors.
    /// Sessions with ClaudeSessionId can be resumed via --resume flag even if the original process is gone.
    /// </summary>
    public RestoreSessionsResult LoadPersistedSessions(SessionStateStore store)
    {
        var loadResult = store.Load();

        // If load failed, return immediately with error info
        if (!loadResult.Success)
        {
            _log?.Invoke($"CRITICAL: Failed to load sessions.json: {loadResult.ErrorMessage}");
            return new RestoreSessionsResult
            {
                Sessions = new List<PersistedSession>(),
                LoadSuccess = false,
                LoadErrorMessage = loadResult.ErrorMessage,
                FileExistedButFailed = loadResult.FileExistedButFailed
            };
        }

        var persisted = loadResult.Sessions;
        var valid = new List<PersistedSession>();

        // Track seen ClaudeSessionIds to detect duplicates in persisted data
        var seenClaudeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ps in persisted)
        {
            // Sessions with ClaudeSessionId can be resumed with --resume flag,
            // even if the original process is gone (ConPty crash recovery)
            if (!string.IsNullOrEmpty(ps.ClaudeSessionId))
            {
                // Check for duplicate ClaudeSessionIds - this indicates corrupt persisted data
                if (seenClaudeIds.Contains(ps.ClaudeSessionId))
                {
                    _log?.Invoke($"WARNING: Persisted session {ps.Id} has duplicate ClaudeSessionId {ps.ClaudeSessionId[..8]}..., clearing to force fresh start.");
                    ps.ClaudeSessionId = null;
                }
                else
                {
                    seenClaudeIds.Add(ps.ClaudeSessionId);
                    _log?.Invoke($"Persisted session {ps.Id} has ClaudeSessionId {ps.ClaudeSessionId[..8]}..., valid for resume.");
                }
                valid.Add(ps);
                continue;
            }

            // Sessions without ClaudeSessionId are still valid - they just won't use --resume
            // ConPTY will start a fresh Claude process for them
            _log?.Invoke($"Persisted session {ps.Id} has no ClaudeSessionId, will start fresh Claude process.");
            valid.Add(ps);
        }

        _log?.Invoke($"Found {valid.Count}/{persisted.Count} valid persisted session(s).");

        // Don't re-save here - let RestorePersistedSessions handle cleanup after restoration
        return new RestoreSessionsResult
        {
            Sessions = valid,
            LoadSuccess = true,
            LoadErrorMessage = null,
            FileExistedButFailed = false
        };
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
        _claudeSessionMap.Clear();
    }
}
