using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Prompts;

/// <summary>
/// THE prompt log (issue #1551). Every prompt the operator sends and every reply the agent sends back,
/// for every agent, on every machine in the fleet - held here, on the Gateway.
///
/// The Gateway is the right home and the Director is not, for two reasons:
/// - The Gateway is the one place that sees the WHOLE fleet. Anyone asking for history asks here and
///   it is already present; nothing has to go hunting across machines for it.
/// - The Gateway is what moves to the server. The log moves with it, because it was never somewhere
///   else to begin with.
///
/// The Director captures and pushes (it is the only thing that sees a prompt at all, and the only thing
/// that knows whether it was typed or spoken and from which surface). This is the SERVICE-SIDE copy and
/// the only prompt log DevThrottle holds. Same shape as the existing stats spine, where the Director
/// observes and GatewayInputStatsAggregator holds.
///
/// It is NOT the only copy in existence, and this comment claimed otherwise until an inspection went
/// looking: a Director also keeps prompt-derived text in local files on the member's own machine - a
/// first-prompt snippet and per-turn summaries in its own session history, and an expected-first-prompt
/// in sessions.json and its backup. Issue #2380 tracks bringing those within the member's delete.
///
/// One JSON line per message in a daily file: base/prompt-log/conversation-yyyyMMdd.jsonl.
///
/// PARTITIONED BY TENANT (issue #1848). Prompt text is customer content, so on the hosted Gateway one
/// account's messages must never be readable by another. The partition is the DIRECTORY, not a predicate on
/// the read: a tenant's history lives in its own folder, so a read physically cannot open another tenant's
/// file. <see cref="TenantId.Local"/> keeps today's path exactly (self-host is unchanged and nothing
/// migrates); a hosted account tenant lands under tenants/&lt;id&gt;/. The tenant is always supplied by the
/// caller, which resolved it from the authenticated device key at the boundary - this type never guesses one.
///
/// Retention is BOUNDED (CR-3b, devthrottle_internal issue #1180). Prompt text is customer content -
/// proprietary code, file paths, error output, pasted credentials - so it lives for the retention window
/// and no longer. <see cref="PromptLogRetentionSweep"/> owns the window and the timer;
/// <see cref="PurgeOlderThan"/> is the enforcement. A member can also export their whole history and
/// delete it outright (<see cref="ReadAll"/> / <see cref="DeleteAll"/>, served by PromptEndpoints).
///
/// Fail-safe: every write is wrapped, so a logging error can never fail a Director's push.
/// </summary>
public sealed class GatewayPromptLog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// True only for the EXACT form <see cref="Tenancy.TenantRegistry"/> mints: a canonical lowercase GUID.
    ///
    /// A tenant id becomes a DIRECTORY NAME, so it must be a shape this system actually produces - not merely
    /// "characters that look harmless". Two structural aliases have already been found here, and both were
    /// the same class of defect:
    ///
    ///  - The first rule was <c>^[A-Za-z0-9._-]{1,64}$</c>, which accepts <c>".."</c> - and combining the root
    ///    with <c>tenants</c> and <c>".."</c> canonicalizes to exactly the root, the LOCAL partition.
    ///  - The second accepted <c>A-F</c> as well as <c>a-f</c>. The registry mints canonical LOWERCASE guids
    ///    and the tenants table uses a CASE-SENSITIVE collation, so two ids differing only in case are
    ///    DIFFERENT IDENTITIES to the database - while Windows and Azure Files name the SAME directory for
    ///    both. That is one tenant reading another's prompt text through a casing alias.
    ///
    /// The lesson both times: at a path boundary the dangerous values are built from harmless characters, and
    /// a collision does not need a special character - it needs two accepted spellings of one path. So this
    /// accepts ONE spelling: parse strictly, then require the value to equal its own canonical round-trip.
    /// Anything else is refused rather than normalised, because normalising is how two identities quietly
    /// share a folder.
    /// </summary>
    private static bool IsMintedAccountTenant(string value)
        => Guid.TryParseExact(value, "D", out var parsed)
           && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

    /// <summary>
    /// One file-IO lock PER TENANT, not one shared across the whole fleet (audit gap audit-a). The partition
    /// is already the directory, so two tenants never touch the same daily FILE; a single process-global lock
    /// would nonetheless serialize them, letting one tenant's append/read of an unbounded (retention is
    /// forever) file stall every other tenant's unrelated prompt IO. Each tenant gets its OWN gate, keyed by
    /// <see cref="TenantId"/>, so a caller only ever takes the lock of its own partition and one account's
    /// large or slow file cannot block another's read. The gate still serializes a single tenant's own
    /// concurrent appends/reads to its files, which is the only serialization actually required.
    /// </summary>
    private readonly ConcurrentDictionary<TenantId, object> _gates = new();
    private readonly string _directory;

    /// <summary>This tenant's file-IO lock, created on first use. Two distinct tenants get two distinct gates.</summary>
    private object GateFor(TenantId tenant) => _gates.GetOrAdd(tenant, static _ => new object());

    /// <param name="directory">Override the log directory (tests). Defaults to the per-user location.</param>
    public GatewayPromptLog(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory;
    }

    /// <summary>The Gateway's prompt-log directory.</summary>
    public static string DefaultDirectory() => CcStorage.PromptLog();

    /// <summary>
    /// Replace a tenant's raw account id with its hashed log form anywhere it appears in text bound for the
    /// log - in practice a file path inside an exception message, since the partition directory IS the tenant
    /// id. This is a single exact substitution of a value we hold, not a general-purpose scrub: it is
    /// complete for the one way the id can get in here, and it keeps the failure LOUD rather than swallowing
    /// the message to be safe.
    /// </summary>
    private static string Redact(string text, TenantId tenant)
        => string.IsNullOrEmpty(text) || !tenant.IsValid
            ? text
            : text.Replace(tenant.Value, tenant.ToLogString(), StringComparison.Ordinal);

    /// <summary>
    /// The directory one tenant's daily files live in. The local tenant keeps the root directory it has
    /// always used; every other tenant gets its own folder beneath it. A tenant id that is not a plain
    /// identifier is refused loudly rather than scrubbed - scrubbing is how two tenants quietly share a
    /// folder, and this folder holds prompt TEXT.
    /// </summary>
    public string DirectoryFor(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A prompt-log partition needs a valid tenant; an unresolved tenant is denied, never defaulted.", nameof(tenant));
        // The local tenant is the root directory it has always used - self-host unchanged, nothing migrates.
        if (tenant.IsLocal)
            return _directory;

        // Every other partition must be a minted account tenant - including the reserved SYSTEM tenant, which
        // is deliberately REFUSED here rather than given a folder: no prompt text belongs to it, so the safe
        // answer is that it has no partition at all. A value that is not a minted account is refused, never
        // coerced: this folder holds prompt TEXT, and scrubbing a bad name is how two tenants share a folder.
        if (!IsMintedAccountTenant(tenant.Value))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' is not a minted account tenant and cannot name a prompt-log partition.",
                nameof(tenant));

        var combined = Path.Combine(_directory, "tenants", tenant.Value);

        // Belt and braces, because the cost of being wrong here is one tenant reading another's prompts: the
        // result must actually LIE INSIDE the partition root. The pattern above already excludes traversal,
        // so this can only fire if that pattern is ever loosened - which is exactly when it is wanted.
        var expectedRoot = Path.GetFullPath(Path.Combine(_directory, "tenants")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(combined).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' resolves outside the prompt-log partition root.", nameof(tenant));

        return combined;
    }

    /// <summary>The daily file a message at <paramref name="utcNow"/> lands in, for one tenant.</summary>
    public string FileFor(TenantId tenant, DateTime utcNow)
        => Path.Combine(DirectoryFor(tenant), $"conversation-{utcNow:yyyyMMdd}.jsonl");

    /// <summary>
    /// Append messages pushed by a Director, into that Director's tenant partition. Returns how many were
    /// written. Never throws: a logging failure must not fail the Director's push.
    /// </summary>
    public int Append(TenantId tenant, IEnumerable<PromptRecord> records)
    {
        var directory = DirectoryFor(tenant);
        var gate = GateFor(tenant);
        var written = 0;
        try
        {
            // Group by day so a batch spanning midnight (or a backfill spanning months) lands in the
            // right daily files rather than all in today's.
            foreach (var day in records.GroupBy(r => r.TsUtc.Date))
            {
                var lines = day.Select(r => JsonSerializer.Serialize(r, JsonOpts)).ToList();
                var path = FileFor(tenant, day.Key);
                lock (gate)
                {
                    Directory.CreateDirectory(directory);
                    File.AppendAllLines(path, lines);
                }
                written += lines.Count;
            }
        }
        catch (Exception ex)
        {
            // The exception message from a file operation carries the FULL PATH, and on hosted that path
            // contains the tenant's raw account id - so logging it verbatim would print account identifiers
            // into a log that is otherwise free of them. Redacted, not dropped: the failure still says what
            // went wrong and which partition, in the same hashed form every other tenant-bearing log uses.
            FileLog.Write($"[GatewayPromptLog] Append FAILED (swallowed) for tenant={tenant.ToLogString()}: {Redact(ex.Message, tenant)}");
        }
        return written;
    }

    /// <summary>
    /// Read every message in the inclusive UTC day range for ONE tenant, oldest first. Skips unparseable
    /// lines rather than failing the whole read, so one bad line cannot hide a month of work.
    /// </summary>
    public IReadOnlyList<PromptRecord> Read(TenantId tenant, DateTime fromUtc, DateTime toUtc)
    {
        var results = new List<PromptRecord>();
        var gate = GateFor(tenant);
        for (var day = fromUtc.Date; day <= toUtc.Date; day = day.AddDays(1))
        {
            var path = FileFor(tenant, day);
            if (!File.Exists(path)) continue;
            string[] lines;
            try
            {
                lock (gate) { lines = File.ReadAllLines(path); }
            }
            catch (Exception ex)
            {
                // Same reason as Append: the path itself names the tenant on hosted. The daily FILE name is
                // safe and is what actually identifies which read failed.
                FileLog.Write($"[GatewayPromptLog] Read FAILED for tenant={tenant.ToLogString()} file={Path.GetFileName(path)}: {Redact(ex.Message, tenant)}");
                continue;
            }
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<PromptRecord>(line, JsonOpts);
                    if (record is not null) results.Add(record);
                }
                catch (JsonException ex)
                {
                    FileLog.Write($"[GatewayPromptLog] Skipping unparseable line in {Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Every message ONE tenant holds, oldest day first - the account-export read. Enumerates the daily
    /// files that actually exist in the tenant's partition rather than iterating a date range, so the whole
    /// history is returned without knowing how far back it goes.
    /// </summary>
    public IReadOnlyList<PromptRecord> ReadAll(TenantId tenant)
    {
        var directory = DirectoryFor(tenant);
        if (!Directory.Exists(directory)) return Array.Empty<PromptRecord>();

        var days = DailyFilesIn(directory).Keys.OrderBy(d => d).ToList();
        if (days.Count == 0) return Array.Empty<PromptRecord>();
        return Read(tenant, days[0], days[^1]);
    }

    /// <summary>
    /// Delete EVERY daily file in one tenant's partition - the account right-to-erasure. This is
    /// DevThrottle's only copy of the prompt log, so when this returns the service holds none of that
    /// tenant's prompt history; the Director's own local files on the member's machine are a separate
    /// matter and are not reached from here (issue #2380). Deliberately
    /// LOUD on failure, unlike <see cref="Append"/>: a delete the caller believes happened but did not is a
    /// broken promise about customer data, so an IO failure propagates and the endpoint reports it.
    /// Returns how many daily files were removed.
    /// </summary>
    public int DeleteAll(TenantId tenant)
    {
        var directory = DirectoryFor(tenant);
        if (!Directory.Exists(directory)) return 0;

        var deleted = 0;
        lock (GateFor(tenant))
        {
            foreach (var path in DailyFilesIn(directory).Values)
            {
                File.Delete(path);
                deleted++;
            }
        }
        FileLog.Write($"[GatewayPromptLog] DeleteAll: tenant={tenant.ToLogString()}, deleted {deleted} daily files");
        return deleted;
    }

    /// <summary>
    /// Retention enforcement: delete every daily file, in EVERY partition, whose day is strictly before the
    /// cutoff's day. Sweeps the partitions found ON DISK (the Local root plus every folder under tenants/)
    /// rather than a tenant census, so a partition orphaned by a deleted tenant still ages out instead of
    /// holding that customer's prompt text forever. Granularity is the daily file: a file is removed only
    /// once its whole day is past the cutoff, so a record lives at most one day past the window.
    /// Per-file failures are logged and skipped - a locked file must not stop the rest of the sweep - and
    /// the file that failed is simply retried on the next pass. Returns how many files were removed.
    /// </summary>
    public int PurgeOlderThan(DateTime cutoffUtc)
    {
        var deleted = 0;
        deleted += PurgePartition(TenantId.Local, _directory, cutoffUtc);

        var tenantsRoot = Path.Combine(_directory, "tenants");
        if (Directory.Exists(tenantsRoot))
        {
            foreach (var partition in Directory.GetDirectories(tenantsRoot))
            {
                // The folder name IS the tenant id; an orphaned or malformed folder still gets its own gate
                // (keyed by the raw name) so the purge never crosses another partition's lock.
                deleted += PurgePartition(new TenantId(Path.GetFileName(partition)), partition, cutoffUtc);
            }
        }
        return deleted;
    }

    private int PurgePartition(TenantId tenant, string directory, DateTime cutoffUtc)
    {
        if (!Directory.Exists(directory)) return 0;
        var deleted = 0;
        lock (GateFor(tenant))
        {
            foreach (var (day, path) in DailyFilesIn(directory))
            {
                if (day >= cutoffUtc.Date) continue;
                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayPromptLog] PurgePartition: could not delete {Path.GetFileName(path)} for tenant={tenant.ToLogString()}: {Redact(ex.Message, tenant)}");
                }
            }
        }
        return deleted;
    }

    /// <summary>
    /// The daily files in one partition directory, keyed by their UTC day. Only files matching the exact
    /// conversation-yyyyMMdd.jsonl shape are returned - anything else in the folder is not this log's to
    /// touch, and deleting by parsed-name-only is how a purge eats a file it does not own.
    /// </summary>
    private static Dictionary<DateTime, string> DailyFilesIn(string directory)
    {
        var files = new Dictionary<DateTime, string>();
        foreach (var path in Directory.GetFiles(directory, "conversation-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var stamp = name.Substring("conversation-".Length);
            if (DateTime.TryParseExact(stamp, "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var day))
                files[day.Date] = path;
        }
        return files;
    }
}
