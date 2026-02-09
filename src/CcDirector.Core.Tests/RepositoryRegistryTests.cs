using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests;

public class RepositoryRegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public RepositoryRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RepoRegistryTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "repositories.json");
    }

    [Fact]
    public void Load_CreatesFileIfNotExists()
    {
        var registry = new RepositoryRegistry(_filePath);
        registry.Load();

        Assert.True(File.Exists(_filePath));
        Assert.Empty(registry.Repositories);
    }

    [Fact]
    public void Load_ReadsExistingEntries()
    {
        File.WriteAllText(_filePath, """
            [
                { "Name": "my-repo", "Path": "C:\\Repos\\my-repo" }
            ]
            """);

        var registry = new RepositoryRegistry(_filePath);
        registry.Load();

        Assert.Single(registry.Repositories);
        Assert.Equal("my-repo", registry.Repositories[0].Name);
        Assert.Equal("C:\\Repos\\my-repo", registry.Repositories[0].Path);
    }

    [Fact]
    public void TryAdd_NewRepo_ReturnsTrue_Persists()
    {
        var repoDir = Path.Combine(_tempDir, "test-repo");
        Directory.CreateDirectory(repoDir);

        var registry = new RepositoryRegistry(_filePath);
        registry.Load();

        var result = registry.TryAdd(repoDir);

        Assert.True(result);
        Assert.Single(registry.Repositories);

        // Verify persisted to disk
        var registry2 = new RepositoryRegistry(_filePath);
        registry2.Load();
        Assert.Single(registry2.Repositories);
        Assert.Equal(repoDir, registry2.Repositories[0].Path);
    }

    [Fact]
    public void TryAdd_DuplicatePath_ReturnsFalse()
    {
        var repoDir = Path.Combine(_tempDir, "test-repo");
        Directory.CreateDirectory(repoDir);

        var registry = new RepositoryRegistry(_filePath);
        registry.Load();

        registry.TryAdd(repoDir);
        var result = registry.TryAdd(repoDir);

        Assert.False(result);
        Assert.Single(registry.Repositories);
    }

    [Fact]
    public void TryAdd_DuplicatePath_CaseInsensitive()
    {
        var repoDir = Path.Combine(_tempDir, "test-repo");
        Directory.CreateDirectory(repoDir);

        var registry = new RepositoryRegistry(_filePath);
        registry.Load();

        registry.TryAdd(repoDir.ToLower());
        var result = registry.TryAdd(repoDir.ToUpper());

        Assert.False(result);
        Assert.Single(registry.Repositories);
    }

    [Fact]
    public void TryAdd_DerivesNameFromFolder()
    {
        var repoDir = Path.Combine(_tempDir, "my-awesome-project");
        Directory.CreateDirectory(repoDir);

        var registry = new RepositoryRegistry(_filePath);
        registry.Load();

        registry.TryAdd(repoDir);

        Assert.Equal("my-awesome-project", registry.Repositories[0].Name);
    }

    [Fact]
    public void SeedFrom_AddsOnlyNewEntries()
    {
        var repo1 = Path.Combine(_tempDir, "repo1");
        var repo2 = Path.Combine(_tempDir, "repo2");
        Directory.CreateDirectory(repo1);
        Directory.CreateDirectory(repo2);

        var registry = new RepositoryRegistry(_filePath);
        registry.Load();

        // Pre-add repo1
        registry.TryAdd(repo1);

        // Seed both
        var configs = new[]
        {
            new RepositoryConfig { Name = "repo1", Path = repo1 },
            new RepositoryConfig { Name = "repo2", Path = repo2 }
        };
        registry.SeedFrom(configs);

        Assert.Equal(2, registry.Repositories.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
