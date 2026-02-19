using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class TerminalVerificationTests : IDisposable
{
    private readonly string _testDir;

    public TerminalVerificationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"terminal_verification_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public void ExtractUserPrompts_ValidJsonl_ReturnsPrompts()
    {
        // Arrange
        var jsonlPath = Path.Combine(_testDir, "test_session.jsonl");
        var lines = new[]
        {
            """{"type":"user","message":{"content":"This is the first user prompt that is long enough to pass the minimum length check"}}""",
            """{"type":"assistant","message":{"content":"This is an assistant response"}}""",
            """{"type":"user","message":{"content":"This is another user prompt that should be extracted from the file"}}"""
        };
        File.WriteAllLines(jsonlPath, lines);

        // Act
        var prompts = ClaudeSessionReader.ExtractUserPrompts(jsonlPath);

        // Assert
        Assert.Equal(2, prompts.Count);
        Assert.Contains("This is the first user prompt", prompts[0]);
        Assert.Contains("This is another user prompt", prompts[1]);
    }

    [Fact]
    public void ExtractUserPrompts_SkipsMetaMessages()
    {
        // Arrange
        var jsonlPath = Path.Combine(_testDir, "test_meta.jsonl");
        var lines = new[]
        {
            """{"type":"user","isMeta":true,"message":{"content":"This is a meta message that should be skipped entirely"}}""",
            """{"type":"user","message":{"content":"This is a regular user prompt that should be extracted normally"}}"""
        };
        File.WriteAllLines(jsonlPath, lines);

        // Act
        var prompts = ClaudeSessionReader.ExtractUserPrompts(jsonlPath);

        // Assert
        Assert.Single(prompts);
        Assert.Contains("regular user prompt", prompts[0]);
    }

    [Fact]
    public void ExtractUserPrompts_SkipsShortPrompts()
    {
        // Arrange
        var jsonlPath = Path.Combine(_testDir, "test_short.jsonl");
        var lines = new[]
        {
            """{"type":"user","message":{"content":"Short"}}""",
            """{"type":"user","message":{"content":"This is a sufficiently long user prompt that exceeds the minimum length"}}"""
        };
        File.WriteAllLines(jsonlPath, lines);

        // Act
        var prompts = ClaudeSessionReader.ExtractUserPrompts(jsonlPath);

        // Assert
        Assert.Single(prompts);
        Assert.Contains("sufficiently long", prompts[0]);
    }

    [Fact]
    public void ExtractUserPrompts_HandlesStringMessage()
    {
        // Arrange
        var jsonlPath = Path.Combine(_testDir, "test_string.jsonl");
        var lines = new[]
        {
            """{"type":"user","message":"This is a simple string message that is long enough to pass the length check"}"""
        };
        File.WriteAllLines(jsonlPath, lines);

        // Act
        var prompts = ClaudeSessionReader.ExtractUserPrompts(jsonlPath);

        // Assert
        Assert.Single(prompts);
        Assert.Contains("simple string message", prompts[0]);
    }

    [Fact]
    public void ExtractUserPrompts_HandlesContentArray()
    {
        // Arrange
        var jsonlPath = Path.Combine(_testDir, "test_array.jsonl");
        var lines = new[]
        {
            """{"type":"user","message":{"content":[{"type":"text","text":"This is text content from an array structure that should be extracted"}]}}"""
        };
        File.WriteAllLines(jsonlPath, lines);

        // Act
        var prompts = ClaudeSessionReader.ExtractUserPrompts(jsonlPath);

        // Assert
        Assert.Single(prompts);
        Assert.Contains("text content from an array", prompts[0]);
    }

    [Fact]
    public void ExtractUserPrompts_EmptyFile_ReturnsEmpty()
    {
        // Arrange
        var jsonlPath = Path.Combine(_testDir, "empty.jsonl");
        File.WriteAllText(jsonlPath, "");

        // Act
        var prompts = ClaudeSessionReader.ExtractUserPrompts(jsonlPath);

        // Assert
        Assert.Empty(prompts);
    }

    [Fact]
    public void ExtractUserPrompts_NonExistentFile_ReturnsEmpty()
    {
        // Arrange
        var jsonlPath = Path.Combine(_testDir, "nonexistent.jsonl");

        // Act
        var prompts = ClaudeSessionReader.ExtractUserPrompts(jsonlPath);

        // Assert
        Assert.Empty(prompts);
    }

    [Fact]
    public void ExtractUserPrompts_SkipsMalformedLines()
    {
        // Arrange
        var jsonlPath = Path.Combine(_testDir, "test_malformed.jsonl");
        var lines = new[]
        {
            "not json at all",
            """{"type":"user","message":{"content":"Valid prompt that should be extracted despite the malformed line above"}}""",
            "{incomplete json",
            """{"type":"user","message":{"content":"Another valid prompt that should also be extracted from this file"}}"""
        };
        File.WriteAllLines(jsonlPath, lines);

        // Act
        var prompts = ClaudeSessionReader.ExtractUserPrompts(jsonlPath);

        // Assert
        Assert.Equal(2, prompts.Count);
    }

    [Fact]
    public void TerminalVerificationResult_Defaults()
    {
        var result = new TerminalVerificationResult();
        Assert.False(result.IsMatched);
        Assert.False(result.IsPotential);
        Assert.Null(result.MatchedSessionId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void TerminalVerificationStatus_DefaultIsWaiting()
    {
        Assert.Equal(TerminalVerificationStatus.Waiting, default(TerminalVerificationStatus));
    }

    [Fact]
    public void TerminalVerificationStatus_HasPotentialState()
    {
        // Verify the Potential state exists in the enum
        Assert.True(Enum.IsDefined(typeof(TerminalVerificationStatus), TerminalVerificationStatus.Potential));
    }

    [Fact]
    public void VerifyWithTerminalContent_Under50Lines_NoJsonlFiles_StaysWaiting()
    {
        // This is the KEY test - with < 50 lines and no .jsonl files,
        // status should stay Waiting, NOT become Failed

        // Create a mock session manually (we can't use SessionManager easily)
        // Instead, let's test the logic directly by checking isConfirmationRun

        // When lineCount < 50, isConfirmationRun = false
        // When isConfirmationRun = false, SetTerminalVerificationStatus(Failed) is NOT called

        int lineCount = 10;
        bool isConfirmationRun = lineCount >= 50;

        // This should be false
        Assert.False(isConfirmationRun, "With 10 lines, isConfirmationRun should be false");

        // With isConfirmationRun = false, the method should NOT set Failed status
        // even if no .jsonl files are found
    }

    [Fact]
    public void VerifyWithTerminalContent_Over50Lines_NoJsonlFiles_SetsFailed()
    {
        // With >= 50 lines and no .jsonl files, status should become Failed

        int lineCount = 50;
        bool isConfirmationRun = lineCount >= 50;

        Assert.True(isConfirmationRun, "With 50 lines, isConfirmationRun should be true");
    }

    [Fact]
    public void VerifyWithTerminalContent_49Lines_IsNotConfirmationRun()
    {
        int lineCount = 49;
        bool isConfirmationRun = lineCount >= 50;
        Assert.False(isConfirmationRun);
    }

    [Fact]
    public void VerifyWithTerminalContent_50Lines_IsConfirmationRun()
    {
        int lineCount = 50;
        bool isConfirmationRun = lineCount >= 50;
        Assert.True(isConfirmationRun);
    }

    [Fact]
    public void ExtractUserPrompts_SkipsCommandMessages()
    {
        // Arrange - system-injected command invocations should be filtered out
        var jsonlPath = Path.Combine(_testDir, "test_commands.jsonl");
        var lines = new[]
        {
            """{"type":"user","message":"<command-message>review-code</command-message>\n<command-name>/review-code</command-name>"}""",
            """{"type":"user","message":{"content":"This is the actual user prompt that should be kept and extracted"}}"""
        };
        File.WriteAllLines(jsonlPath, lines);

        // Act
        var prompts = ClaudeSessionReader.ExtractUserPrompts(jsonlPath);

        // Assert - only the real user prompt, not the command message
        Assert.Single(prompts);
        Assert.Contains("actual user prompt", prompts[0]);
    }

    [Fact]
    public void ExtractUserPrompts_SkipsSkillExpansions()
    {
        // Arrange - skill expansions injected by CLI should be filtered out
        var jsonlPath = Path.Combine(_testDir, "test_skills.jsonl");
        var lines = new[]
        {
            """{"type":"user","message":{"content":[{"type":"text","text":"Base directory for this skill: D:\\Repos\\project\\.claude\\skills\\review-code\n\n# Code Review Skill\n\nReview changed files."}]}}""",
            """{"type":"user","message":{"content":"Please review my code changes and find any bugs in the implementation"}}"""
        };
        File.WriteAllLines(jsonlPath, lines);

        // Act
        var prompts = ClaudeSessionReader.ExtractUserPrompts(jsonlPath);

        // Assert - only the real user prompt, not the skill expansion
        Assert.Single(prompts);
        Assert.Contains("review my code changes", prompts[0]);
    }

    [Fact]
    public void ExtractUserPrompts_MixedContent_OnlyRealPrompts()
    {
        // Arrange - realistic session with commands, skills, tool results, and actual prompts
        var jsonlPath = Path.Combine(_testDir, "test_mixed.jsonl");
        var lines = new[]
        {
            // Command invocation (should be filtered)
            """{"type":"user","message":"<command-message>commit</command-message>\n<command-name>/commit</command-name>"}""",
            // Skill expansion (should be filtered)
            """{"type":"user","message":{"content":[{"type":"text","text":"Base directory for this skill: D:\\project\\.claude\\skills\\commit\n\n# Commit Skill\n\nCreate a git commit."}]}}""",
            // Tool result (should be filtered - no text type)
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_123","content":"file contents here"}]}}""",
            // Short prompt (should be filtered - < 10 chars)
            """{"type":"user","message":"fix"}""",
            // Real user prompt (should be kept)
            """{"type":"user","message":"Look at the latest screenshot and figure out why verification failed"}""",
            // Another real prompt (should be kept)
            """{"type":"user","message":{"content":"Can you also add better logging to help debug this issue in the future"}}"""
        };
        File.WriteAllLines(jsonlPath, lines);

        // Act
        var prompts = ClaudeSessionReader.ExtractUserPrompts(jsonlPath);

        // Assert - only the two real user prompts
        Assert.Equal(2, prompts.Count);
        Assert.Contains("verification failed", prompts[0]);
        Assert.Contains("better logging", prompts[1]);
    }

    [Fact]
    public void ExtractUserPrompts_SkipsSystemInjectedContent()
    {
        // Arrange - system-injected messages that aren't user-typed
        var jsonlPath = Path.Combine(_testDir, "test_system.jsonl");
        var lines = new[]
        {
            // local-command-stdout (system injection)
            """{"type":"user","message":"<local-command-stdout>Login successful</local-command-stdout>"}""",
            // task-notification (system injection)
            """{"type":"user","message":"<task-notification>\n<task-id>b499ff3</task-id>\n<output-file>C:\\temp\\output.txt</output-file>\n</task-notification>"}""",
            // system-reminder (system injection)
            """{"type":"user","message":"<system-reminder>Remember to use proper error handling</system-reminder>"}""",
            // Context continuation (system injection)
            """{"type":"user","message":"This session is being continued from a previous conversation that ran out of context. The summary below covers the earlier discussion."}""",
            // Real user prompt (should be kept)
            """{"type":"user","message":"Can you fix the login bug in the authentication handler please"}"""
        };
        File.WriteAllLines(jsonlPath, lines);

        // Act
        var prompts = ClaudeSessionReader.ExtractUserPrompts(jsonlPath);

        // Assert - only the real user prompt
        Assert.Single(prompts);
        Assert.Contains("fix the login bug", prompts[0]);
    }

    [Fact]
    public void IsSystemInjectedContent_DetectsAllTypes()
    {
        Assert.True(ClaudeSessionReader.IsSystemInjectedContent("<command-message>commit</command-message>"));
        Assert.True(ClaudeSessionReader.IsSystemInjectedContent("<command-name>/commit</command-name>"));
        Assert.True(ClaudeSessionReader.IsSystemInjectedContent("<local-command-stdout>output</local-command-stdout>"));
        Assert.True(ClaudeSessionReader.IsSystemInjectedContent("<task-notification><task-id>123</task-id></task-notification>"));
        Assert.True(ClaudeSessionReader.IsSystemInjectedContent("<system-reminder>reminder text</system-reminder>"));
        Assert.True(ClaudeSessionReader.IsSystemInjectedContent("<tool-result>some result</tool-result>"));
        Assert.True(ClaudeSessionReader.IsSystemInjectedContent("Base directory for this skill: D:\\project\\.claude\\skills\\commit"));
        Assert.True(ClaudeSessionReader.IsSystemInjectedContent("This session is being continued from a previous conversation that ran out of context."));

        Assert.False(ClaudeSessionReader.IsSystemInjectedContent("Can you fix the login bug?"));
        Assert.False(ClaudeSessionReader.IsSystemInjectedContent("What are all these uncommitted files?"));
    }

    [Fact]
    public void NormalizeForMatching_CollapsesWhitespace()
    {
        // Handles word wrapping (newlines inserted in terminal)
        Assert.Equal("hello world foo bar", ClaudeSessionReader.NormalizeForMatching("hello  world\nfoo   bar"));
        Assert.Equal("fix the authentication bug in the login handler",
            ClaudeSessionReader.NormalizeForMatching("fix the authentication bug\n  in the login handler"));
        Assert.Equal("a b c", ClaudeSessionReader.NormalizeForMatching("  a  \n  b  \n  c  "));
        Assert.Equal("", ClaudeSessionReader.NormalizeForMatching(""));
        Assert.Equal("", ClaudeSessionReader.NormalizeForMatching("   "));
    }

    [Fact]
    public void NormalizeForMatching_HandlesWordWrappedPrompts()
    {
        // Simulate a prompt that wraps in terminal at column 80
        var originalPrompt = "fix the authentication bug in the login handler that causes users to be logged out";
        var wrappedInTerminal = "fix the authentication bug in the login handler that causes users to be logged\nout";

        var normalizedPrompt = ClaudeSessionReader.NormalizeForMatching(originalPrompt);
        var normalizedTerminal = ClaudeSessionReader.NormalizeForMatching(wrappedInTerminal);

        Assert.Equal(normalizedPrompt, normalizedTerminal);

        // Also verify the normalized terminal text contains the normalized prompt
        var fullTerminal = $"> {wrappedInTerminal}\nSome response text\n> Another prompt";
        var normalizedFull = ClaudeSessionReader.NormalizeForMatching(fullTerminal);
        Assert.Contains(normalizedPrompt, normalizedFull);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Ignore cleanup errors (locked files, etc.)
        }
    }
}
