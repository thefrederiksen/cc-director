using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
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
///
/// PARTITIONED BY TENANT (issue #1884). Every directory, chunk and record used to be keyed SOLELY by the
/// caller-supplied upload id, on one global root. A GUID is not a tenant boundary: secrecy of an identifier
/// is not authorization, and upload ids travel in client logs, retries and store-and-forward queues - so on
/// the hosted Gateway one account holding another's upload id could read its transcript, overwrite its
/// chunks, or resolve its record. An upload id is now only meaningful INSIDE its own tenant: the partition is
/// the DIRECTORY (<see cref="ForTenant"/>), so a read physically cannot open another tenant's staging, and
/// the record itself carries its tenant as a second, independent check. <see cref="TenantId.Local"/> keeps
/// today's path exactly - self-host is unchanged and nothing migrates - and a hosted account tenant lands
/// under tenants/&lt;id&gt;/. The tenant is always supplied by the caller, which resolved it from the
/// authenticated device key at the boundary; this type never guesses one.
/// </summary>
public sealed class VoiceUploadStore
{
    /// <summary>The container directory hosting the non-local partitions, directly under the base root.</summary>
    public const string TenantPartitionDirectoryName = "tenants";

    // This partition's staging root: the base root for the local tenant, base/tenants/<id> otherwise.
    private readonly string _root;
    // The base root every partition is computed from, so ForTenant on an already-partitioned store still
    // resolves against the base rather than nesting a partition inside a partition.
    private readonly string _partitionBase;
    // The tenant this instance is bound to. Stamped onto every record written and required to match on
    // every record read.
    private readonly TenantId _tenant;

    public VoiceUploadStore() : this(CcStorage.VoiceTurnUploads()) { }

    /// <summary>Test seam: stage under an explicit root instead of the shared storage dir.</summary>
    public VoiceUploadStore(string root) : this(root, TenantId.Local, root) { }

    private VoiceUploadStore(string root, TenantId tenant, string partitionBase)
    {
        _root = root;
        _tenant = tenant;
        _partitionBase = partitionBase;
        Directory.CreateDirectory(_root);
    }

    /// <summary>The tenant this store instance is bound to.</summary>
    public TenantId Tenant => _tenant;

    /// <summary>This partition's staging root on disk.</summary>
    public string Root => _root;

    /// <summary>
    /// A view of this staging bound to ONE tenant. Every path, gate, chunk and record of the returned store
    /// lives inside that tenant's partition, so an upload id from another tenant simply does not exist here -
    /// the isolation is the directory, not a predicate a later edit could forget to apply.
    /// </summary>
    public VoiceUploadStore ForTenant(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException(
                "An upload partition needs a valid tenant; an unresolved tenant is denied, never defaulted.",
                nameof(tenant));
        return tenant.IsLocal && string.Equals(_root, _partitionBase, StringComparison.Ordinal) && _tenant.IsLocal
            ? this
            : new VoiceUploadStore(PartitionRootFor(tenant), tenant, _partitionBase);
    }

    /// <summary>
    /// True only for the EXACT form <see cref="Tenancy.TenantRegistry"/> mints: a canonical lowercase GUID.
    ///
    /// A tenant id becomes a DIRECTORY NAME here, so it must be a shape this system actually produces - not
    /// merely "characters that look harmless". Both structural aliases already found on the prompt-log
    /// partition (<c>GatewayPromptLog</c>) apply verbatim to this one:
    ///
    ///  - A character allow-list such as <c>^[A-Za-z0-9._-]{1,64}$</c> accepts <c>".."</c>, and combining the
    ///    base root with <c>tenants</c> and <c>".."</c> canonicalizes to exactly the base root - the LOCAL
    ///    partition.
    ///  - An allow-list accepting <c>A-F</c> as well as <c>a-f</c> lets two ids that differ only in case name
    ///    the SAME directory on Windows and Azure Files while being DIFFERENT identities to the
    ///    case-sensitive tenants table. That is one tenant reading another's audio through a casing alias.
    ///
    /// So this accepts ONE spelling: parse strictly, then require the value to equal its own canonical
    /// round-trip. Anything else is REFUSED rather than normalised - normalising is how two identities
    /// quietly share a folder, and this folder holds recorded AUDIO and its TRANSCRIPT.
    /// </summary>
    private static bool IsMintedAccountTenant(string value)
        => Guid.TryParseExact(value, "D", out var parsed)
           && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

