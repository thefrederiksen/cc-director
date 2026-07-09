using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Durable store of <see cref="Mission"/> records (see
/// docs/new_architecture/mission-as-first-class-unit-of-work.md). A Mission is a first-class persisted
/// record that sessions attach to, so it MUST survive a Director restart - this store persists the whole
/// set to <c>missions.json</c> in the same director tool-config location as <see cref="SessionStateStore"/>
/// keeps <c>sessions.json</c>. Mirrors that store's JSON options + logging shape, but exposes a record API
/// (Create / Get / List / Delete) because a Mission is an addressable record, not a rebuilt-on-save list.
///
/// Every mutating call reads, mutates, and rewrites the file under a single lock so concurrent Control API
/// requests cannot interleave a half-written set.
/// </summary>
public sealed class MissionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();

    public string FilePath { get; }

    public MissionStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            CcStorage.ToolConfig("director"),
            "missions.json");
        FileLog.Write($"[MissionStore] Initialized: FilePath={FilePath}");
    }

    /// <summary>
    /// Create and persist a new Mission with a freshly minted id. <paramref name="missionName"/> is
    /// required (a blank name throws) and <paramref name="parentMissionId"/> nests it under a parent when
    /// given. Returns the created record.
    /// </summary>
    public Mission Create(string missionName, Guid? parentMissionId = null)
    {
        if (string.IsNullOrWhiteSpace(missionName))
            throw new ArgumentException("missionName is required", nameof(missionName));

        FileLog.Write($"[MissionStore] Create: name=\"{missionName}\" parent={parentMissionId?.ToString() ?? "(none)"}");

        var mission = new Mission
        {
            MissionId = Guid.NewGuid(),
            MissionName = missionName.Trim(),
            ParentMissionId = parentMissionId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        lock (_lock)
        {
            var missions = LoadAll();
            missions.Add(mission);
            SaveAll(missions);
        }

        FileLog.Write($"[MissionStore] Create: created mission {mission.MissionId}");
        return mission;
    }

    /// <summary>Return the Mission with the given id, or null when none exists.</summary>
    public Mission? Get(Guid missionId)
    {
        lock (_lock)
            return LoadAll().FirstOrDefault(m => m.MissionId == missionId);
    }

    /// <summary>Return every stored Mission, oldest first.</summary>
    public IReadOnlyList<Mission> List()
    {
        lock (_lock)
            return LoadAll().OrderBy(m => m.CreatedAt).ToList();
    }

    /// <summary>Delete the Mission with the given id. Returns true when a record was removed.</summary>
    public bool Delete(Guid missionId)
    {
        FileLog.Write($"[MissionStore] Delete: mission={missionId}");
        lock (_lock)
        {
            var missions = LoadAll();
            var removed = missions.RemoveAll(m => m.MissionId == missionId) > 0;
            if (removed)
                SaveAll(missions);
            return removed;
        }
    }

    /// <summary>Read the full set from disk. An absent file is an empty set; a corrupt file fails loudly.</summary>
    private List<Mission> LoadAll()
    {
        if (!File.Exists(FilePath))
            return new List<Mission>();

        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<List<Mission>>(json, JsonOptions) ?? new List<Mission>();
    }

    /// <summary>Write the full set to disk, creating the directory when missing.</summary>
    private void SaveAll(List<Mission> missions)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException($"Cannot determine directory from path: {FilePath}");

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            FileLog.Write($"[MissionStore] SaveAll: created directory {dir}");
        }

        var serialized = JsonSerializer.Serialize(missions, JsonOptions);
        File.WriteAllText(FilePath, serialized);
        FileLog.Write($"[MissionStore] SaveAll: saved {missions.Count} mission(s)");
    }
}
