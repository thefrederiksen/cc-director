using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// In-memory registry of live Directors. Two ingress paths feed it, both kept
/// permanently:
///
///   1. Filesystem watch on %LOCALAPPDATA%\cc-director\config\director\instances\.
///      Used by same-machine Directors. Same-machine Directors do not need any
///      Gateway URL configured to be discovered. See <see cref="InstancesDirectory"/>.
///
///   2. HTTP register / heartbeat / unregister. Used by Directors that have
///      <c>gateway.url</c> configured (typically cross-machine). The Director POSTs
///      <see cref="Upsert"/> on startup, calls <see cref="Heartbeat"/> every 15 s, and
///      DELETEs via <see cref="Remove"/> on graceful shutdown.
///
/// De-duplication: keys are <c>directorId</c>. If both paths report the same id,
/// the HTTP entry wins because it carries <see cref="DirectorDto.TailnetEndpoint"/>
/// which the FSW path cannot provide.
/// </summary>
public sealed class DirectorRegistry : IDisposable
{
    public static string InstancesDirectory { get; } = CcStorage.DirectorInstances();

    /// <summary>The directory this registry actually watches (the shared default in production).</summary>
    public string WatchDirectory { get; }

    /// <param name="instancesDirectory">
    /// Override the watched instances directory. Tests pass an isolated temp directory so a
    /// real Director running on the dev machine is never discovered by (or polluted with)
    /// test hosts. Production omits it and uses the shared <see cref="InstancesDirectory"/>.
    /// </param>
    public DirectorRegistry(string? instancesDirectory = null)
    {
        WatchDirectory = instancesDirectory ?? InstancesDirectory;
    }

    /// <summary>If an HTTP-registered Director has not heartbeat for this long, it gets swept.</summary>
    public static TimeSpan HttpHeartbeatTimeout { get; } = TimeSpan.FromSeconds(60);

    // Gateway Cleanup mission (post-cut): the reachability circuit-breaker (consecutive-failure counting,
    // cooldown, unreachable-evict) and the advertised-endpoint re-verification state machine are DELETED.
    // Liveness is the tunnel connection itself now, not an HTTP probe, so there is nothing to circuit-break.

    private readonly ConcurrentDictionary<string, DirectorDto> _directors = new();

    /// <summary>
    /// Directors that have answered at least one fleet probe (issue #197). Deliberately
    /// NOT cleared by <see cref="Upsert"/>: the unreachable-evict / 410 / re-register
    /// cycle re-registers the same live process every few minutes, and whether it EVER
    /// answered is exactly the signal that distinguishes "endpoint was never provisioned
    /// (check Tailscale Serve on that machine)" from "was fine, went dark". Cleared on
    /// graceful unregister and when the heartbeat-stale sweep removes a dead process -
    /// a future process under the same id starts with a truthful blank slate.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _everReachable = new();
    private FileSystemWatcher? _watcher;
    private Timer? _sweeper;
    private bool _disposed;

    /// <summary>Raised when a Director appears (file created or HTTP register).</summary>
    public event Action<DirectorDto>? OnDirectorAdded;

    /// <summary>Raised when a Director disappears (file removed, HTTP unregister, or stale).</summary>
    public event Action<string>? OnDirectorRemoved;

