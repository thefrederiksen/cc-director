using CcDirector.Core.Voice.Services;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for ClaudeResponseExtractor - extracting assistant responses from .jsonl files.
/// </summary>
public class ClaudeResponseExtractorTests : IDisposable
{
    private readonly string _tempDir;

    public ClaudeResponseExtractorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"voice_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    [Fact]
    public void ExtractLastResponse_FileNotFound_ReturnsNull()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "nonexistent.jsonl");

        // Act
        var result = ClaudeResponseExtractor.ExtractLastResponse(path);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractLastResponse_EmptyFile_ReturnsNull()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "empty.jsonl");
        File.WriteAllText(path, "");

        // Act
        var result = ClaudeResponseExtractor.ExtractLastResponse(path);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractLastResponse_SimpleStringContent_ExtractsText()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "simple.jsonl");
        var content = """
            {"type":"user","message":"Hello"}
            {"type":"assistant","message":"Hi there! How can I help you today?"}
            """;
        File.WriteAllText(path, content);

        // Act
        var result = ClaudeResponseExtractor.ExtractLastResponse(path);

        // Assert
        Assert.Equal("Hi there! How can I help you today?", result);
    }

    [Fact]
    public void ExtractLastResponse_MessageObjectWithContent_ExtractsText()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "object.jsonl");
        var content = """
            {"type":"user","message":"What is 2+2?"}
            {"type":"assistant","message":{"content":"The answer is 4."}}
            """;
        File.WriteAllText(path, content);

        // Act
        var result = ClaudeResponseExtractor.ExtractLastResponse(path);

        // Assert
        Assert.Equal("The answer is 4.", result);
    }

    [Fact]
    public void ExtractLastResponse_ArrayContentWithTextBlocks_CombinesText()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "array.jsonl");
        var content = """
            {"type":"user","message":"Explain something"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"First part."},{"type":"text","text":"Second part."}]}}
            """;
        File.WriteAllText(path, content);

        // Act
        var result = ClaudeResponseExtractor.ExtractLastResponse(path);

        // Assert
        Assert.Equal("First part.\nSecond part.", result);
    }

    [Fact]
    public void ExtractLastResponse_MultipleResponses_ReturnsLast()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "multiple.jsonl");
        var content = """
            {"type":"user","message":"First question"}
            {"type":"assistant","message":"First answer"}
            {"type":"user","message":"Second question"}
            {"type":"assistant","message":"Second answer"}
            {"type":"user","message":"Third question"}
            {"type":"assistant","message":"Third answer - this is the last one"}
            """;
        File.WriteAllText(path, content);

        // Act
        var result = ClaudeResponseExtractor.ExtractLastResponse(path);

        // Assert
        Assert.Equal("Third answer - this is the last one", result);
    }

    [Fact]
    public void ExtractLastResponse_OnlyUserMessages_ReturnsNull()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "users_only.jsonl");
        var content = """
            {"type":"user","message":"Hello"}
            {"type":"user","message":"Are you there?"}
            """;
        File.WriteAllText(path, content);

        // Act
        var result = ClaudeResponseExtractor.ExtractLastResponse(path);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractLastResponse_MixedContentBlocks_ExtractsOnlyText()
    {
        // Arrange - simulates tool use blocks mixed with text
        var path = Path.Combine(_tempDir, "mixed.jsonl");
        var content = """
            {"type":"user","message":"Do something"}
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"123"},{"type":"text","text":"I used a tool to help."},{"type":"tool_result","id":"123"},{"type":"text","text":"Here is the result."}]}}
            """;
        File.WriteAllText(path, content);

        // Act
        var result = ClaudeResponseExtractor.ExtractLastResponse(path);

        // Assert
        Assert.Equal("I used a tool to help.\nHere is the result.", result);
    }

    [Fact]
    public void ExtractLastResponse_MalformedJsonLine_SkipsAndContinues()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "malformed.jsonl");
        var content = """
            {"type":"user","message":"Question"}
            {this is not valid json
            {"type":"assistant","message":"Valid response"}
            """;
        File.WriteAllText(path, content);

        // Act
        var result = ClaudeResponseExtractor.ExtractLastResponse(path);

        // Assert
        Assert.Equal("Valid response", result);
    }

    [Fact]
    public void ExtractAllResponses_MultipleResponses_ReturnsAll()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "all.jsonl");
        var content = """
            {"type":"user","message":"Q1"}
            {"type":"assistant","message":"A1"}
            {"type":"user","message":"Q2"}
            {"type":"assistant","message":"A2"}
            {"type":"user","message":"Q3"}
            {"type":"assistant","message":"A3"}
            """;
        File.WriteAllText(path, content);

        // Act
        var result = ClaudeResponseExtractor.ExtractAllResponses(path);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal("A1", result[0]);
        Assert.Equal("A2", result[1]);
        Assert.Equal("A3", result[2]);
    }

    [Fact]
    public void ExtractAllResponses_FileNotFound_ReturnsEmptyList()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "nonexistent.jsonl");

        // Act
        var result = ClaudeResponseExtractor.ExtractAllResponses(path);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractLastResponse_WhitespaceOnlyLines_SkipsAndContinues()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "whitespace.jsonl");
        var content = """
            {"type":"user","message":"Question"}

            {"type":"assistant","message":"Answer"}

            """;
        File.WriteAllText(path, content);

        // Act
        var result = ClaudeResponseExtractor.ExtractLastResponse(path);

        // Assert
        Assert.Equal("Answer", result);
    }

    [Fact]
    public void ExtractLastResponse_RealWorldFormat_ExtractsCorrectly()
    {
        // Arrange - simulates actual Claude CLI format
        var path = Path.Combine(_tempDir, "realworld.jsonl");
        var content = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"What files are in the docs folder?"}]},"isMeta":false}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Let me check the docs folder for you."},{"type":"tool_use","id":"tool1","name":"Bash","input":{"command":"ls docs/"}}]},"costUsd":0.01}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tool1","content":"README.md\nSETUP.md\n"}]},"isMeta":true}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"The docs folder contains:\n- README.md\n- SETUP.md\n\nWould you like me to show you the contents of any of these files?"}]},"costUsd":0.02}
            """;
        File.WriteAllText(path, content);

        // Act
        var result = ClaudeResponseExtractor.ExtractLastResponse(path);

        // Assert
        Assert.Contains("The docs folder contains:", result);
        Assert.Contains("README.md", result);
    }
}
