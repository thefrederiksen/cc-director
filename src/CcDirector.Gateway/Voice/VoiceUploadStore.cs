using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Voice;

/// <summary>
/// Gateway-side resumable upload staging for the guaranteed audio-turn front door.
///
/// The phone records with a MediaRecorder timeslice, so the audio arrives as an ordered
/// sequence of container fragments. Each fragment is stored to a per-upload dir as it lands
/// (SHA256-idempotent, so a retried chunk after a dropped connection is a free no-op), then
/// <see cref="Assemble"/> concatenates them IN ORDER back into one blob. A fragment is NOT
/// independently decodable (only fragment 0 carries the container header), so
/// reassemble-then-forward is the correct model.
///
/// Why this lives on the Gateway (not reusing the Director's <c>/voice/utterance</c> path):
/// the Director's complete step transcribes and posts text to the session - a different flow
/// that produces no audio reply. Here the Gateway buffers the chunks itself and then feeds the
/// assembled clip into the existing async voice-turn worker, so the resumable upload and the
/// audio-reply turn become one pipeline behind a single Gateway URL.
///
/// Transient by design for the voice-turn path: the per-upload dir is deleted once the turn has been
/// started (<see cref="Delete"/> + the age-based <see cref="SweepAbandoned"/>).
///
/// The durable dictation path (issue #1183) adds a per-upload-id DELIVERY RECORD on top of the same
/// staging: while an upload is undelivered it stays PENDING (its chunks are retained for resume, never
/// age-swept), and on delivery or abandonment it becomes a small terminal tombstone
/// (<see cref="MarkDelivered"/> / <see cref="MarkAbandoned"/>) that discards the heavy chunk bytes but
/// keeps the outcome marker, so a delivered upload id is de-duplicated forever - across time and a
/// Gateway restart - until the client acknowledges it (<see cref="Acknowledge"/>). See
/// <see cref="DictationDeliveryRecord"/> for the model.
/// </summary>
public sealed class VoiceUploadStore
{
    private readonly string _root;

    public VoiceUploadStore() : this(CcStorage.VoiceTurnUploads()) { }

