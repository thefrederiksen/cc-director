using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for SimpleChatSummarizer prompt-building methods.
/// Validates that completion prompts extract content faithfully
/// and exclude tool-use metadata noise.
/// </summary>
public class SimpleChatSummarizerTests
{
    private static TurnData MakeTurn(
        string prompt,
        List<string>? tools = null,
        List<string>? files = null,
        List<string>? commands = null)
    {
        return new TurnData(
            prompt,
            tools ?? new List<string>(),
            files ?? new List<string>(),
            commands ?? new List<string>(),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public void BuildCompletionPrompt_IncludesUserPrompt()
    {
        // Arrange
        var turn = MakeTurn("What is in this directory?");

        // Act
        var prompt = SimpleChatSummarizer.BuildCompletionPrompt(turn, "some terminal output");

        // Assert
        Assert.Contains("The user asked: What is in this directory?", prompt);
    }

    [Fact]
    public void BuildCompletionPrompt_IncludesTerminalOutput()
    {
        // Arrange
        var turn = MakeTurn("List files");
        var terminal = "file1.cs\nfile2.cs\nfile3.cs";

        // Act
        var prompt = SimpleChatSummarizer.BuildCompletionPrompt(turn, terminal);

        // Assert
        Assert.Contains("Terminal output:", prompt);
        Assert.Contains("file1.cs", prompt);
        Assert.Contains("file3.cs", prompt);
    }

    [Fact]
    public void BuildCompletionPrompt_DoesNotIncludeToolsUsed()
    {
        // Arrange
        var turn = MakeTurn("Do something", tools: new List<string> { "Read", "Glob", "Bash" });

        // Act
        var prompt = SimpleChatSummarizer.BuildCompletionPrompt(turn, "output");

        // Assert
        Assert.DoesNotContain("Tools used:", prompt);
        Assert.DoesNotContain("Read, Glob, Bash", prompt);
    }

    [Fact]
    public void BuildCompletionPrompt_DoesNotIncludeFilesTouched()
    {
        // Arrange
        var turn = MakeTurn("Edit files",
            files: new List<string> { @"C:\src\file1.cs", @"C:\src\file2.cs" });

        // Act
        var prompt = SimpleChatSummarizer.BuildCompletionPrompt(turn, "output");

        // Assert
        Assert.DoesNotContain("Files touched:", prompt);
        Assert.DoesNotContain("file1.cs", prompt);
    }

    [Fact]
    public void BuildCompletionPrompt_DoesNotIncludeBashCommands()
    {
        // Arrange
        var turn = MakeTurn("Run tests",
            commands: new List<string> { "dotnet test", "git status" });

        // Act
        var prompt = SimpleChatSummarizer.BuildCompletionPrompt(turn, "output");

        // Assert
        Assert.DoesNotContain("Commands run:", prompt);
        Assert.DoesNotContain("dotnet test", prompt);
    }

    [Fact]
    public void BuildCompletionPrompt_IncludesExtractionInstruction()
    {
        // Arrange
        var turn = MakeTurn("Show me the projects");

        // Act
        var prompt = SimpleChatSummarizer.BuildCompletionPrompt(turn, "output");

        // Assert
        Assert.Contains("Extract Claude's actual response", prompt);
        Assert.Contains("Remove all tool-use noise", prompt);
        Assert.Contains("Keep lists, tables, and explanations intact", prompt);
    }

    [Fact]
    public void BuildCompletionPrompt_EmptyTerminal_OmitsTerminalSection()
    {
        // Arrange
        var turn = MakeTurn("Hello");

        // Act
        var prompt = SimpleChatSummarizer.BuildCompletionPrompt(turn, "");

        // Assert
        Assert.DoesNotContain("Terminal output:", prompt);
    }

    [Fact]
    public void BuildCompletionPrompt_WhitespaceTerminal_OmitsTerminalSection()
    {
        // Arrange
        var turn = MakeTurn("Hello");

        // Act
        var prompt = SimpleChatSummarizer.BuildCompletionPrompt(turn, "   \n  \t  ");

        // Assert
        Assert.DoesNotContain("Terminal output:", prompt);
    }

    [Fact]
    public void BuildCompletionPrompt_LongTerminal_TruncatesToMaxChars()
    {
        // Arrange
        var turn = MakeTurn("Do something");
        var terminal = new string('x', 10000);

        // Act
        var prompt = SimpleChatSummarizer.BuildCompletionPrompt(turn, terminal);

        // Assert -- terminal portion should be truncated to 4000 chars (MaxTerminalChars)
        // The prompt includes header lines + terminal, so total > 4000 but terminal portion <= 4000
        Assert.Contains("Terminal output:", prompt);
        var terminalStart = prompt.IndexOf("Terminal output:");
        var terminalContent = prompt[(terminalStart + "Terminal output:".Length)..].Trim();
        Assert.True(terminalContent.Length <= 4000,
            $"Terminal content should be at most 4000 chars, was {terminalContent.Length}");
    }

    [Fact]
    public void BuildCompletionPrompt_LongUserPrompt_TruncatesTo500Chars()
    {
        // Arrange
        var longPrompt = new string('a', 1000);
        var turn = MakeTurn(longPrompt);

        // Act
        var prompt = SimpleChatSummarizer.BuildCompletionPrompt(turn, "output");

        // Assert -- user prompt should be truncated with "..."
        Assert.Contains("...", prompt);
        // Should not contain the full 1000-char string
        Assert.DoesNotContain(longPrompt, prompt);
    }

    [Fact]
    public void BuildProgressPrompt_IncludesTerminalText()
    {
        // Act
        var prompt = SimpleChatSummarizer.BuildProgressPrompt("Reading file src/main.cs...");

        // Assert
        Assert.Contains("Reading file src/main.cs...", prompt);
        Assert.Contains("What is Claude doing right now?", prompt);
    }

    [Fact]
    public void BuildCompletionPrompt_DoesNotContainOldSummarizeWording()
    {
        // Arrange
        var turn = MakeTurn("List files");

        // Act
        var prompt = SimpleChatSummarizer.BuildCompletionPrompt(turn, "output");

        // Assert -- old wording should be gone
        Assert.DoesNotContain("Summarize what was accomplished", prompt);
        Assert.DoesNotContain("A Claude Code turn just finished", prompt);
    }
}
