using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

public class ClaudeSessionReaderMarkerTests : IDisposable
{
    private readonly string _tempDir;

    public ClaudeSessionReaderMarkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"MarkerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void FindSessionByMarker_FindsMatchingFile()
    {
        // Arrange - create a .jsonl file containing the marker GUID
        var sessionId = Guid.NewGuid().ToString();
        var marker = Guid.NewGuid().ToString();
        var jsonlPath = Path.Combine(_tempDir, $"{sessionId}.jsonl");

        File.WriteAllText(jsonlPath, string.Join("\n",
            $"{{\"type\":\"user\",\"message\":\"Just ignore this: {marker}\"}}",
            "{\"type\":\"assistant\",\"message\":\"Got it, ignored.\"}"));

        // Act
        var found = ClaudeSessionReader.FindSessionByMarkerInFolder(_tempDir, marker);

        // Assert
        Assert.Equal(sessionId, found);
    }

    [Fact]
    public void FindSessionByMarker_ReturnsNullWhenNotFound()
    {
        // Arrange - create a .jsonl file without the marker
        var sessionId = Guid.NewGuid().ToString();
        var jsonlPath = Path.Combine(_tempDir, $"{sessionId}.jsonl");

        File.WriteAllText(jsonlPath,
            "{\"type\":\"user\",\"message\":\"Some other prompt\"}");

        // Act
        var found = ClaudeSessionReader.FindSessionByMarkerInFolder(_tempDir, Guid.NewGuid().ToString());

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public void FindSessionByMarker_ReturnsNullForEmptyFolder()
    {
        var found = ClaudeSessionReader.FindSessionByMarkerInFolder(_tempDir, Guid.NewGuid().ToString());
        Assert.Null(found);
    }

    [Fact]
    public void FindSessionByMarker_ReturnsNullForNonExistentFolder()
    {
        var nonExistent = Path.Combine(_tempDir, "does_not_exist");
        var found = ClaudeSessionReader.FindSessionByMarkerInFolder(nonExistent, Guid.NewGuid().ToString());
        Assert.Null(found);
    }

    [Fact]
    public void FindSessionByMarker_ReturnsNullForNullOrEmptyMarker()
    {
        Assert.Null(ClaudeSessionReader.FindSessionByMarkerInFolder(_tempDir, ""));
#pragma warning disable CS8625 // Testing null safety — deliberately passing null to non-nullable parameter
        Assert.Null(ClaudeSessionReader.FindSessionByMarkerInFolder(_tempDir, null));
#pragma warning restore CS8625
    }

    [Fact]
    public void FindSessionByMarker_SkipsFilesWithoutMarker()
    {
        // Arrange - create multiple files, only one has the marker
        var marker = Guid.NewGuid().ToString();

        var file1Id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_tempDir, $"{file1Id}.jsonl"),
            "{\"type\":\"user\",\"message\":\"No marker here\"}");

        var file2Id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_tempDir, $"{file2Id}.jsonl"),
            $"{{\"type\":\"user\",\"message\":\"Just ignore this: {marker}\"}}");

        var file3Id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_tempDir, $"{file3Id}.jsonl"),
            "{\"type\":\"user\",\"message\":\"Also no marker\"}");

        // Act
        var found = ClaudeSessionReader.FindSessionByMarkerInFolder(_tempDir, marker);

        // Assert
        Assert.Equal(file2Id, found);
    }

    [Fact]
    public void FindSessionByMarker_HandlesCorruptFiles()
    {
        // Arrange - one corrupt file, one valid file with marker
        var marker = Guid.NewGuid().ToString();

        File.WriteAllText(Path.Combine(_tempDir, "corrupt.jsonl"), "not valid at all {{{");

        var validId = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_tempDir, $"{validId}.jsonl"),
            $"{{\"type\":\"user\",\"message\":\"Just ignore this: {marker}\"}}");

        // Act - should not throw, should find the valid file
        var found = ClaudeSessionReader.FindSessionByMarkerInFolder(_tempDir, marker);

        // Assert
        Assert.Equal(validId, found);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
