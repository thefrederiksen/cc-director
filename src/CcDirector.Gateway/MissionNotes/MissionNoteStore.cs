using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.MissionNotes;

/// <summary>
/// The Gateway-owned, restart-surviving store of each mission's WHY (Mission Screen mission, Phase 1b,
/// issue #1405). The Mission Screen's founding rule is that every mission states a WHY shown front and
/// center; a missing WHY is a loud flag. That WHY must be DURABLE and SHARED - every Cockpit, the phone,
/// and the future Mission-Control chat/API must read the same WHY - so it lives here on the Gateway, not
/// in any one browser's local storage.
///
/// KEYING (Phase 1b is a known stepping stone): a note is keyed by the mission's NORMALIZED name - the
/// SAME lowercased key the Cockpit's groupByMission derives from the "&lt;Mission&gt; - &lt;Role&gt;"
/// session-name convention - so the note attaches to the derived mission regardless of display casing.
/// The display name is stored alongside for reference. This name-keying ORPHANS a note if a mission's
/// sessions are renamed; when missions become first-class objects (Phase 3) with a stable id - the
/// Gateway-native <see cref="CcDirector.Core.Sessions.MissionStore"/> (missions.json) is that target -
/// the WHY re-keys onto the stable mission id and this name-key becomes a migration source.
///
/// UNSET (issue #1405): a WHY that is empty or all-whitespace is treated as UNSET - the note is removed,
/// so the Mission Screen shows its "no why set" flag rather than a blank. A PUT with an empty why is the
/// clear path.
///
/// PERSISTENCE (Hosted Gateway mission, Step 1b): notes live in the EF data layer's <c>mission_notes</c>
/// table (SQLite locally), keyed by the normalized mission name, NOT the old hand-rolled
/// <c>mission-notes.json</c>. The public API and observable behavior are unchanged. On first run after the
/// upgrade a legacy <c>mission-notes.json</c> is imported once through the shared recoverable-import helper.
///
/// CORRUPT-FILE = QUARANTINE, deliberately (not fail-loud): this is a COSMETIC store, and a corrupt WHY file
/// must NOT block Gateway boot. So the import uses <see cref="CorruptFilePolicy.Quarantine"/> - a corrupt
/// legacy file is renamed aside as <c>.corrupt-&lt;stamp&gt;</c> (bytes preserved for the operator) and the
/// store boots empty, reproducing the long-standing quarantine behaviour exactly. This is the per-store
/// criticality contract: FAIL-LOUD for operationally-critical stores (never silently lose a scheduled job,
/// claim, or hold), QUARANTINE-and-continue for cosmetic stores that must not block boot. A failed WRITE is
/// still fail-loud (a WHY that cannot persist would vanish on restart, so the request fails loudly).
///
/// Threading: the Gateway is a single writer. Every operation runs under this store's write lock over a
/// fresh pooled context.
/// </summary>
public sealed class MissionNoteStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly string _legacyJsonPath;

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <param name="legacyJsonPath">The legacy <c>mission-notes.json</c> path to import ONCE if it exists and
    /// the table is empty. REQUIRED (no silent default).</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    /// <exception cref="ArgumentException">The legacy path is null/empty/whitespace.</exception>
    public MissionNoteStore(GatewayDatabase db, string legacyJsonPath)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrWhiteSpace(legacyJsonPath))
            throw new ArgumentException("legacy json path is required", nameof(legacyJsonPath));
        _legacyJsonPath = legacyJsonPath;

        lock (_gate)
            ImportLegacyJsonIfNeeded();
    }

    /// <summary>One mission's WHY: the normalized grouping key, the display name as last written, the
    /// WHY text, and when it was last set.</summary>
    public sealed record MissionNote(string Key, string Mission, string Why, DateTime UpdatedAtUtc);

    /// <summary>
    /// The grouping key for a mission display name: trimmed and lower-cased, matching the Cockpit's
    /// groupByMission key so a note attaches to the mission regardless of display casing.
    /// </summary>
    public static string NormalizeKey(string mission) => (mission ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Set (or clear) a mission's WHY. An empty or all-whitespace <paramref name="why"/> UNSETS the note
    /// (removes it) so the screen shows its flag; any other value stores the trimmed WHY. Returns the
    /// resulting note, or null when the note was cleared/left unset. Written through to the database before
    /// returning. <paramref name="nowUtc"/> is injected so tests are deterministic.
    /// </summary>
    /// <exception cref="ArgumentException">The mission name is null/empty/whitespace.</exception>
    public MissionNote? Set(string mission, string? why, DateTime nowUtc)
    {
        var display = (mission ?? "").Trim();
        if (display.Length == 0)
            throw new ArgumentException("mission is required", nameof(mission));

        var key = NormalizeKey(display);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var existing = ctx.MissionNotes.FirstOrDefault(e => e.Key == key);

            var trimmed = (why ?? "").Trim();
            if (trimmed.Length == 0)
            {
                if (existing is not null)
                {
                    ctx.MissionNotes.Remove(existing);
                    ctx.SaveChanges();
                    FileLog.Write($"[MissionNoteStore] Set: cleared why for mission='{display}' (key={key})");
                }
                return null;
            }

            var updatedAtUtc = nowUtc.ToUniversalTime();
            if (existing is null)
            {
                ctx.MissionNotes.Add(new MissionNoteEntity
                {
                    Key = key,
                    TenantId = ctx.ActiveTenant!,
                    Mission = display,
                    Why = trimmed,
                    UpdatedAtUtc = updatedAtUtc,
                });
            }
            else
            {
                existing.Mission = display;
                existing.Why = trimmed;
                existing.UpdatedAtUtc = updatedAtUtc;
            }
            ctx.SaveChanges();
            FileLog.Write($"[MissionNoteStore] Set: mission='{display}' (key={key}), why length={trimmed.Length}");
            return new MissionNote(key, display, trimmed, updatedAtUtc);
        }
    }

    /// <summary>The note for a mission display name, or null when none is set.</summary>
    public MissionNote? Get(string mission)
    {
        var key = NormalizeKey(mission);
        if (key.Length == 0) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var e = ctx.MissionNotes.AsNoTracking().FirstOrDefault(x => x.Key == key);
            return e is null ? null : ToRecord(e);
        }
    }

    /// <summary>A snapshot of every set WHY, ordered by key so the read is stable. A copy, detached from
    /// the live collection.</summary>
    public IReadOnlyList<MissionNote> All()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            // Order by key in memory with StringComparer.Ordinal - matching the legacy OrderBy(Key, Ordinal)
            // exactly. (SQLite's ORDER BY on a TEXT column also uses BINARY/ordinal, but sorting in memory
            // keeps the ordinal guarantee independent of the provider's default collation.)
            return ctx.MissionNotes.AsNoTracking().ToList()
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(ToRecord)
                .ToList();
        }
    }

    /// <summary>
    /// MIGRATION READ ONLY: every note on the box, grouped by the tenant that owns it, as
    /// tenant -> (normalized name -> why). Bypasses the ambient tenant filter with IgnoreQueryFilters,
    /// because this runs at startup where there is no ambient tenant to scope to, and the caller needs
    /// EVERY account's notes in order to hand each account its own back.
    ///
    /// The grouping IS the safety property, not a convenience. These notes are keyed by mission NAME, and a
    /// mission name is free text a person typed - customer names, project names. Handing a flat set to a
    /// name-matching migration would give one account's stated reason for its work to another account that
    /// happened to name a mission the same thing, which is precisely the disclosure #1039 closed. So this
    /// returns the tenant WITH the data and never without it, and
    /// <see cref="Core.Sessions.MissionStore.ImportWhys"/> takes one tenant's map at a time.
    ///
    /// An UNATTRIBUTED note (no tenant) belongs to nobody and is given to nobody - the same rule the mission
    /// store applies to unattributed rows on hosted.
    ///
    /// Nothing else may use this. Every serving read goes through <see cref="All"/>, which is scoped.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> AllByTenantForMigration()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var rows = ctx.MissionNotes.AsNoTracking().IgnoreQueryFilters().ToList();

            var byTenant = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            foreach (var group in rows.GroupBy(r => r.TenantId ?? "", StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(group.Key))
                    continue;

                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var row in group)
                {
                    if (string.IsNullOrWhiteSpace(row.Key) || string.IsNullOrWhiteSpace(row.Why))
                        continue;
                    map[row.Key] = row.Why;
                }
                if (map.Count > 0)
                    byTenant[group.Key] = map;
            }
            return byTenant;
        }
    }

    private static MissionNote ToRecord(MissionNoteEntity e)
        => new(e.Key, e.Mission, e.Why, e.UpdatedAtUtc);

    // ---- one-time legacy JSON import --------------------------------------------------------------

    /// <summary>The on-disk shape: one document holding every mission WHY.</summary>
    private sealed class StoreFile
    {
        public List<MissionNote> Notes { get; set; } = new();
    }

    /// <summary>
    /// Import a legacy <c>mission-notes.json</c> exactly once, through the shared recoverable-import plumbing
    /// (<see cref="LegacyJsonImport.Recoverable"/>): import only when the file exists AND the table is empty;
    /// recover a lingering file idempotently; rename aside best-effort after a successful import. Uses
    /// <see cref="CorruptFilePolicy.Quarantine"/> because this is a cosmetic store - a corrupt WHY file is
    /// renamed aside as <c>.corrupt</c> and the store boots empty rather than blocking Gateway boot.
    /// </summary>
    private void ImportLegacyJsonIfNeeded()
        => LegacyJsonImport.Recoverable(
            _legacyJsonPath,
            "[MissionNoteStore]",
            isPopulated: () => { using var ctx = _db.CreateContext(); return ctx.MissionNotes.Any(); },
            importCommitted: ImportRowsFromLegacyJson,
            corruptFilePolicy: CorruptFilePolicy.Quarantine);

    /// <summary>
    /// Parse the legacy file and insert every note inside one transaction. Mirrors the old load exactly: skip
    /// a row with an empty normalized key or an empty why, normalize the key, last-wins on a duplicate key,
    /// and normalize the timestamp to UTC. A PARSE error (a corrupt input file) throws a
    /// <see cref="LegacyJsonImportCorruptException"/> - the helper's Quarantine policy renames it aside as
    /// <c>.corrupt</c> and boots empty (this cosmetic store must not block boot).
    /// </summary>
    private void ImportRowsFromLegacyJson()
    {
        StoreFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_legacyJsonPath), FileJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new LegacyJsonImportCorruptException(
                $"The legacy mission-notes file '{_legacyJsonPath}' could not be parsed: {ex.Message}", ex);
        }

        if (parsed is null)
            throw new LegacyJsonImportCorruptException(
                $"The legacy mission-notes file '{_legacyJsonPath}' deserialized to a null document.");

        // Reproduce the old load's row handling, INCLUDING last-wins on a duplicate normalized key (the old
        // in-memory Dictionary keyed by the normalized key overwrote), so the imported set matches.
        var toImport = new Dictionary<string, MissionNote>(StringComparer.Ordinal);
        foreach (var n in parsed.Notes)
        {
            var key = NormalizeKey(n.Mission);
            if (key.Length == 0 || string.IsNullOrWhiteSpace(n.Why))
                continue; // skip a malformed/empty row rather than fail the whole boot
            toImport[key] = n with { Key = key, UpdatedAtUtc = n.UpdatedAtUtc.ToUniversalTime() };
        }

        using var ctx = _db.CreateContext();
        using var tx = ctx.Database.BeginTransaction();
        foreach (var n in toImport.Values)
        {
            ctx.MissionNotes.Add(new MissionNoteEntity
            {
                Key = n.Key,
                TenantId = ctx.ActiveTenant!,
                Mission = n.Mission,
                Why = n.Why,
                UpdatedAtUtc = n.UpdatedAtUtc,
            });
        }
        ctx.SaveChanges();
        tx.Commit();

        FileLog.Write($"[MissionNoteStore] Import: {toImport.Count} mission why(s) imported from {_legacyJsonPath}");
    }
}