    /// <summary>
    /// Raise <see cref="OnDirectorRemoved"/> without letting a subscriber kill the process.
    ///
    /// This is an ENTRY-POINT boundary in the CodingStyle sense (an external event subscription): every
    /// caller is either a FileSystemWatcher callback or the stale-sweep timer, both of which run on
    /// thread-pool threads with NO enclosing try/catch. An exception thrown by a subscriber there is
    /// UNHANDLED, so it does not merely fail the removal - it terminates the whole Gateway process.
    ///
    /// Not hypothetical: a subscriber writes the tenant-scoped snooze store, and when that store's database
    /// was unavailable the throw came straight up this path and took the process down - observed as a test
    /// run that ABORTED partway through while still reporting exit code 0. Failing to clear one removed
    /// Director's snoozes is a bounded, cosmetic loss; losing the Gateway is not. The failure is logged
    /// LOUD - this catches to keep the process alive, not to hide the fault.
    ///
    /// Each subscriber is invoked INDEPENDENTLY. A plain Invoke on a multicast delegate stops at the first
    /// handler that throws, so one faulting subscriber would silently deprive every later one of the event
    /// (the session-number release and the roster-cache forget are on this list) - trading a process crash
    /// for a quiet partial removal, which is harder to notice and just as wrong.
    /// </summary>
    private void RaiseDirectorRemoved(string directorId)
    {
        foreach (var handler in OnDirectorRemoved?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try
            {
                ((Action<string>)handler)(directorId);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorRegistry] OnDirectorRemoved subscriber FAILED for director={directorId}: {ex}");
            }
        }
    }

    // ===== HTTP path =====

    /// <summary>
    /// Add or refresh an HTTP-registered Director. Idempotent. The dto is stamped
    /// with <c>Source="http"</c> and <c>LastSeen=UtcNow</c>. If an FSW entry already
    /// exists for the same id, the HTTP entry replaces it (it has the tailnet endpoint).
    /// </summary>
    public DirectorDto Upsert(DirectorRegistrationRequest req)
    {
        if (string.IsNullOrEmpty(req.DirectorId))
            throw new ArgumentException("directorId is required", nameof(req));

        var now = DateTime.UtcNow;
        var dto = new DirectorDto
        {
            DirectorId = req.DirectorId,
            Pid = req.Pid,
            StartedAt = req.StartedAt == default ? now : req.StartedAt,
            ControlEndpoint = req.TailnetEndpoint, // HTTP path: the tailnet endpoint IS the control endpoint
            TailnetEndpoint = req.TailnetEndpoint,
            // Issue #324: a flagged no-endpoint registration carries the Director's own
            // reason; readers (fan-outs) must not probe an endpoint the Director declared dead.
            EndpointUnreachableReason = string.IsNullOrWhiteSpace(req.EndpointUnreachableReason) ? null : req.EndpointUnreachableReason,
            MachineName = req.MachineName,
            User = req.User,
            Version = req.Version,
            SchemaVersion = 1,
            LastSeen = now,
            Source = "http",
        };

        var existed = _directors.TryGetValue(req.DirectorId, out _);
        _directors[req.DirectorId] = dto;
        FileLog.Write(dto.EndpointUnreachableReason is null
            ? $"[DirectorRegistry] Upsert (http): id={dto.DirectorId}, endpoint={dto.TailnetEndpoint}, existed={existed}"
            : $"[DirectorRegistry] Upsert (http, FLAGGED no reachable endpoint): id={dto.DirectorId}, existed={existed}, reason={dto.EndpointUnreachableReason}");
        if (!existed)
            OnDirectorAdded?.Invoke(dto);
        return dto;
    }

    /// <summary>
    /// Gateway Cleanup mission (tunnel-only): register (or refresh) a Director from its DirectorHub stream
    /// Hello. The tunnel is the ONLY registration path now (HTTP register is gone), so a live stream IS the
    /// Director's presence. Stamped <c>Source="stream"</c> with an EMPTY control/tailnet endpoint - the Gateway
    /// never dials a Director, it only reaches it down this stream. Marks it state-reporting so the reconcile
    /// poll skips it. Idempotent; refreshes LastSeen on every Hello (connect + reconnect + periodic re-push).
    /// </summary>
    public DirectorDto RegisterFromStream(string directorId, string machineName, string user, string version, int pid, DateTime startedAt)
    {
        if (string.IsNullOrEmpty(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));

        var now = DateTime.UtcNow;
        // Merge with any existing entry: a Hello field that arrives empty must not wipe a value the entry
        // already carries (e.g. a file-discovered entry's machine name). Production Hellos always carry the
        // full identity; this just makes re-registration and mixed-source ordering safe.
        _directors.TryGetValue(directorId, out var existing);
        var dto = new DirectorDto
        {
            DirectorId = directorId,
            Pid = pid > 0 ? pid : (existing?.Pid ?? 0),
            StartedAt = startedAt != default ? startedAt : (existing?.StartedAt ?? now),
            ControlEndpoint = "",     // tunnel-only: the Gateway never dials this Director
            TailnetEndpoint = null,
            MachineName = !string.IsNullOrEmpty(machineName) ? machineName : (existing?.MachineName ?? ""),
            User = !string.IsNullOrEmpty(user) ? user : (existing?.User ?? ""),
            Version = !string.IsNullOrEmpty(version) ? version : (existing?.Version ?? ""),
            SchemaVersion = 1,
            LastSeen = now,
            Source = "stream",
        };
        var existed = existing is not null;
        _directors[directorId] = dto;
        _stateReporting.TryAdd(directorId, true);
        if (!existed)
        {
            FileLog.Write($"[DirectorRegistry] RegisterFromStream: id={directorId}, machine={machineName}, version={version}");
            OnDirectorAdded?.Invoke(dto);
        }
        return dto;
    }

    /// <summary>
    /// Refresh the heartbeat timestamp on an existing HTTP-registered Director.
    /// Returns false if the id is unknown (caller can choose to ask the Director to re-register).
    /// </summary>
    public bool Heartbeat(string directorId)
    {
        if (string.IsNullOrEmpty(directorId)) return false;
        if (!_directors.TryGetValue(directorId, out var existing)) return false;
        existing.LastSeen = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Mark a Director as PUSH-CAPABLE (issue #186): it rang the doorbell or sent a
    /// heartbeat carrying a session-state snapshot, so the Gateway's turn tracker does
    /// not need to poll it. File-discovered and old-build Directors never get marked
    /// and stay on the 15s reconcile poll. In-memory: resets on Gateway restart and
    /// re-establishes on the Director's first ping.
    /// </summary>
    public void MarkStateReporting(string directorId)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        if (_stateReporting.TryAdd(directorId, true))
            FileLog.Write($"[DirectorRegistry] {directorId} is state-reporting (doorbell/heartbeat push); reconcile poll skips it");
    }

    /// <summary>True when the Director pushes its own session-state signals.</summary>
    public bool IsStateReporting(string directorId)
        => !string.IsNullOrEmpty(directorId) && _stateReporting.ContainsKey(directorId);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _stateReporting = new();

    /// <summary>
    /// Remove a Director from the registry (HTTP graceful shutdown). Returns true if
    /// it was present.
    /// </summary>
    public bool Remove(string directorId)
    {
        if (string.IsNullOrEmpty(directorId)) return false;
        if (_directors.TryRemove(directorId, out _))
        {
            _everReachable.TryRemove(directorId, out _); // graceful goodbye: next process starts blank
            FileLog.Write($"[DirectorRegistry] Remove (http): id={directorId}");
            RaiseDirectorRemoved(directorId);
            return true;
        }
        return false;
    }

    // ===== FSW path =====

    /// <summary>Begin watching the instances directory and start the stale sweeper.</summary>
    public void Start()
    {
        FileLog.Write($"[DirectorRegistry] Start: watching {WatchDirectory}");
        Directory.CreateDirectory(WatchDirectory);

        LoadExisting();

        _watcher = new FileSystemWatcher(WatchDirectory, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Created += OnFileCreatedOrChanged;
        _watcher.Changed += OnFileCreatedOrChanged;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;

        // Stale sweeper - every 30s.
        // FSW entries: drop if file gone or PID dead.
        // HTTP entries: drop if LastSeen older than HttpHeartbeatTimeout.
        _sweeper = new Timer(_ => SweepStale(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Snapshot of all currently-known Directors.</summary>
    public IReadOnlyCollection<DirectorDto> ListDirectors()
        => _directors.Values.ToList().AsReadOnly();

    /// <summary>Look up by Director ID. Null if unknown.</summary>
    public DirectorDto? Get(string directorId)
        => _directors.TryGetValue(directorId, out var d) ? d : null;

    private void LoadExisting()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(WatchDirectory, "*.json"))
                TryParseAndAdd(f);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRegistry] LoadExisting FAILED: {ex.Message}");
        }
    }

    private void OnFileCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        FileLog.Write($"[DirectorRegistry] File created/changed: {e.Name}");
        // FileSystemWatcher fires before write is complete on some systems - retry briefly.
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (TryParseAndAdd(e.FullPath)) return;
                await Task.Delay(100);
            }
            FileLog.Write($"[DirectorRegistry] Could not parse {e.Name} after retries");
        });
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        FileLog.Write($"[DirectorRegistry] File deleted: {e.Name}");
        var id = Path.GetFileNameWithoutExtension(e.Name ?? "");
        if (string.IsNullOrEmpty(id)) return;
        // Only remove if the entry came from the FSW path. An HTTP entry must not be
        // wiped by a stray file delete - it lives by its own heartbeat lifecycle.
        if (_directors.TryGetValue(id, out var existing) && existing.Source == "file")
        {
            if (_directors.TryRemove(id, out _))
                RaiseDirectorRemoved(id);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        var oldId = Path.GetFileNameWithoutExtension(e.OldName ?? "");
        if (!string.IsNullOrEmpty(oldId)
            && _directors.TryGetValue(oldId, out var existing)
            && existing.Source == "file"
            && _directors.TryRemove(oldId, out _))
        {
            RaiseDirectorRemoved(oldId);
        }
        TryParseAndAdd(e.FullPath);
    }

    private bool TryParseAndAdd(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return false;

            var dto = JsonSerializer.Deserialize<DirectorDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (dto is null || string.IsNullOrEmpty(dto.DirectorId)) return false;

            // If an HTTP-registered entry exists for the same id, leave it alone.
            // HTTP carries the tailnet endpoint which FSW cannot supply.
            if (_directors.TryGetValue(dto.DirectorId, out var existing) && existing.Source == "http")
            {
                FileLog.Write($"[DirectorRegistry] Skipping FSW upsert for id={dto.DirectorId}: HTTP entry already present");
                return true;
            }

            dto.LastSeen = DateTime.UtcNow;
            dto.Source = "file";
            var wasNew = !_directors.ContainsKey(dto.DirectorId);
            _directors[dto.DirectorId] = dto;
            if (wasNew) OnDirectorAdded?.Invoke(dto);
            FileLog.Write($"[DirectorRegistry] Added (file): id={dto.DirectorId}, endpoint={dto.ControlEndpoint}");
            return true;
        }
        catch (IOException) { return false; /* file still being written */ }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRegistry] TryParseAndAdd FAILED for {path}: {ex.Message}");
            return false;
        }
    }

    private void SweepStale()
    {
        if (_disposed) return;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _directors.ToArray())
            {
                // Gateway Cleanup mission (tunnel-only): "stream" entries are aged out exactly like "http" -
                // by staleness. A connected Director refreshes LastSeen on every Hello + the ~10s periodic
                // re-push, so it never ages; a Director whose tunnel closed stops refreshing and is swept after
                // HttpHeartbeatTimeout. This is why the DirectorHub does NOT drop the entry the instant the
                // stream closes: a dead Director's cached roster must survive the sweep window so a Gateway-owned
                // snooze can still fire it back to "needs you" from the cache, and a brief reconnect blip never
                // flaps the roster.
                if (kv.Value.Source == "http" || kv.Value.Source == "stream")
                {
                    var lastSeen = kv.Value.LastSeen ?? DateTime.MinValue;
                    if (now - lastSeen > HttpHeartbeatTimeout)
                    {
                        if (_directors.TryRemove(kv.Key, out _))
                        {
                            _everReachable.TryRemove(kv.Key, out _);
                            FileLog.Write($"[DirectorRegistry] Sweeper removed stale {kv.Value.Source} entry: {kv.Key} (last seen {(now - lastSeen).TotalSeconds:F0}s ago)");
                            RaiseDirectorRemoved(kv.Key);
                        }
                    }
                    continue;
                }

                // FSW path: file gone or PID dead.
                var f = Path.Combine(WatchDirectory, $"{kv.Key}.json");
                if (!File.Exists(f))
                {
                    if (_directors.TryRemove(kv.Key, out _))
                    {
                        FileLog.Write($"[DirectorRegistry] Sweeper removed orphan (file gone): {kv.Key}");
                        RaiseDirectorRemoved(kv.Key);
                    }
                    continue;
                }

                var pid = kv.Value.Pid;
                if (pid > 0)
                {
                    try { System.Diagnostics.Process.GetProcessById(pid); }
                    catch (ArgumentException) // process not running
                    {
                        // Issue #891: do NOT swallow a failed delete. If the file is locked or we lack
                        // permission it stays on disk; the orphan-file sweep below completes the delete
                        // on a later pass (this loop iterates the in-memory roster, from which the entry
                        // is about to be removed, so it would otherwise never be revisited).
                        try { File.Delete(f); }
                        catch (Exception ex)
                        {
                            FileLog.Write($"[DirectorRegistry] Sweeper could not delete instance file for {kv.Key} (pid {pid} dead); orphan sweep will retry: {ex.Message}");
                        }
                        if (_directors.TryRemove(kv.Key, out _))
                        {
                            FileLog.Write($"[DirectorRegistry] Sweeper removed orphan (pid {pid} dead): {kv.Key}");
                            RaiseDirectorRemoved(kv.Key);
                        }
                    }
                    catch { /* permission errors etc - leave it for next pass */ }
                }
            }

            SweepOrphanInstanceFiles();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRegistry] SweepStale error: {ex.Message}");
        }
    }

    /// <summary>
    /// Directory-level safety net (issue #891). The in-memory sweep above removes a dead Director from
    /// the roster even if deleting its backing instance file failed (locked, permission). Because that
    /// loop iterates the in-memory dictionary, such a file is never revisited and, on the next Gateway
    /// restart, <see cref="LoadExisting"/> re-imports it - which can resurrect a phantom Director if the
    /// recorded process id has since been recycled to an unrelated live process. This scan completes the
    /// delete: it enumerates the watch directory for files that have NO live in-memory entry and whose
    /// recorded process id is dead, and deletes them. Files with a live entry are handled by the sweep
    /// above; files whose process is still alive, or that predate pid stamping (pid &lt;= 0), are left
    /// untouched so a healthy Director's file is never removed.
    /// </summary>
    internal void SweepOrphanInstanceFiles()
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(WatchDirectory, "*.json");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRegistry] Orphan-file sweep could not enumerate {WatchDirectory}: {ex.Message}");
            return;
        }

        foreach (var f in files)
        {
            var id = Path.GetFileNameWithoutExtension(f);
            // A file backing a still-known Director is the in-memory sweep's responsibility; leave it.
            if (!string.IsNullOrEmpty(id) && _directors.ContainsKey(id))
                continue;

            int pid;
            try
            {
                var json = File.ReadAllText(f);
                if (string.IsNullOrWhiteSpace(json)) continue; // being written; try next pass
                var dto = JsonSerializer.Deserialize<DirectorDto>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
                if (dto is null) continue;
                pid = dto.Pid;
            }
            catch (IOException) { continue; /* still being written */ }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorRegistry] Orphan-file sweep could not read {Path.GetFileName(f)}: {ex.Message}");
                continue;
            }

            // Only delete when we can PROVE the process is gone. pid <= 0 predates pid stamping, so we
            // cannot prove death and must leave the file rather than risk deleting a live Director's.
            if (pid <= 0 || !IsProcessDead(pid))
                continue;

            try
            {
                File.Delete(f);
                FileLog.Write($"[DirectorRegistry] Orphan-file sweep deleted stale instance file {Path.GetFileName(f)} (pid {pid} dead)");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorRegistry] Orphan-file sweep could not delete {Path.GetFileName(f)} (pid {pid} dead); will retry next sweep: {ex.Message}");
            }
        }
    }

    /// <summary>True when no process with <paramref name="pid"/> is currently running. A permission or
    /// other unexpected error returns false (do not assume dead) so we never delete on uncertainty.</summary>
    private static bool IsProcessDead(int pid)
    {
        try
        {
            using var _ = System.Diagnostics.Process.GetProcessById(pid);
            return false;
        }
        catch (ArgumentException) { return true; }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sweeper?.Dispose();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