    /// <summary>Test seam: stage under an explicit root instead of the shared storage dir.</summary>
    public VoiceUploadStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Begin (or re-open) an upload. The caller supplies a GUID id (it is also the
    /// idempotency key for the resulting turn); a missing/blank id mints a fresh one.
    /// Idempotent: re-registering the same id just ensures the folder exists.
    /// </summary>
    public string Register(string? uploadId)
    {
        var uid = NormalizeId(uploadId) ?? Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(DirFor(uid));
        FileLog.Write($"[VoiceUploadStore] Register: uploadId={uid}");
        return uid;
    }

    /// <summary>True once <see cref="Register"/> has staged this upload (and it has not been swept).</summary>
    public bool Exists(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        return uid is not null && Directory.Exists(DirFor(uid));
    }

    /// <summary>
    /// The bytes this upload currently occupies on disk, optionally IGNORING one chunk index.
    ///
    /// The caller uses this to enforce the total-upload ceiling before staging another chunk. The
    /// exclusion is what makes that safe under the store's own idempotency: re-sending chunk 5 REPLACES
    /// chunk 5, it does not add to the total, so counting the copy already on disk would push a
    /// perfectly legal retry over the ceiling - and retries are the normal case on the mobile path this
    /// serves, not the exception.
    ///
    /// Returns 0 for an unknown upload. Best-effort: a chunk that vanishes mid-enumeration (the sweeper)
    /// is skipped rather than throwing, because this is a guard rail, not an accounting record.
    /// </summary>
    public long StagedBytes(string uploadId, int? excludeIndex = null)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return 0;
        var dir = DirFor(uid);
        if (!Directory.Exists(dir)) return 0;

        var exclude = excludeIndex is { } i ? ChunkPath(dir, i) : null;
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            if (exclude is not null && string.Equals(path, exclude, StringComparison.OrdinalIgnoreCase)) continue;
            try { total += new FileInfo(path).Length; }
            catch (FileNotFoundException) { /* swept mid-enumeration */ }
            catch (DirectoryNotFoundException) { /* swept mid-enumeration */ }
        }
        return total;
    }

    /// <summary>
    /// Store one chunk. Idempotent on (index, bytes): a chunk already on disk with the same
    /// SHA256 is accepted without rewriting, so retries are free. A supplied SHA that does not
    /// match the bytes is rejected so corruption never enters the assembly.
    /// </summary>
    public async Task StoreChunkAsync(string uploadId, int index, byte[] bytes, string? expectedSha, CancellationToken ct = default)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        if (index < 0) throw new InvalidOperationException("chunk index must be >= 0");
        if (bytes.Length == 0) throw new InvalidOperationException("empty chunk");

        var actualSha = Sha256Hex(bytes);
        if (!string.IsNullOrEmpty(expectedSha) &&
            !string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"chunk {index} SHA mismatch: header={expectedSha} actual={actualSha}");
        }

        var dir = DirFor(uid);
        Directory.CreateDirectory(dir);
        var path = ChunkPath(dir, index);

        // Idempotent: identical chunk already on disk -> no-op.
        if (File.Exists(path) &&
            string.Equals(Sha256Hex(await File.ReadAllBytesAsync(path, ct)), actualSha, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[VoiceUploadStore] StoreChunk: uploadId={uid} index={index} already present (idempotent)");
            return;
        }

        // Atomic write (temp + move) so a half-written chunk never poisons the assembly.
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, path, overwrite: true);
        FileLog.Write($"[VoiceUploadStore] StoreChunk: uploadId={uid} index={index} bytes={bytes.Length}");
    }

    /// <summary>
    /// Reassemble chunks 0..totalChunks-1 in order. When a chunk is missing the partial upload
    /// is preserved (not discarded) and <see cref="AssembleResult.Missing"/> lists the indices the
    /// client must re-send. On success <see cref="AssembleResult.Audio"/> carries the full blob.
    /// </summary>
    public async Task<AssembleResult> AssembleAsync(string uploadId, int totalChunks, CancellationToken ct = default)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        var dir = DirFor(uid);
        if (!Directory.Exists(dir))
            return AssembleResult.Unknown();
        if (totalChunks <= 0)
            throw new InvalidOperationException("totalChunks must be > 0");

        // Completeness gate (issue #586 contract, applied here for the phone push-to-talk upload,
        // issue #592): every index 0..totalChunks-1 must be present AND non-empty. A missing OR
        // zero-byte chunk is "incomplete" - the result names the exact indices to re-send and NO
        // assembled clip is produced, so a truncated upload is refused, never transcribed.
        var missing = new List<int>();
        for (var i = 0; i < totalChunks; i++)
        {
            var path = ChunkPath(dir, i);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) missing.Add(i);
        }
        if (missing.Count > 0)
        {
            FileLog.Write($"[VoiceUploadStore] Assemble: uploadId={uid} INCOMPLETE missing={string.Join(',', missing)}");
            return AssembleResult.Incomplete(missing);
        }

        using var assembled = new MemoryStream();
        for (var i = 0; i < totalChunks; i++)
        {
            var part = await File.ReadAllBytesAsync(ChunkPath(dir, i), ct);
            assembled.Write(part, 0, part.Length);
        }
        var bytes = assembled.ToArray();
        FileLog.Write($"[VoiceUploadStore] Assemble: uploadId={uid} chunks={totalChunks} totalBytes={bytes.Length}");
        return AssembleResult.Ok(bytes);
    }

    /// <summary>
    /// Delete staging dirs whose last write is older than <paramref name="maxAge"/> — abandoned uploads
    /// whose client dropped before completing (the staging is only deleted on success, so without this
    /// an interrupted upload would leak forever). Best-effort per dir; returns how many were removed.
    /// </summary>
    public int SweepAbandoned(TimeSpan maxAge)
    {
        var removed = 0;
        var cutoff = DateTime.UtcNow - maxAge;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    {
                        Directory.Delete(dir, recursive: true);
                        removed++;
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[VoiceUploadStore] Sweep dir={dir} failed: {ex.Message}");
                }
            }
            if (removed > 0) FileLog.Write($"[VoiceUploadStore] SweepAbandoned removed={removed} older than {maxAge}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] SweepAbandoned failed: {ex.Message}");
        }
        return removed;
    }

    /// <summary>Delete the staging dir for an upload. Best-effort; called once the turn is started.</summary>
    public void Delete(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return;
        try
        {
            var dir = DirFor(uid);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] Delete uploadId={uid} failed: {ex.Message}");
        }
    }

    // ====== durable delivery record (issue #1183) ===================================
    // One durable record per upload id with three states. PENDING is the ABSENCE of a terminal record
    // while the staging dir exists (its chunks are retained for resume). DELIVERED and ABANDONED are
    // terminal tombstones written as record.json AFTER the heavy chunk bytes are discarded. The tombstone
    // is the durable "already resolved" marker and is retired only by a client acknowledgment - so it is
    // exactly as long-lived as the audio it guards, and a delivered/abandoned upload id never re-injects.

    /// <summary>
    /// The durable delivery record for this upload id, or null when there is none (an unknown id, or a
    /// staged upload that was never given a marker). Read from disk, so it survives a Gateway restart (a
    /// fresh store instance over the same root finds it).
    /// </summary>
    public DictationDeliveryRecord? ReadRecord(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        return uid is null ? null : ReadRecordFile(RecordPath(DirFor(uid)));
    }

    private static DictationDeliveryRecord? ReadRecordFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<DictationDeliveryRecord>(File.ReadAllText(path), RecordJson);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] ReadRecord {path} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// True when this upload id holds an explicit PENDING marker (issue #1188): undelivered and not
    /// abandoned, its chunks retained. The enforced per-session dictation lock is a projection of this.
    /// </summary>
    public bool IsPending(string uploadId) => ReadRecord(uploadId) is { State: DictationDeliveryState.Pending };

    /// <summary>
    /// Write the explicit durable PENDING marker for this upload id, carrying the owning session id (issue
    /// #1188). While a PENDING marker exists the session is LOCKED for human input (a projection read by
    /// <see cref="IsSessionLocked"/>). Called at register so the session id is on disk and the lock survives
    /// a Gateway restart. Keeps any staged chunks and overwrites a prior PENDING or FAILED marker (a retry
    /// re-entry back to PENDING); the DELIVERED/ABANDONED short-circuit means those never reach here.
    /// </summary>
    public void MarkPending(string uploadId, string sessionId)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        var dir = DirFor(uid);
        Directory.CreateDirectory(dir);
        WriteRecordMarker(dir, new DictationDeliveryRecord(
            DictationDeliveryState.Pending, false, false, "", null, sessionId ?? ""));
        FileLog.Write($"[VoiceUploadStore] MarkPending: uploadId={uid} sessionId={sessionId}");
    }

    /// <summary>
    /// True when any dictation upload is PENDING for this session (issue #1188): the enforced session lock is
    /// a pure projection of the durable PENDING marker - it NEVER auto-releases; it clears only when every
    /// record for the session has left PENDING (delivered / abandoned / failed). Computed from disk, so it
    /// survives a Gateway restart. The Gateway's OWN dictation injection reaches the session directly through
    /// the Director control API, not the guarded Gateway front door, so it is not blocked by this lock.
    /// </summary>
    public bool IsSessionLocked(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        foreach (var rec in EnumeratePendingRecords())
            if (string.Equals(rec.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The distinct session ids that currently hold a PENDING dictation (issue #1188).</summary>
    public IReadOnlyCollection<string> LockedSessionIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in EnumeratePendingRecords())
            if (!string.IsNullOrWhiteSpace(rec.SessionId)) set.Add(rec.SessionId);
        return set;
    }

    private IEnumerable<DictationDeliveryRecord> EnumeratePendingRecords()
    {
        string[] dirs;
        try { dirs = Directory.Exists(_root) ? Directory.GetDirectories(_root) : Array.Empty<string>(); }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] enumerate pending failed: {ex.Message}");
            dirs = Array.Empty<string>();
        }
        foreach (var dir in dirs)
            if (ReadRecordFile(RecordPath(dir)) is { State: DictationDeliveryState.Pending } rec)
                yield return rec;
    }

    /// <summary>
    /// Transition this upload id to the durable DELIVERED tombstone: persist the submitted outcome, then
    /// discard the heavy chunk bytes (the turn is resolved and resume is no longer needed) while keeping the
    /// small marker. Idempotent: re-marking an already-delivered id rewrites the same tombstone. The
    /// tombstone is retired only by <see cref="Acknowledge"/>.
    /// </summary>
    public void MarkDelivered(string uploadId, bool submitted, bool movedOn, string transcript)
        => WriteTombstone(uploadId, new DictationDeliveryRecord(
            DictationDeliveryState.Delivered, submitted, movedOn, transcript ?? "", null, ExistingSessionId(uploadId)));

    /// <summary>
    /// Transition this upload id to the durable ABANDONED tombstone: persist the reason and discard the
    /// chunk bytes. Terminal and not-undelivered, so the session lock is off. The abandon WRITE triggers
    /// from the surfaces are a later task; this provides the state and its read side.
    /// </summary>
    public void MarkAbandoned(string uploadId, string reason)
        => WriteTombstone(uploadId, new DictationDeliveryRecord(
            DictationDeliveryState.Abandoned, false, false, "", reason ?? "", ExistingSessionId(uploadId)));

    /// <summary>
    /// Park this upload id as FAILED with a permanent-failure reason code (issue #1185). Unlike DELIVERED
    /// and ABANDONED, FAILED is NOT a terminal tombstone and NOT a de-dupe short-circuit: it is a
    /// user-retryable pause that KEEPS the staged chunk bytes, so an explicit retry can re-complete without
    /// a full re-upload. <see cref="IsPending"/> is false while FAILED (the session is not locked, which
    /// stops the client auto-loop); <see cref="ClearFailed"/> puts it back to PENDING for the retry.
    /// </summary>
    public void MarkFailed(string uploadId, string reasonCode)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        var dir = DirFor(uid);
        Directory.CreateDirectory(dir);
        // Write ONLY the marker - keep the chunk bytes (the retry re-drives them), the opposite of a tombstone.
        // Preserve the owning session id so a later ClearFailed can restore a PENDING marker that re-locks it.
        WriteRecordMarker(dir, new DictationDeliveryRecord(
            DictationDeliveryState.Failed, false, false, "", reasonCode ?? "", ExistingSessionId(uid)));
        FileLog.Write($"[VoiceUploadStore] MarkFailed: uploadId={uid} reason={reasonCode} (chunks retained)");
    }

    /// <summary>
    /// If this upload id is parked FAILED, clear it back to an explicit PENDING marker - preserving the
    /// owning session id (which re-locks the session while the retry re-drives) and KEEPING the staged chunks
    /// - so an explicit retry re-completes without a full re-upload (issue #1185, updated for #1188). A no-op
    /// returning false for any other state: DELIVERED and ABANDONED tombstones are left intact (their
    /// short-circuit stands).
    /// </summary>
    public bool ClearFailed(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return false;
        if (ReadRecord(uid) is not { State: DictationDeliveryState.Failed } failed) return false;
        try
        {
            WriteRecordMarker(DirFor(uid), new DictationDeliveryRecord(
                DictationDeliveryState.Pending, false, false, "", null, failed.SessionId));
            FileLog.Write($"[VoiceUploadStore] ClearFailed: uploadId={uid} back to PENDING (chunks retained)");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] ClearFailed uploadId={uid} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Retire the terminal tombstone for this upload id once the client has acknowledged it. Idempotent: a
    /// no-op returning false when the record is already gone. The server retires a tombstone ONLY on this
    /// client ack, so a delivered/abandoned upload id is de-duplicated for as long as the client could
    /// still re-drive it; a lost ack simply leaves the tiny marker and a later re-complete re-acks.
    /// </summary>
    public bool Acknowledge(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return false;
        var dir = DirFor(uid);
        if (!Directory.Exists(dir)) return false;
        try
        {
            Directory.Delete(dir, recursive: true);
            FileLog.Write($"[VoiceUploadStore] Acknowledge: uploadId={uid} tombstone retired");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] Acknowledge uploadId={uid} failed: {ex.Message}");
            return false;
        }
    }

    // The owning session id already recorded for this upload id (empty when there is no record yet), so a
    // state transition preserves the session id first written by MarkPending at register (issue #1188).
    private string ExistingSessionId(string uploadId) => ReadRecord(uploadId)?.SessionId ?? "";

    // Write the small durable marker first (atomic temp+move), THEN discard the heavy chunk bytes, so a
    // crash between the two leaves a valid tombstone rather than orphaned chunks with no marker. Used by the
    // TERMINAL transitions (delivered/abandoned) that no longer need the audio; FAILED keeps its bytes and
    // so writes the marker alone (see MarkFailed).
    private void WriteTombstone(string uploadId, DictationDeliveryRecord record)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        var dir = DirFor(uid);
        Directory.CreateDirectory(dir);
        WriteRecordMarker(dir, record);
        foreach (var part in Directory.EnumerateFiles(dir, "*.part"))
        {
            try { File.Delete(part); }
            catch (Exception ex) { FileLog.Write($"[VoiceUploadStore] discard chunk {part} failed: {ex.Message}"); }
        }
        FileLog.Write($"[VoiceUploadStore] MarkRecord: uploadId={uid} state={record.State} " +
            $"submitted={record.Submitted} movedOn={record.MovedOn}");
    }

    // Persist the record.json marker atomically (temp + move), leaving any staged chunks untouched.
    private static void WriteRecordMarker(string dir, DictationDeliveryRecord record)
    {
        var path = RecordPath(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, RecordJson));
        File.Move(tmp, path, overwrite: true);
    }

    // ====== internals ===============================================================

    private string DirFor(string uid) => Path.Combine(_root, uid);
    private static string ChunkPath(string dir, int index) => Path.Combine(dir, $"{index:D5}.part");
    private static string RecordPath(string dir) => Path.Combine(dir, "record.json");

    private static readonly JsonSerializerOptions RecordJson = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Accept only GUID-shaped ids so the id can never escape the staging root.</summary>
    private static string? NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Guid.TryParse(id, out var g) ? g.ToString("N") : null;
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>Outcome of <see cref="VoiceUploadStore.AssembleAsync"/>.</summary>
public readonly record struct AssembleResult(string Status, byte[]? Audio, IReadOnlyList<int> Missing)
{
    public static AssembleResult Ok(byte[] audio) => new("ok", audio, Array.Empty<int>());
    public static AssembleResult Incomplete(IReadOnlyList<int> missing) => new("incomplete", null, missing);
    public static AssembleResult Unknown() => new("unknown_upload", null, Array.Empty<int>());
}

