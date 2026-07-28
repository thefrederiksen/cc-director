using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// The filename search, run over a temporary tree rather than the machine's real drives.
///
/// The truncation tests are the ones that matter most. A search that quietly returns part of the answer is
/// worse than one that fails, because the caller has no way to tell a short answer from a complete one - so
/// the bound being hit, and WHICH bound it was, are asserted rather than assumed.
/// </summary>
public sealed class FileSearchServiceTests : IDisposable
{
    private readonly string _root;

    public FileSearchServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cc-file-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temporary directory that outlives the test run is not a test failure */ }
    }

    private string CreateFile(string relativePath, string content = "x")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private FileSearchService SearchOverRoot() => new(new[] { _root });

    private FileSearchResult Search(string query, int limit = 0, int timeout = 10_000)
    {
        var result = SearchOverRoot().Search(query, limit, timeout, CancellationToken.None);
        return new FileSearchResult(result);
    }

    /// <summary>A thin wrapper so the assertions below read as sentences rather than property chains.</summary>
    private sealed record FileSearchResult(Gateway.Contracts.FileSearchResultDto Dto)
    {
        public IReadOnlyList<string> Names => Dto.Files.Select(file => file.Name).ToList();
    }

    [Fact]
    public void Search_ByExtension_FindsMatchingFilesAtEveryDepth()
    {
        CreateFile("top.pptx");
        CreateFile("nested/deeper/deck.pptx");
        CreateFile("nested/notes.txt");

        var result = Search("*.pptx");

        Assert.Equal(2, result.Dto.Files.Count);
        Assert.Contains("top.pptx", result.Names);
        Assert.Contains("deck.pptx", result.Names);
        Assert.DoesNotContain("notes.txt", result.Names);
    }

    [Fact]
    public void Search_PlainText_MatchesPartOfTheFilename()
    {
        CreateFile("Q3-budget-final.xlsx");
        CreateFile("forecast.xlsx");

        var result = Search("budget");

        Assert.Single(result.Dto.Files);
        Assert.Equal("Q3-budget-final.xlsx", result.Dto.Files[0].Name);
    }

    /// <summary>
    /// A query carrying a directory separator is about WHERE a file is, so it is matched against the whole
    /// path. Without this rule "nested/*.txt" would be tested against the bare filename and never match.
    /// </summary>
    [Fact]
    public void Search_QueryWithADirectorySeparator_MatchesAgainstTheWholePath()
    {
        CreateFile("nested/notes.txt");
        CreateFile("elsewhere/notes.txt");

        var separator = Path.DirectorySeparatorChar;
        var result = Search($"*{separator}nested{separator}*.txt");

        Assert.Single(result.Dto.Files);
        Assert.Contains("nested", result.Dto.Files[0].Path);
    }

    [Fact]
    public void Search_ReturnsSizeAndModifiedTime()
    {
        CreateFile("sized.txt", "twelve chars");

        var result = Search("sized.txt");

        Assert.Single(result.Dto.Files);
        Assert.Equal(12, result.Dto.Files[0].SizeBytes);
        Assert.True(result.Dto.Files[0].ModifiedUtc > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public void Search_NoMatches_IsCompleteRatherThanTruncated()
    {
        CreateFile("something.txt");

        var result = Search("*.nothing");

        Assert.Empty(result.Dto.Files);
        Assert.False(result.Dto.Truncated);
        Assert.Null(result.Dto.TruncationReason);
    }

    /// <summary>Hitting the result ceiling must be reported, and reported as the ceiling specifically.</summary>
    [Fact]
    public void Search_MoreMatchesThanTheLimit_IsTruncatedWithTheLimitReason()
    {
        for (var index = 0; index < 10; index++)
            CreateFile($"file{index}.txt");

        var result = Search("*.txt", limit: 3);

        Assert.Equal(3, result.Dto.Files.Count);
        Assert.True(result.Dto.Truncated);
        Assert.Equal("limit", result.Dto.TruncationReason);
    }

    /// <summary>
    /// A walk that finished abandons nothing. The field exists because a walker CAN fail to return at all -
    /// a macOS folder under privacy consent blocks in the kernel rather than denying - and a search that gave
    /// up on a root must say so rather than quietly returning less. This pins the ordinary case so the
    /// reporting cannot drift to "always zero" or "always non-zero"; the blocking case itself is not
    /// reproducible on demand and is verified on real macOS hardware instead.
    /// </summary>
    [Fact]
    public void Search_WalkThatFinished_AbandonsNoRoots()
    {
        CreateFile("thing.txt");

        var result = Search("thing.txt");

        Assert.Equal(0, result.Dto.AbandonedRoots);
        Assert.False(result.Dto.Truncated);
    }

    [Fact]
    public void Search_CompletedWalk_ReportsTheRootsItSearchedAndTheDirectoriesItVisited()
    {
        CreateFile("nested/deeper/thing.txt");

        var result = Search("*.txt");

        Assert.Contains(_root, result.Dto.Roots);
        Assert.True(result.Dto.DirectoriesVisited >= 3);
        Assert.False(result.Dto.Truncated);
    }

    [Fact]
    public void Search_EchoesTheQueryAndTheMachine()
    {
        CreateFile("thing.txt");

        var result = Search("thing.txt");

        Assert.Equal("thing.txt", result.Dto.Query);
        Assert.Equal(Environment.MachineName, result.Dto.Machine);
    }

    /// <summary>
    /// Regression guard for a defect found by testing on a real Mac: the skip list used to match bare
    /// DIRECTORY NAMES, so a developer's own "dev" folder - and any project folder called "run", "sys" or
    /// "proc" - was silently dropped from every search on every platform. The skip list is absolute paths
    /// now, so an ordinary folder that happens to share one of those names is searched like any other.
    ///
    /// This is the failure the whole class is written to avoid: not a wrong answer, a quietly short one.
    /// </summary>
    [Theory]
    [InlineData("dev")]
    [InlineData("run")]
    [InlineData("sys")]
    [InlineData("proc")]
    public void Search_AnOrdinaryFolderSharingAKernelTreeName_IsStillSearched(string folderName)
    {
        CreateFile(Path.Combine(folderName, "mine.txt"));

        var result = Search("mine.txt");

        Assert.Single(result.Dto.Files);
        Assert.Contains(folderName, result.Dto.Files[0].Path);
    }

    /// <summary>
    /// The ceiling is clamped to the service maximum however large a number the caller asks for, so one
    /// request cannot ask a machine to serialise an unbounded answer.
    /// </summary>
    [Fact]
    public void Search_LimitAboveTheMaximum_IsClampedToTheMaximum()
    {
        for (var index = 0; index < 5; index++)
            CreateFile($"file{index}.txt");

        var result = Search("*.txt", limit: FileSearchService.MaximumLimit * 10);

        Assert.Equal(5, result.Dto.Files.Count);
        Assert.False(result.Dto.Truncated);
    }
}