    /// <summary>
    /// The staging root one tenant's uploads live in. The local tenant keeps the root it has always used -
    /// self-host unchanged, nothing migrates - and every other tenant gets its own folder beneath it.
    /// </summary>
    private string PartitionRootFor(TenantId tenant)
    {
        if (tenant.IsLocal) return _partitionBase;

        // Every other partition must be a minted account tenant - including the reserved SYSTEM tenant, which
        // is deliberately REFUSED rather than given a folder: no recorded audio belongs to it, so the safe
        // answer is that it has no partition at all.
        if (!IsMintedAccountTenant(tenant.Value))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' is not a minted account tenant and cannot name an upload partition.",
                nameof(tenant));

        var combined = Path.Combine(_partitionBase, TenantPartitionDirectoryName, tenant.Value);

        // Belt and braces, because the cost of being wrong here is one account reading another's dictation:
        // the result must actually LIE INSIDE the partition container. The rule above already excludes
        // traversal, so this can only fire if that rule is ever loosened - which is exactly when it is wanted.
        var expectedRoot =
            Path.GetFullPath(Path.Combine(_partitionBase, TenantPartitionDirectoryName)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(combined).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' resolves outside the upload partition root.", nameof(tenant));

        return combined;
    }

    /// <summary>
    /// The SECOND, independent check on a record: does the record on disk claim this partition's tenant?
    /// The directory already makes a cross-tenant read impossible, so this can only fire if a partition root
    /// is ever mis-computed - which is precisely the failure that must not silently hand over a transcript.
    ///
    /// An ABSENT tenant on the record is accepted ONLY for the local partition. That is not a fallback: it is
    /// the exact reading of records written before this field existed, all of which are self-host records in
    /// the local partition. In an account partition an absent tenant is refused, so a missing value can never
    /// become a way in.
    /// </summary>
    private bool BelongsHere(DictationDeliveryRecord record)
        => string.IsNullOrEmpty(record.Tenant)
            ? _tenant.IsLocal
            : string.Equals(record.Tenant, _tenant.Value, StringComparison.Ordinal);

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
                // The partition container is not an upload; sweeping it by age would delete every other
                // tenant's staging in one go (issue #1884). An upload directory is a 32-hex id, so this
                // name can only ever be the container.
                if (IsPartitionContainer(dir)) continue;
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
        if (uid is null) return null;
        var record = ReadRecordFile(RecordPath(DirFor(uid)));
        if (record is null) return null;
        // Tenant-checked as well as partitioned (issue #1884). See BelongsHere: the directory is the boundary,
        // this is the independent second opinion, and a record that fails it is treated as absent rather than
        // handed to a caller it does not belong to.
        if (!BelongsHere(record))
        {
            FileLog.Write($"[VoiceUploadStore] ReadRecord: uploadId={uid} record belongs to another tenant " +
                $"(partition={_tenant.ToLogString()}); refused");
            return null;
        }
        return record;
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
        WithRecordLock(uid, () =>
        {
            var dir = DirFor(uid);
            Directory.CreateDirectory(dir);
            // Carry the #1593 re-baseline forward: a phone that never saw our 502 simply RE-REGISTERS its
            // upload id and retries. That path lands here, so dropping the value would hand the retry back the
            // very baseline our own failed attempt invalidated - the exact drop this field exists to stop.
            // Read inside the gate, so it cannot be a value from before another writer moved it.
            WriteRecordMarker(dir, new DictationDeliveryRecord(
                DictationDeliveryState.Pending, false, false, "", null, sessionId ?? "", ExistingRebaseline(uid)));
            FileLog.Write($"[VoiceUploadStore] MarkPending: uploadId={uid} sessionId={sessionId}");
        });
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
        {
            if (IsPartitionContainer(dir)) continue;
            if (ReadRecordFile(RecordPath(dir)) is { State: DictationDeliveryState.Pending } rec && BelongsHere(rec))
                yield return rec;
        }
    }

    /// <summary>
    /// Transition this upload id to the durable DELIVERED tombstone: persist the submitted outcome, then
    /// discard the heavy chunk bytes (the turn is resolved and resume is no longer needed) while keeping the
    /// small marker. Idempotent: re-marking an already-delivered id rewrites the same tombstone. The
    /// tombstone is retired only by <see cref="Acknowledge"/>.
    /// </summary>
    public void MarkDelivered(string uploadId, bool submitted, bool movedOn, string transcript)
        => WriteTombstone(uploadId, uid => new DictationDeliveryRecord(
            DictationDeliveryState.Delivered, submitted, movedOn, transcript ?? "", null, ExistingSessionId(uid)));

    /// <summary>
    /// Transition this upload id to the durable ABANDONED tombstone: persist the reason and discard the
    /// chunk bytes. Terminal and not-undelivered, so the session lock is off. The abandon WRITE triggers
    /// from the surfaces are a later task; this provides the state and its read side.
    /// </summary>
    public void MarkAbandoned(string uploadId, string reason)
        => WriteTombstone(uploadId, uid => new DictationDeliveryRecord(
            DictationDeliveryState.Abandoned, false, false, "", reason ?? "", ExistingSessionId(uid)));

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
        WithRecordLock(uid, () =>
        {
            var dir = DirFor(uid);
            Directory.CreateDirectory(dir);
            // Write ONLY the marker - keep the chunk bytes (the retry re-drives them), the opposite of a
            // tombstone. Preserve the owning session id so a later ClearFailed can restore a PENDING marker
            // that re-locks it, and the #1593 re-baseline so a transcription failure on a retry cannot erase
            // what an earlier failed DELIVERY attempt already learned about the buffer. Both read inside the
            // gate, so neither can be a value from before another writer moved it.
            WriteRecordMarker(dir, new DictationDeliveryRecord(
                DictationDeliveryState.Failed, false, false, "", reasonCode ?? "", ExistingSessionId(uid),
                ExistingRebaseline(uid)));
            FileLog.Write($"[VoiceUploadStore] MarkFailed: uploadId={uid} reason={reasonCode} (chunks retained)");
        });
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
        // Read-modify-write under the gate, read inside: without it, a ClearFailed that read FAILED could
        // write PENDING back over a tombstone that landed in between, resurrecting a resolved upload id.
        return WithRecordLock(uid, () =>
        {
            if (ReadRecord(uid) is not { State: DictationDeliveryState.Failed } failed) return false;
            try
            {
                WriteRecordMarker(DirFor(uid), new DictationDeliveryRecord(
                    DictationDeliveryState.Pending, false, false, "", null, failed.SessionId,
                    failed.RebaselineBufferBytes));
                FileLog.Write($"[VoiceUploadStore] ClearFailed: uploadId={uid} back to PENDING (chunks retained)");
                return true;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[VoiceUploadStore] ClearFailed uploadId={uid} failed: {ex.Message}");
                return false;
            }
        });
    }

    /// <summary>
    /// Record the honest moved-on baseline for this upload id after one of OUR OWN delivery attempts failed
    /// (Lost Dictations mission, issue #1593). The record stays exactly where it was - this is NOT a state
    /// transition and NOT a tombstone: a PENDING record stays PENDING with its chunks intact, so the client's
    /// retry re-drives normally. Only <see cref="DictationDeliveryRecord.RebaselineBufferBytes"/> moves.
    ///
    /// MONOTONIC: the stored value only ever grows (each failed attempt adds more of its own noise, and a
    /// later fresh read is at least as honest as an earlier one), so repeated failures can never lower the
    /// baseline back into a range where our own noise reads as a real turn.
    ///
    /// A no-op returning false when there is no record yet, or when the record is already terminal
    /// (DELIVERED / ABANDONED): those are resolved and their guard has already run, so re-baselining them
    /// would move a number nothing will read again.
    /// </summary>
    public bool RecordFailedDeliveryBaseline(string uploadId, long bufferBytes)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return false;

        // Read-modify-write under the per-upload gate, with the read INSIDE the lock. Two things depend on
        // that, and the second is the serious one:
        //   1. The max-and-write is atomic, so two re-baseline writers cannot both read the old value and let
        //      the one that computed the SMALLER max land last - handing a retry a baseline we already knew
        //      was too low, which is the drop this field exists to stop. Monotonicity has to hold at the
        //      WRITE, not just in the arithmetic.
        //   2. We write only the re-baseline field onto the record AS IT IS RIGHT NOW, never a record read
        //      before the lock. Otherwise a stalled re-baseline could write a remembered PENDING record back
        //      over a tombstone that landed in the meantime and resurrect a delivered upload id (issue #1183).
        return WithRecordLock(uid, () =>
        {
            if (ReadRecord(uid) is not { } record) return false;
            // Terminal is final: its guard has already run for the last time, and rewriting the tombstone here
            // is precisely how a resolved upload id would come back to life.
            if (record.State is DictationDeliveryState.Delivered or DictationDeliveryState.Abandoned) return false;
            BetweenRecordReadAndWriteForTests?.Invoke(uid);

            var merged = Math.Max(record.RebaselineBufferBytes ?? 0, bufferBytes);
            if (merged <= (record.RebaselineBufferBytes ?? -1)) return false; // already at least this honest
            try
            {
                // Marker only - the chunk bytes stay put, because this upload is still going to be delivered.
                WriteRecordMarker(DirFor(uid), record with { RebaselineBufferBytes = merged });
                FileLog.Write($"[VoiceUploadStore] RecordFailedDeliveryBaseline: uploadId={uid} rebaseline={merged} " +
                    $"(state={record.State}, chunks retained)");
                return true;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[VoiceUploadStore] RecordFailedDeliveryBaseline uploadId={uid} failed: {ex.Message}");
                return false;
            }
        });
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
        // Retiring the record is a record mutation, so it takes the same gate. Without it, a retirement landing
        // in the middle of another writer's read-modify-write would let that writer re-create the staging
        // directory and a marker for an upload id that had just been acknowledged away.
        return WithRecordLock(uid, () =>
        {
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
        });
    }

    // The owning session id already recorded for this upload id (empty when there is no record yet), so a
    // state transition preserves the session id first written by MarkPending at register (issue #1188).
    private string ExistingSessionId(string uploadId) => ReadRecord(uploadId)?.SessionId ?? "";

    // The re-baseline already recorded for this upload id (null when there is no record, or no attempt has
    // failed), so a NON-TERMINAL state transition preserves what a failed delivery attempt learned (#1593).
    // The terminal tombstones deliberately do NOT carry it: their guard has already run for the last time.
    private long? ExistingRebaseline(string uploadId) => ReadRecord(uploadId)?.RebaselineBufferBytes;

    // Write the small durable marker first (atomic temp+move), THEN discard the heavy chunk bytes, so a
    // crash between the two leaves a valid tombstone rather than orphaned chunks with no marker. Used by the
    // TERMINAL transitions (delivered/abandoned) that no longer need the audio; FAILED keeps its bytes and
    // so writes the marker alone (see MarkFailed).
    // The record is composed by a FACTORY run inside the gate, not passed in ready-made: a tombstone carries
    // the session id already on the record, and reading that outside the lock would be one more
    // read-modify-write straddling the gate - the very shape this is here to eliminate.
    private void WriteTombstone(string uploadId, Func<string, DictationDeliveryRecord> compose)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        // Under the per-upload gate like every other record write: a tombstone that lands while another writer
        // is mid read-modify-write is exactly the interleaving that would let a resolved upload id be written
        // back to PENDING and re-injected (issue #1183).
        WithRecordLock(uid, () =>
        {
            var record = compose(uid);
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
        });
    }

    // Persist the record.json marker atomically (temp + move), leaving any staged chunks untouched.
    //
    // THE ONE PLACE a record is written, so it is the one place the owning tenant is stamped (issue #1884).
    // Stamped here rather than by each composer on purpose: a composer that forgot would write an
    // unattributed record, and the whole point of the field is that it cannot be forgotten.
    private void WriteRecordMarker(string dir, DictationDeliveryRecord record)
    {
        var path = RecordPath(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record with { Tenant = _tenant.Value }, RecordJson));
        File.Move(tmp, path, overwrite: true);
    }

    // ====== internals ===============================================================

    // ====== the per-upload record gate ==============================================
    //
    // EVERY read-modify-write of an upload's record.json runs under this gate. Not just the re-baseline:
    // serializing the re-baseline writers against each other only is worse than useless, because the
    // dangerous interleaving is between DIFFERENT writers. A re-baseline that reads a PENDING record, then
    // stalls while MarkDelivered lands a terminal tombstone, would write its remembered PENDING record back
    // over that tombstone and RESURRECT the upload id as pending - reopening the exact re-injection door the
    // durable record exists to close (issue #1183). Hence: one gate per upload id, every writer inside it,
    // and every writer RE-READS the record inside the lock rather than trusting anything read outside it.
    //
    // Keyed by the upload's staging DIRECTORY - the resource itself, not the store object - because callers
    // construct their own VoiceUploadStore instances over the same root (the endpoint, the aggregator, and
    // the tests all do), so a gate belonging to an instance would protect nothing. Canonicalized through
    // Path.GetFullPath so two spellings of one directory cannot resolve to two different gates.
    //
    // In-process only, and deliberately so: every writer of a given staging root lives in this Gateway. It is
    // NOT a cross-process file lock and does not pretend to be; the individual marker write is already atomic
    // (temp + move), so the worst a second process could do is land a stale value - which no deployment we run
    // can produce, because one Gateway owns a staging root.
    //
    // REFCOUNTED, so the map is bounded by CONCURRENT users rather than by lifetime upload ids - it holds
    // nothing at rest. This is why there is no "prune on terminal transition": removing a gate that another
    // thread is still holding would let the next caller lock a DIFFERENT object and walk straight into the
    // section, which is the race this gate exists to prevent. An entry that nobody holds cannot be in anyone's
    // way, and an entry that somebody holds must not be removed - refcounting is just those two rules.
    private sealed class RecordGate
    {
        public int Users;
    }

    /// <summary>
    /// Test seam: invoked INSIDE the per-upload gate, between the record read and the record write of
    /// <see cref="RecordFailedDeliveryBaseline"/>. Null in production and never assigned outside tests.
    ///
    /// It exists because the tombstone-resurrection hazard is an INTERLEAVING, and an interleaving cannot be
    /// proven by hammering threads and hoping: a test that only sometimes reproduces the race is a test that
    /// will pass on a broken build. This lets one test stall the read-modify-write at the exact instant a
    /// competing tombstone writer tries to land, deterministically, in both directions - the gate holding, and
    /// the gate removed.
    /// </summary>
    internal static Action<string>? BetweenRecordReadAndWriteForTests;

    private static readonly Dictionary<string, RecordGate> _recordGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static RecordGate AcquireGate(string key)
    {
        lock (_recordGates)
        {
            if (!_recordGates.TryGetValue(key, out var gate))
            {
                gate = new RecordGate();
                _recordGates[key] = gate;
            }
            gate.Users++;
            return gate;
        }
    }

    private static void ReleaseGate(string key, RecordGate gate)
    {
        lock (_recordGates)
        {
            if (--gate.Users == 0) _recordGates.Remove(key);
        }
    }

    /// <summary>Run a record read-modify-write for this upload id under its per-upload gate.</summary>
    private T WithRecordLock<T>(string uid, Func<T> body)
    {
        var key = GateKey(uid);
        var gate = AcquireGate(key);
        try
        {
            lock (gate) return body();
        }
        finally
        {
            ReleaseGate(key, gate);
        }
    }

    private void WithRecordLock(string uid, Action body) => WithRecordLock(uid, () => { body(); return true; });

    private string GateKey(string uid) => Path.GetFullPath(DirFor(uid));

    private string DirFor(string uid) => Path.Combine(_root, uid);

    // True for the directory that HOLDS the other tenants' partitions, which is never itself an upload.
    private static bool IsPartitionContainer(string dir)
        => string.Equals(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            TenantPartitionDirectoryName, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// The canonical staging form of a caller-supplied upload id, or null when it is not a GUID. Exposed so
    /// the endpoint's in-memory caches key on the SAME single spelling this store keys its directories by:
    /// two spellings of one GUID must not become two cache entries for one staging directory.
    /// </summary>
    internal static string? NormalizeUploadId(string? id) => NormalizeId(id);

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
/// <param name="RebaselineBufferBytes">
/// The honest moved-on baseline for this upload id after one of OUR OWN delivery attempts failed (Lost
/// Dictations mission, issue #1593), or null when no attempt has failed. A failed submit types the text into
/// the terminal and clears it again, growing the session buffer by thousands of bytes that the moved-on guard
/// cannot tell apart from real turns - so a retry judged against the phone's record-time baseline gets dropped
/// as stale by noise the failure itself made. The guard therefore judges a retry against the LARGER of the
/// request's baseline and this value. It lives on the SERVER record, not in the phone's request, so a phone
/// that never saw the 502 still retries against the honest baseline. Null (absent) in every record written
/// before this field existed, which reads back as "no attempt has failed" - the correct meaning.
/// </param>
/// <param name="Tenant">
/// The tenant that owns this upload (issue #1884). Stamped by <see cref="VoiceUploadStore"/> on every write
/// from the partition the record is written into - callers never supply it - and required to match on every
/// read. The directory partition is the boundary; this field is the independent second check, so a
/// mis-computed partition root cannot silently hand one account another's audio or transcript. Empty in
/// records written before the field existed, which are self-host records and are accepted only in the local
/// partition.
/// </param>
public sealed record DictationDeliveryRecord(
    DictationDeliveryState State,
    bool Submitted,
    bool MovedOn,
    string Transcript,
    string? Reason,
    string SessionId = "",
    long? RebaselineBufferBytes = null,
    string Tenant = "");
