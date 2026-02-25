using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

public class RootDirectoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly List<RootDirectoryConfig> _roots = new();

    public string FilePath { get; }
    public IReadOnlyList<RootDirectoryConfig> Roots => _roots.AsReadOnly();

    public RootDirectoryStore(string? filePath = null)
    {
        FilePath = filePath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CcDirector",
            "root-directories.json");
    }

    public void Load()
    {
        FileLog.Write($"[RootDirectoryStore] Load: path={FilePath}");

        var dir = System.IO.Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {FilePath}");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(FilePath))
        {
            File.WriteAllText(FilePath, "[]");
            FileLog.Write("[RootDirectoryStore] Load: created empty file");
            return;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<List<RootDirectoryConfig>>(json, JsonOptions);
            if (loaded != null)
            {
                _roots.Clear();
                _roots.AddRange(loaded);
            }
            FileLog.Write($"[RootDirectoryStore] Load: loaded {_roots.Count} root directories");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RootDirectoryStore] Load FAILED: {ex.Message}");
        }
    }

    public void Add(RootDirectoryConfig config)
    {
        FileLog.Write($"[RootDirectoryStore] Add: label={config.Label}, path={config.Path}, provider={config.Provider}");
        _roots.Add(config);
        Save();
    }

    public void Update(int index, RootDirectoryConfig config)
    {
        FileLog.Write($"[RootDirectoryStore] Update: index={index}, label={config.Label}");
        if (index < 0 || index >= _roots.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} out of range (count={_roots.Count})");

        _roots[index] = config;
        Save();
    }

    public void Remove(int index)
    {
        if (index < 0 || index >= _roots.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} out of range (count={_roots.Count})");

        FileLog.Write($"[RootDirectoryStore] Remove: index={index}, label={_roots[index].Label}");
        _roots.RemoveAt(index);
        Save();
    }

    private void Save()
    {
        FileLog.Write($"[RootDirectoryStore] Save: writing {_roots.Count} entries");
        var json = JsonSerializer.Serialize(_roots, JsonOptions);
        File.WriteAllText(FilePath, json);
    }
}
