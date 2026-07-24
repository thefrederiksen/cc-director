using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// A machine-local, persisted list of worktree folders git already DEREGISTERED but could not
/// physically delete because a build output was locked (inspection round 4). Git no longer lists
/// such a path as a worktree, so NO future inventory can rediscover it - the reaper would never
/// revisit it and the folder would leak forever, even though the reap UI promises it "will be
/// retried". Persisting the path here lets the reaper retry the physical delete on a later run and
/// makes that promise true. When the lock is released the retry finishes the delete and the entry is
/// dropped; if the owner removed the folder by hand the entry is dropped too.
/// </summary>
public sealed class WorktreeLeftoverStore
{
    private readonly string _file;

    /// <param name="dir">Leftover directory (defaults to the machine-local cc-director path).</param>
    public WorktreeLeftoverStore(string? dir = null)
    {
        _file = Path.Combine(dir ?? DefaultDir(), "leftovers.json");
    }

    private static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cc-director", "worktree-leftovers");

    /// <summary>The normalized folder paths recorded as undeleted leftovers.</summary>
    public IReadOnlyList<string> All()
    {
        try
        {
            if (!File.Exists(_file))
                return Array.Empty<string>();
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? new List<string>();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeLeftoverStore] read failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>Record a folder git deregistered but could not fully delete. Best-effort - idempotent.</summary>
    public void Add(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return;
        var set = new HashSet<string>(All(), StringComparer.OrdinalIgnoreCase) { normalizedPath };
        Write(set);
    }

    /// <summary>Drop a leftover once it has been deleted (retry succeeded) or removed by hand.</summary>
    public void Remove(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return;
        var set = new HashSet<string>(All(), StringComparer.OrdinalIgnoreCase);
        if (set.Remove(normalizedPath))
            Write(set);
    }

    private void Write(IEnumerable<string> paths)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(paths.ToList()));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeLeftoverStore] write failed: {ex.Message}");
        }
    }
}
