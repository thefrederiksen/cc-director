using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// A machine-local, synchronous reservation a session holds on its working directory while it is
/// alive - the coordination the destructive worktree reaper needs and the Gateway roster cannot
/// provide (inspection). A worktree is a LOCAL folder, so only sessions on THIS machine can be in
/// it; when ANY Director slot launches a session it writes a reservation here, and the reaper (in
/// any slot) refuses to remove a worktree that a live reservation covers. Because it is a local
/// filesystem write taken AT LAUNCH, there is no Gateway propagation delay and no partial-roster
/// window: a session that has started has already reserved its worktree before the reaper could see
/// it, and a session in a SUBDIRECTORY reserves and protects the whole worktree.
///
/// Crash safety: a reservation records the OWNING Director's process id and start time. Sessions are
/// children of their Director, so if that Director process is gone the sessions are gone too and the
/// reservation is stale - any reader prunes it. A missed <see cref="Release"/> while the Director is
/// still alive only OVER-protects (a worktree is spared until the Director restarts), which is the
/// safe direction.
/// </summary>
public sealed class WorktreeReservationStore
{
    private readonly string _dir;
    private readonly int _ownerPid;
    private readonly DateTime _ownerStartUtc;
    private readonly Func<int, DateTime?> _processStartUtc;

    /// <param name="dir">Reservation directory (defaults to the machine-local cc-director path).</param>
    /// <param name="ownerPid">This Director's process id (test seam; defaults to the current process).</param>
    /// <param name="ownerStartUtc">This Director's process start time (test seam).</param>
    /// <param name="processStartUtc">Returns a pid's start time in UTC, or null when the process is not
    /// alive (test seam; defaults to a real process lookup).</param>
    public WorktreeReservationStore(
        string? dir = null,
        int? ownerPid = null,
        DateTime? ownerStartUtc = null,
        Func<int, DateTime?>? processStartUtc = null)
    {
        _dir = dir ?? DefaultDir();
        var self = System.Diagnostics.Process.GetCurrentProcess();
        _ownerPid = ownerPid ?? self.Id;
        _ownerStartUtc = ownerStartUtc ?? self.StartTime.ToUniversalTime();
        _processStartUtc = processStartUtc ?? DefaultProcessStartUtc;
    }

    private static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cc-director", "worktree-reservations");

    private sealed record Reservation(string WorktreePath, int OwnerPid, DateTime OwnerStartUtc, string SessionId);

    /// <summary>Reserve <paramref name="workingDirectory"/> for a live session. Best-effort - never
    /// throws into the session lifecycle.</summary>
    public void Reserve(string workingDirectory, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(sessionId))
            return;
        try
        {
            Directory.CreateDirectory(_dir);
            var row = new Reservation(WorktreeReaperService.NormalizePath(workingDirectory), _ownerPid, _ownerStartUtc, sessionId);
            File.WriteAllText(FileFor(sessionId), JsonSerializer.Serialize(row));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeReservationStore] Reserve failed for {sessionId}: {ex.Message}");
        }
    }

    /// <summary>Release a session's reservation. Best-effort.</summary>
    public void Release(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;
        try
        {
            var f = FileFor(sessionId);
            if (File.Exists(f))
                File.Delete(f);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeReservationStore] Release failed for {sessionId}: {ex.Message}");
        }
    }

    /// <summary>
    /// The normalized worktree paths currently reserved by a LIVE session (its owning Director is
    /// alive). Prunes reservations whose owning Director is gone as it reads.
    /// </summary>
    public IReadOnlySet<string> LiveReservedPaths()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_dir))
            return live;

        string[] files;
        try { files = Directory.GetFiles(_dir, "*.json"); }
        catch (Exception ex) { FileLog.Write($"[WorktreeReservationStore] enumerate failed: {ex.Message}"); return live; }

        foreach (var file in files)
        {
            Reservation? r;
            try { r = JsonSerializer.Deserialize<Reservation>(File.ReadAllText(file)); }
            catch { r = null; }
            if (r is null || string.IsNullOrWhiteSpace(r.WorktreePath))
            {
                TryDelete(file);
                continue;
            }

            var start = _processStartUtc(r.OwnerPid);
            // Alive when the owning Director's pid exists AND its start time matches (guards against a
            // reused pid). If the start time cannot be read at all we keep the reservation (fail safe).
            bool alive = start is null
                ? false
                : Math.Abs((start.Value - r.OwnerStartUtc).TotalSeconds) < 2;
            if (alive)
                live.Add(r.WorktreePath);
            else
                TryDelete(file); // the Director is gone, so its sessions are gone - stale
        }
        return live;
    }

    private string FileFor(string sessionId)
    {
        // sessionId is a Guid/opaque id; sanitize defensively for a filename.
        var safe = string.Concat(sessionId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(_dir, safe + ".json");
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { /* another reader won the race - fine */ }
    }

    private static DateTime? DefaultProcessStartUtc(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime();
        }
        catch
        {
            return null; // not running (or not inspectable) - treat as gone
        }
    }
}
