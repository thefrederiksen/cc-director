using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// The durable on-disk store for desktop fire-and-forget dictations (issue #1130). Every Send writes
/// its recorded audio here BEFORE any transcription call, so a failed or slow transcription, an
/// unreachable transcription server, or an application crash can never lose the spoken words: the clip
/// is retried in the background and re-driven on the next launch. A record is deleted only once the
/// dictation has been transcribed and delivered into its session.
///
/// Layout, under <c>%LOCALAPPDATA%\cc-director\pending-dictations\</c> (or a test-supplied directory):
///   &lt;id&gt;.wav   - the recorded PCM WAV, exactly as the microphone produced it
///   &lt;id&gt;.json  - the <see cref="PendingDictation"/> sidecar (target session, prefix, attempts, error)
///
/// This is the desktop peer of the mobile server-owned durable upload (issue #1006). The store is
/// deliberately UI-free and takes its directory by injection, so it is fully unit-testable against a
/// temp folder with no application state.
/// </summary>
public sealed class PendingDictationStore
{
    private readonly string _dir;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <param name="directory">The folder to store clips in. Defaults to the shared
    /// <see cref="CcStorage.PendingDictations"/> location; tests pass a temp directory.</param>
    public PendingDictationStore(string? directory = null)
    {
        _dir = string.IsNullOrWhiteSpace(directory) ? CcStorage.PendingDictations() : directory;
    }

    /// <summary>The directory this store writes to (for diagnostics and user-facing "saved at" messages).</summary>
    public string Directory => _dir;

    private string WavPath(string id) => Path.Combine(_dir, id + ".wav");
    private string SidecarPath(string id) => Path.Combine(_dir, id + ".json");

    /// <summary>
    /// Persist a recorded clip durably and return its record. The audio is written first, then the
    /// sidecar, so a record is never visible without its audio. Throws if the write fails - the caller
    /// must know durability was NOT achieved (the mobile contract's "not persisted" branch) rather than
    /// believe a clip is safe when it is not.
    /// </summary>
    public PendingDictation Save(string sessionId, string prefix, byte[] wav, string before = "", string after = "")
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
        if (wav is null || wav.Length == 0) throw new ArgumentException("wav is empty", nameof(wav));

        var record = new PendingDictation
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Prefix = prefix ?? "",
            Before = before ?? "",
            After = after ?? "",
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            AttemptCount = 0,
            LastError = null,
            Status = PendingDictationStatus.Pending,
        };

        System.IO.Directory.CreateDirectory(_dir);
        lock (_gate)
        {
            File.WriteAllBytes(WavPath(record.Id), wav);
            File.WriteAllText(SidecarPath(record.Id), JsonSerializer.Serialize(record, JsonOptions));
        }
        FileLog.Write($"[PendingDictationStore] saved {wav.Length} bytes: id={record.Id}, session={sessionId}");
        return record;
    }

    /// <summary>Read the recorded audio for a saved clip. Throws if the audio file is missing.</summary>
    public byte[] ReadAudio(PendingDictation pending)
    {
        if (pending is null) throw new ArgumentNullException(nameof(pending));
        return File.ReadAllBytes(WavPath(pending.Id));
    }

    /// <summary>True when the clip's audio file still exists on disk.</summary>
    public bool HasAudio(PendingDictation pending)
        => pending is not null && File.Exists(WavPath(pending.Id));

    /// <summary>
    /// Record a failed delivery attempt: bump the attempt count, store the error, and set the status so
    /// the sweeper knows whether to keep auto-retrying (<see cref="PendingDictationStatus.Pending"/>) or
    /// park it for user action (<see cref="PendingDictationStatus.NeedsAttention"/>). Returns the
    /// updated record. The audio is untouched.
    /// </summary>
    public PendingDictation RecordFailedAttempt(PendingDictation pending, string error, PendingDictationStatus status)
    {
        if (pending is null) throw new ArgumentNullException(nameof(pending));
        var updated = pending with
        {
            AttemptCount = pending.AttemptCount + 1,
            LastError = Truncate(error, 500),
            Status = status,
        };
        WriteSidecar(updated);
        FileLog.Write($"[PendingDictationStore] attempt {updated.AttemptCount} failed: id={updated.Id}, status={status}, error={updated.LastError}");
        return updated;
    }

    /// <summary>Overwrite a clip's sidecar with a new record (e.g. promoting NeedsAttention to Pending on launch).</summary>
    public void WriteSidecar(PendingDictation pending)
    {
        if (pending is null) throw new ArgumentNullException(nameof(pending));
        System.IO.Directory.CreateDirectory(_dir);
        lock (_gate)
        {
            File.WriteAllText(SidecarPath(pending.Id), JsonSerializer.Serialize(pending, JsonOptions));
        }
    }

    /// <summary>Delete a delivered (or pruned) clip: both its audio and its sidecar. Best-effort and idempotent.</summary>
    public void Delete(PendingDictation pending)
    {
        if (pending is null) throw new ArgumentNullException(nameof(pending));
        lock (_gate)
        {
            TryDeleteFile(WavPath(pending.Id));
            TryDeleteFile(SidecarPath(pending.Id));
        }
        FileLog.Write($"[PendingDictationStore] deleted id={pending.Id}");
    }

    /// <summary>
    /// Load every saved clip, oldest first (the order they should be re-driven). A sidecar without its
    /// audio, or one that will not parse, is skipped (and the orphan cleaned up) rather than failing the
    /// whole scan - one corrupt record must not block the rest from being recovered.
    /// </summary>
    public IReadOnlyList<PendingDictation> LoadAll()
    {
        if (!System.IO.Directory.Exists(_dir)) return Array.Empty<PendingDictation>();

        var records = new List<PendingDictation>();
        foreach (var sidecar in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
        {
            PendingDictation? record = null;
            try
            {
                record = JsonSerializer.Deserialize<PendingDictation>(File.ReadAllText(sidecar));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[PendingDictationStore] skipping unreadable sidecar {Path.GetFileName(sidecar)}: {ex.Message}");
            }

            if (record is null || string.IsNullOrWhiteSpace(record.Id))
                continue;

            if (!File.Exists(WavPath(record.Id)))
            {
                // Sidecar with no audio: nothing to deliver, clean up the orphan.
                FileLog.Write($"[PendingDictationStore] orphan sidecar (no audio) id={record.Id}; removing");
                TryDeleteFile(sidecar);
                continue;
            }

            records.Add(record);
        }

        records.Sort((a, b) => string.CompareOrdinal(a.CreatedUtc, b.CreatedUtc));
        return records;
    }

    /// <summary>
    /// Delete clips older than <paramref name="maxAge"/> - the only place a saved dictation is discarded
    /// without being delivered. The window is generous (days, not the mobile hour) because on the
    /// desktop the audio is the single copy; a clip is dropped only when it is so old its session is
    /// certainly gone. Returns how many were pruned.
    /// </summary>
    public int PruneOlderThan(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var pruned = 0;
        foreach (var record in LoadAll())
        {
            if (DateTime.TryParse(record.CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var created)
                && created.ToUniversalTime() < cutoff)
            {
                FileLog.Write($"[PendingDictationStore] pruning stale clip id={record.Id}, created={record.CreatedUtc}");
                Delete(record);
                pruned++;
            }
        }
        return pruned;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? "") : s[..max] + "...";

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { FileLog.Write($"[PendingDictationStore] delete file failed ({path}): {ex.Message}"); }
    }
}
