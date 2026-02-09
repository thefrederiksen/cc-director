using System.Text.Json;

namespace CcDirector.Core.Configuration;

public class RepositoryRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly List<RepositoryConfig> _repositories = new();

    public string FilePath { get; }
    public IReadOnlyList<RepositoryConfig> Repositories => _repositories.AsReadOnly();

    public RepositoryRegistry(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CcDirector",
            "repositories.json");
    }

    public void Load()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(FilePath))
        {
            File.WriteAllText(FilePath, "[]");
            return;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<List<RepositoryConfig>>(json, JsonOptions);
            if (loaded != null)
            {
                _repositories.Clear();
                _repositories.AddRange(loaded);
            }
        }
        catch
        {
            // If the file is corrupt, start fresh
        }
    }

    public bool TryAdd(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

        var duplicate = _repositories.Any(r =>
            string.Equals(
                Path.GetFullPath(r.Path).TrimEnd('\\', '/'),
                normalized,
                StringComparison.OrdinalIgnoreCase));

        if (duplicate)
            return false;

        var name = Path.GetFileName(normalized);
        _repositories.Add(new RepositoryConfig { Name = name, Path = normalized });
        Save();
        return true;
    }

    public bool Remove(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

        var index = _repositories.FindIndex(r =>
            string.Equals(
                Path.GetFullPath(r.Path).TrimEnd('\\', '/'),
                normalized,
                StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return false;

        _repositories.RemoveAt(index);
        Save();
        return true;
    }

    public void SeedFrom(IEnumerable<RepositoryConfig> repos)
    {
        foreach (var repo in repos)
        {
            if (!string.IsNullOrWhiteSpace(repo.Path))
                TryAdd(repo.Path);
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_repositories, JsonOptions);
        File.WriteAllText(FilePath, json);
    }
}
