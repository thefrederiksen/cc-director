using CcDirector.Core.Gemini;
using CcDirector.Core.History;
using Xunit;

namespace CcDirector.Core.Tests.Gemini;

/// <summary>
/// Tests for <see cref="GeminiPromptLogReader"/> (issue #1551) - reading Gemini's own logs.json so the
/// durable record gets real prompts with real timestamps, instead of the untimestamped terminal
/// scrollback blob the History tab uses.
/// </summary>
public sealed class GeminiPromptLogReaderTests
{
    /// <summary>
    /// The directory scheme, pinned: Gemini keys its per-project temp dir by the lowercase hex SHA-256
    /// of the project path. This expected value was taken from a live ~/.gemini/tmp on Windows - if
    /// Gemini ever changes the scheme, this test is the thing that notices.
    /// </summary>
    [Fact]
    public void HashRepoPath_matches_the_scheme_gemini_actually_uses()
    {
        var hash = GeminiPromptLogReader.HashRepoPath(@"D:\ReposFred\devthrottle");

        Assert.StartsWith("dd33f065e1e15649", hash);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    [Fact]
    public void ResolvePath_returns_null_for_a_repo_gemini_has_never_run_in()
    {
        Assert.Null(GeminiPromptLogReader.ResolvePath(@"D:\definitely\not\a\real\repo\" + Guid.NewGuid()));
    }

    [Fact]
    public void ResolvePath_of_a_blank_repo_is_null_rather_than_throwing()
    {
        Assert.Null(GeminiPromptLogReader.ResolvePath(""));
        Assert.Null(GeminiPromptLogReader.ResolvePath("   "));
    }

    [Fact]
    public void Read_of_a_repo_with_no_gemini_log_is_empty_rather_than_throwing()
    {
        var history = GeminiPromptLogReader.Read(@"D:\definitely\not\a\real\repo\" + Guid.NewGuid());

        Assert.Same(ConversationHistory.Empty, history);
    }

    /// <summary>
    /// The real payoff: a live logs.json on this machine parses into real user prompts carrying real
    /// timestamps - which is what makes the origin join possible for Gemini at all. Skipped when the
    /// machine has never run Gemini, so this cannot fail on a clean box.
    /// </summary>
    [Fact]
    public void Read_of_a_real_on_disk_log_yields_timestamped_user_prompts()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tmp = Path.Combine(home, ".gemini", "tmp");
        if (!Directory.Exists(tmp)) return; // Gemini never ran here - nothing to verify against.

        var log = Directory.GetFiles(tmp, "logs.json", SearchOption.AllDirectories)
            .FirstOrDefault(f => new FileInfo(f).Length > 100);
        if (log is null) return;

        // Drive the reader through its real resolver by naming the repo whose hash owns this dir.
        var owningDir = Path.GetFileName(Path.GetDirectoryName(log)!);
        var history = ReadByDirectoryHash(owningDir, log);

        Assert.NotEmpty(history.Messages);
        Assert.All(history.Messages, m => Assert.Equal(ConversationRole.User, m.Role));
        Assert.All(history.Messages, m => Assert.NotEmpty(m.Parts));
        // The whole reason this source beats the scrollback: real timestamps.
        Assert.Contains(history.Messages, m => m.Timestamp.HasValue);
    }

    /// <summary>
    /// Drive the real Read() over a known logs.json by recreating the exact layout it resolves -
    /// ~/.gemini/tmp/&lt;hash of repo&gt;/logs.json - under a home directory we control. This exercises
    /// the resolver AND the parser, which is the part that breaks when Gemini changes its format.
    /// </summary>
    private static ConversationHistory ReadByDirectoryHash(string _, string logPath)
    {
        var temp = Path.Combine(Path.GetTempPath(), "gemini-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var repo = Path.Combine(temp, "repo");
            var geminiDir = Path.Combine(temp, ".gemini", "tmp", GeminiPromptLogReader.HashRepoPath(repo));
            Directory.CreateDirectory(geminiDir);
            File.Copy(logPath, Path.Combine(geminiDir, "logs.json"));

            return GeminiPromptLogReader.Read(repo, homeDirectory: temp);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }
}