/// <summary>
/// The lifecycle of one durable dictation upload id (issue #1183, extended by #1185). PENDING is
/// undelivered and not abandoned - its chunks are retained in full for resume, and while it is PENDING the
/// session is locked. DELIVERED (the turn was injected) and ABANDONED (the dictation was given up) are both
/// terminal tombstones - not-undelivered, so the session lock is off - kept until the client acknowledges
/// them. FAILED (a permanent transcription failure) is a PARKED, USER-RETRYABLE pause - NOT a terminal
/// tombstone and NOT a de-dupe short-circuit: it keeps its chunk bytes, leaves the session unlocked (so the
/// client auto-loop stops), and an explicit retry clears it back to PENDING to re-drive.
/// </summary>
public enum DictationDeliveryState { Pending, Delivered, Abandoned, Failed }

/// <summary>
/// A dictation delivery record (issue #1183, extended by #1185 and #1188): the durable marker for an upload
/// id. PENDING is an explicit marker carrying the owning <see cref="SessionId"/> (the enforced session lock
/// is a projection of it). For DELIVERED it holds the submitted outcome so a re-complete returns the
/// identical result without injecting a second turn; for ABANDONED it holds the drop reason; for FAILED it
/// holds the permanent-failure reason code (in <see cref="Reason"/>) and its chunks are kept for an explicit
/// retry. Every state carries the <see cref="SessionId"/> so a transition (e.g. FAILED back to PENDING)
/// preserves the owning session.
/// </summary>
public sealed record DictationDeliveryRecord(
    DictationDeliveryState State,
    bool Submitted,
    bool MovedOn,
    string Transcript,
    string? Reason,
    string SessionId = "");
