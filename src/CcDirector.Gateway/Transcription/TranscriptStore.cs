using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The Gateway-owned, restart-surviving store of dictation transcripts (issue #509): one
/// <c>dictation_transcripts</c> row per utterance, per tenant, holding the raw transcript and the cleaned
/// transcript so a customer's mistranscriptions can be mined later to build their dictionary. WRITE-ONLY for
/// now - there is no read surface and no display; the reading, mining, and dictionary-page experience are
/// tracked separately (devthrottle issue #2075). This is the storage foundation only.
///
/// ONE PATH, self-host is just one tenant: the write is identical whether we host the Gateway or the customer
/// self-hosts - there is NO <c>IsHosted</c> branch here. On the single-tenant local install every row is the
/// <see cref="TenantId.Local"/> ("local") tenant, so behavior is identical to a store with no tenant column;
/// on the hosted Gateway each account's rows are isolated by the same global query filter every other
/// tenant-scoped table uses, so one tenant can never read or trim another's rows. This retires the old
/// host-global flat file (<c>dictation/sessions/*.jsonl</c>) that had to be switched off on hosted precisely
/// because it was not tenant-aware.
///
/// EXPLICIT TENANT ON EVERY CALL (mirroring <see cref="Settings.TenantSettingsStore"/>): the tenant is the one
/// the transcription call already resolved and passed - this layer performs NO ambient or static tenant
/// inference. It goes through <see cref="GatewayDatabase.CreateContext(TenantId)"/>, never the ambient
/// <see cref="GatewayDatabase.CreateContext()"/>. A <see cref="TenantId"/> cannot be blank by construction, so
/// a row can never be attributed to the wrong tenant.
///
/// RETENTION, per tenant: a transcript is kept until it is either older than <see cref="RetentionWindow"/> (30
/// days) or pushed past the newest <see cref="MaxTranscriptsPerTenant"/> (10,000) for that tenant, whichever
/// bites first - about 10 MB of text per tenant maximum. Retention is THROTTLED so it does not run on every
/// write (it runs on the first append per tenant per process and then every <see cref="RetentionCheckEvery"/>
/// appends), which bounds the overshoot to the cap plus at most one check interval.
///
/// Threading: the Gateway is a single writer; every operation runs under this store's write lock over a fresh
/// pooled context. A write failure is SWALLOWED-AND-LOGGED by the caller (the transcription-service hook), so
/// storing a transcript can never fail a live dictation turn - this store is a write-only diagnostic aid, not
/// an operationally-critical store.
/// </summary>
public sealed class TranscriptStore
{
    /// <summary>How long a transcript is kept before age-trim removes it. Tunable constant (issue #509).</summary>
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    /// <summary>The maximum transcripts kept per tenant; older rows beyond this are count-trimmed. About 1 KB
    /// of text per utterance, so the cap is roughly 10 MB per tenant. Tunable constant (issue #509).</summary>
    public const int MaxTranscriptsPerTenant = 10_000;

    /// <summary>Retention runs every this-many appends per tenant (plus the first append per tenant per
    /// process), so it does not run on every write. Bounds the overshoot to the cap plus one interval.</summary>
    public const int RetentionCheckEvery = 64;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    // The effective retention limits. The public constructor uses the production constants; an internal
    // constructor lets tests drive count-trim with a small cap instead of inserting 10,000 rows.
    private readonly TimeSpan _retentionWindow;
    private readonly int _maxPerTenant;

    // Per-tenant append counter, used only to throttle retention. Guarded by _gate like everything else.
    private readonly Dictionary<string, long> _appendsSinceCheck = new(StringComparer.Ordinal);

    /// <param name="db">The Gateway EF database this store reads and writes through. Required.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    public TranscriptStore(GatewayDatabase db)
        : this(db, RetentionWindow, MaxTranscriptsPerTenant)
    {
    }

    /// <summary>Test-only constructor: overrides the retention window and per-tenant cap so count-trim and
    /// age-trim can be exercised deterministically without inserting the full production volume.</summary>
    internal TranscriptStore(GatewayDatabase db, TimeSpan retentionWindow, int maxPerTenant)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _retentionWindow = retentionWindow;
        _maxPerTenant = maxPerTenant;
    }

    /// <summary>
    /// Append one transcript for a tenant and persist it, then run retention on the throttle. The row is
    /// stamped with the EXPLICIT <paramref name="tenant"/>, never the ambient one, and a code-minted GUID id.
    /// <paramref name="nowUtc"/> is injected so tests are deterministic.
    /// </summary>
    /// <param name="tenant">The tenant the transcription call resolved. Required and valid.</param>
    /// <param name="source">The surface that produced the utterance (e.g. "dictation", "voice", "batch").</param>
    /// <param name="rawText">The raw transcript as the provider returned it. Required.</param>
    /// <param name="cleanedText">The cleaned transcript; when null it defaults to <paramref name="rawText"/>.</param>
    /// <param name="cleanupApplied">True when the dictionary correction changed the raw transcript.</param>
    /// <param name="turnId">The turn id shared with the transcription-health record, for correlation. May be null.</param>
    /// <param name="nowUtc">The current time (UTC) to stamp on the row.</param>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    /// <exception cref="ArgumentNullException">The raw text is null.</exception>
    public void Append(
        TenantId tenant, string source, string rawText, string? cleanedText, bool cleanupApplied,
        string? turnId, DateTime nowUtc)
    {
        if (rawText is null) throw new ArgumentNullException(nameof(rawText));

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            ctx.DictationTranscripts.Add(new DictationTranscriptEntity
            {
                TenantId = tenant.Value,
                Id = Guid.NewGuid().ToString("N"),
                TimestampUtc = nowUtc.ToUniversalTime(),
                TurnId = turnId,
                Source = source ?? "",
                RawText = rawText,
                CleanedText = cleanedText ?? rawText,
                CleanupApplied = cleanupApplied,
            });
            ctx.SaveChanges();

            FileLog.Write($"[TranscriptStore] Append: tenant={tenant.ToLogString()} source={source} " +
                          $"rawChars={rawText.Length} cleanedChars={(cleanedText ?? rawText).Length} cleanupApplied={cleanupApplied}");

            if (DueForRetention(tenant))
                RetainCore(ctx, nowUtc);
        }
    }

    /// <summary>
    /// Trim one tenant's transcripts to the retention policy: delete rows older than
    /// <see cref="RetentionWindow"/> and any beyond the newest <see cref="MaxTranscriptsPerTenant"/>. Scoped to
    /// <paramref name="tenant"/> by the global query filter - a tenant can never trim another tenant's rows.
    /// Public so it can be exercised deterministically and so a future startup sweep can call it directly.
    /// </summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public void Retain(TenantId tenant, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            RetainCore(ctx, nowUtc);
        }
    }

    /// <summary>The count of this tenant's transcripts. Test/diagnostic helper - scoped by the query filter.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public int Count(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.DictationTranscripts.AsNoTracking().Count();
        }
    }

    /// <summary>This tenant's transcripts, newest first, as detached copies. Test/diagnostic helper - the read
    /// surface proper is devthrottle issue #2075. Scoped to <paramref name="tenant"/> by the query filter.</summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public IReadOnlyList<DictationTranscriptEntity> List(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.DictationTranscripts.AsNoTracking()
                .OrderByDescending(e => e.TimestampUtc)
                .ThenByDescending(e => e.Id)
                .ToList();
        }
    }

    /// <summary>
    /// The retention core, run inside the write lock against a tenant-scoped context: age-trim then count-trim,
    /// both committed in ONE SaveChanges. The context's global query filter already scopes every query to the
    /// active tenant, so this only ever touches that tenant's rows.
    /// </summary>
    private void RetainCore(GatewayDbContext ctx, DateTime nowUtc)
    {
        var cutoff = nowUtc.ToUniversalTime() - _retentionWindow;

        // Age-trim: rows older than the window. Uses the (tenant_id, TimestampUtc) index.
        var expired = ctx.DictationTranscripts.Where(e => e.TimestampUtc < cutoff).ToList();
        if (expired.Count > 0)
            ctx.DictationTranscripts.RemoveRange(expired);

        // Count-trim: anything beyond the newest cap. Count the survivors (after age-trim is staged, but the
        // query still sees the un-deleted rows, so subtract the staged deletes), then delete the oldest
        // overflow. Order oldest-first (TimestampUtc asc, Id asc as a stable tie-break) and take the overflow.
        var remaining = ctx.DictationTranscripts.Count() - expired.Count;
        var overflow = remaining - _maxPerTenant;
        if (overflow > 0)
        {
            var expiredIds = expired.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var oldest = ctx.DictationTranscripts
                .OrderBy(e => e.TimestampUtc)
                .ThenBy(e => e.Id)
                // Do not re-count a row already staged for age-trim deletion.
                .AsEnumerable()
                .Where(e => !expiredIds.Contains(e.Id))
                .Take(overflow)
                .ToList();
            if (oldest.Count > 0)
                ctx.DictationTranscripts.RemoveRange(oldest);
        }

        if (expired.Count > 0 || overflow > 0)
        {
            ctx.SaveChanges();
            FileLog.Write($"[TranscriptStore] Retain: tenant={ctx.ActiveTenant} ageTrimmed={expired.Count} " +
                          $"countTrimmed={Math.Max(overflow, 0)}");
        }
    }

    /// <summary>
    /// Whether retention is due for this tenant on this append: true on the FIRST append per tenant per process
    /// (so a restart trims stale rows even for a low-traffic tenant) and then every
    /// <see cref="RetentionCheckEvery"/> appends. Called under <see cref="_gate"/>.
    /// </summary>
    private bool DueForRetention(TenantId tenant)
    {
        var n = _appendsSinceCheck.TryGetValue(tenant.Value, out var prev) ? prev + 1 : 1;
        if (n == 1 || n >= RetentionCheckEvery)
        {
            _appendsSinceCheck[tenant.Value] = 0;
            return true;
        }
        _appendsSinceCheck[tenant.Value] = n;
        return false;
    }
}
