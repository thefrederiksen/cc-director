using System.Text.Json;
using CcDirector.Core.Utilities;

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
/// PERSISTENCE (SnoozeRegistry precedent): the whole store is ONE plain JSON file at the path the
/// constructor receives (production: mission-notes.json in the Gateway data dir). Every mutation writes
/// through immediately with an atomic temp-file + rename, so a crash mid-write can never half-truncate
/// the store. On construction the file is loaded back so a Gateway restart re-serves every WHY. A corrupt
/// file is quarantined (never silently overwritten) and the store starts empty so the Gateway still boots.
///
/// NO FALLBACK (CLAUDE.md): a failed persist is a LOGGED error that PROPAGATES - a WHY that cannot be
/// written to disk would silently vanish on the next restart, so the caller's request fails loudly.
/// </summary>
public sealed class MissionNoteStore
{
    private readonly object _gate = new();
    private readonly string _path;

    // normalized mission key -> note. Ordinal keys: the key is already lower-cased by NormalizeKey.
    private readonly Dictionary<string, MissionNote> _notes = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <param name="path">
    /// The JSON file the store persists to. REQUIRED so no caller can silently land on the real user's
    /// file: production (<see cref="GatewayHost"/>) passes mission-notes.json in the Gateway data dir;
    /// tests pass an isolated temp path.
    /// </param>
    /// <exception cref="ArgumentException">The path is null/empty/whitespace.</exception>
    public MissionNoteStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("store path is required", nameof(path));
        _path = path;
        Load();
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
    /// resulting note, or null when the note was cleared/left unset. Written through to disk before
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
            var trimmed = (why ?? "").Trim();
            if (trimmed.Length == 0)
            {
                if (_notes.Remove(key))
                {
                    Save();
                    FileLog.Write($"[MissionNoteStore] Set: cleared why for mission='{display}' (key={key})");
                }
                return null;
            }

            var note = new MissionNote(key, display, trimmed, nowUtc.ToUniversalTime());
            _notes[key] = note;
            Save();
            FileLog.Write($"[MissionNoteStore] Set: mission='{display}' (key={key}), why length={trimmed.Length}");
            return note;
        }
    }

    /// <summary>The note for a mission display name, or null when none is set.</summary>
    public MissionNote? Get(string mission)
    {
        var key = NormalizeKey(mission);
        if (key.Length == 0) return null;
        lock (_gate)
            return _notes.TryGetValue(key, out var n) ? n : null;
    }

    /// <summary>A snapshot of every set WHY, ordered by key so the read is stable. A copy, detached from
    /// the live collection.</summary>
    public IReadOnlyList<MissionNote> All()
    {
        lock (_gate)
            return _notes.Values.OrderBy(n => n.Key, StringComparer.Ordinal).ToList();
    }

    // ---- persistence (SnoozeRegistry precedent) ----------------------------------------------

    /// <summary>The on-disk shape: one document holding every mission WHY.</summary>
    private sealed class StoreFile
    {
        public List<MissionNote> Notes { get; set; } = new();
    }

    /// <summary>
    /// Load the store file written by a previous Gateway run so a restart re-serves every WHY. A missing
    /// file is the normal first boot (empty store, logged). A corrupt file is quarantined (renamed with a
    /// timestamp suffix) so its bytes are preserved for the operator and never silently overwritten; the
    /// store then starts empty so the Gateway still boots.
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[MissionNoteStore] Load: no store file at {_path}; starting empty");
            return;
        }

        StoreFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), FileJsonOptions);
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }

        if (parsed is null)
        {
            Quarantine("file deserialized to null (no store document)");
            return;
        }

        var loaded = 0;
        foreach (var n in parsed.Notes)
        {
            var key = NormalizeKey(n.Mission);
            if (key.Length == 0 || string.IsNullOrWhiteSpace(n.Why))
                continue; // skip a malformed/empty row rather than fail the whole boot
            _notes[key] = n with { Key = key, UpdatedAtUtc = n.UpdatedAtUtc.ToUniversalTime() };
            loaded++;
        }

        FileLog.Write($"[MissionNoteStore] Load: loaded {loaded} mission why(s) from {_path}");
    }

    /// <summary>
    /// Preserve an unreadable store file as "&lt;path&gt;.corrupt-&lt;stamp&gt;" and log loudly. The
    /// original path is then free for the next write-through. If even the quarantine fails, the exception
    /// propagates and the Gateway does not start half-blind.
    /// </summary>
    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[MissionNoteStore] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty. Operator action: inspect the quarantined file to recover mission whys.");
    }

    /// <summary>
    /// Write-through: serialize the whole store and atomically replace the file (temp + rename), so a
    /// concurrent reader or a crash mid-write never sees a half-written store. Called inside the lock by
    /// every mutation. A failed save is a LOGGED error that PROPAGATES (the caller's request fails
    /// loudly) - never a silent skip, because a WHY that did not persist would vanish on the next restart.
    /// </summary>
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new StoreFile { Notes = _notes.Values.ToList() };
            var json = JsonSerializer.Serialize(file, FileJsonOptions);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MissionNoteStore] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}
