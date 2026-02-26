using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class GitIgnoreServiceTests : IDisposable
{
    private readonly string _tempDir;

    public GitIgnoreServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GitIgnoreTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task AddEntryAsync_CreatesGitignore_WhenNotExists()
    {
        var result = await GitIgnoreService.AddEntryAsync(_tempDir, "bin/");

        Assert.True(result);
        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        Assert.True(File.Exists(gitignorePath));
        var content = File.ReadAllText(gitignorePath);
        Assert.Equal("bin/\n", content);
    }

    [Fact]
    public async Task AddEntryAsync_AppendsToExistingGitignore()
    {
        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        File.WriteAllText(gitignorePath, "obj/\n");

        var result = await GitIgnoreService.AddEntryAsync(_tempDir, "bin/");

        Assert.True(result);
        var content = File.ReadAllText(gitignorePath);
        Assert.Equal("obj/\nbin/\n", content);
    }

    [Fact]
    public async Task AddEntryAsync_SkipsDuplicate_ReturnsFalse()
    {
        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        File.WriteAllText(gitignorePath, "bin/\n");

        var result = await GitIgnoreService.AddEntryAsync(_tempDir, "bin/");

        Assert.False(result);
        var content = File.ReadAllText(gitignorePath);
        Assert.Equal("bin/\n", content);
    }

    [Fact]
    public async Task AddEntryAsync_NormalizesBackslashes()
    {
        var result = await GitIgnoreService.AddEntryAsync(_tempDir, "src\\temp\\file.txt");

        Assert.True(result);
        var content = File.ReadAllText(Path.Combine(_tempDir, ".gitignore"));
        Assert.Equal("src/temp/file.txt\n", content);
    }

    [Fact]
    public async Task AddEntryAsync_HandlesTrailingSlashForFolders()
    {
        var result = await GitIgnoreService.AddEntryAsync(_tempDir, ".claude/");

        Assert.True(result);
        var content = File.ReadAllText(Path.Combine(_tempDir, ".gitignore"));
        Assert.Equal(".claude/\n", content);
    }

    [Fact]
    public async Task AddEntryAsync_EnsuresNewlineBeforeAppend()
    {
        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        File.WriteAllText(gitignorePath, "obj/"); // no trailing newline

        var result = await GitIgnoreService.AddEntryAsync(_tempDir, "bin/");

        Assert.True(result);
        var content = File.ReadAllText(gitignorePath);
        Assert.Equal("obj/\nbin/\n", content);
    }

    [Fact]
    public async Task AddEntryAsync_HandlesEmptyGitignore()
    {
        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        File.WriteAllText(gitignorePath, "");

        var result = await GitIgnoreService.AddEntryAsync(_tempDir, "bin/");

        Assert.True(result);
        var content = File.ReadAllText(gitignorePath);
        Assert.Equal("bin/\n", content);
    }

    [Fact]
    public void EntryExists_ReturnsFalse_WhenNoGitignore()
    {
        var result = GitIgnoreService.EntryExists(_tempDir, "bin/");
        Assert.False(result);
    }

    [Fact]
    public void EntryExists_ReturnsTrue_WhenEntryPresent()
    {
        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        File.WriteAllText(gitignorePath, "bin/\nobj/\n");

        Assert.True(GitIgnoreService.EntryExists(_tempDir, "bin/"));
        Assert.True(GitIgnoreService.EntryExists(_tempDir, "obj/"));
    }

    [Fact]
    public void EntryExists_ReturnsFalse_WhenEntryNotPresent()
    {
        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        File.WriteAllText(gitignorePath, "bin/\n");

        Assert.False(GitIgnoreService.EntryExists(_tempDir, "obj/"));
    }

    [Fact]
    public void EntryExists_IgnoresComments()
    {
        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        File.WriteAllText(gitignorePath, "# bin/\n");

        Assert.False(GitIgnoreService.EntryExists(_tempDir, "bin/"));
    }

    [Fact]
    public void EntryExists_NormalizesBackslashes()
    {
        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        File.WriteAllText(gitignorePath, "src/temp/file.txt\n");

        Assert.True(GitIgnoreService.EntryExists(_tempDir, "src\\temp\\file.txt"));
    }

    [Fact]
    public void NormalizeEntry_ConvertsBackslashes()
    {
        Assert.Equal("src/folder/file.txt", GitIgnoreService.NormalizeEntry("src\\folder\\file.txt"));
    }

    [Fact]
    public void NormalizeEntry_PreservesForwardSlashes()
    {
        Assert.Equal("src/folder/file.txt", GitIgnoreService.NormalizeEntry("src/folder/file.txt"));
    }

    [Fact]
    public async Task AddEntryAsync_DuplicateDetectsNormalizedForm()
    {
        var gitignorePath = Path.Combine(_tempDir, ".gitignore");
        File.WriteAllText(gitignorePath, "src/temp/file.txt\n");

        // Adding with backslashes should detect the existing forward-slash version
        var result = await GitIgnoreService.AddEntryAsync(_tempDir, "src\\temp\\file.txt");
        Assert.False(result);
    }
}
