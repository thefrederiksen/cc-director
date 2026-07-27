using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// A TRAILING SEPARATOR ON THE REPO PATH MUST NOT CHANGE THE TRANSCRIPT FOLDER NAME.
///
/// The bug these pin, found live on 2026-07-27: a session whose repo path was stored as
/// "D:\ReposFred\cc-consult\" (a trailing backslash - equivalent to a human, and invisible in every
/// UI) resolved its Claude transcript folder to "D--ReposFred-cc-consult-". Every non-alphanumeric
/// character becomes a dash here, and Path.GetFullPath PRESERVES a trailing separator, so the
/// separator became a TRAILING DASH and named a folder that does not exist.
///
/// What that cost, in order: the transcript lookup missed -> the Director's "turns" read returned
/// no_jsonl with an EMPTY widget list -> the Gateway's voice service found no assistant reply and
/// recorded "nothing to narrate" about a session that had just written a full answer -> it never
/// called the speech provider -> no audio ever existed -> and because the roster holds a voice
/// session yellow until audio is ready, the session sat on "Preparing voice" permanently, with no
/// error surfaced anywhere.
///
/// So this is not a cosmetic path-hygiene test. The folder name is the join between a session and
/// everything that reads its conversation, and it silently returned the wrong answer.
/// </summary>
public class ClaudeSessionReaderTrailingSeparatorTests
{
    [Theory]
    [InlineData(@"D:\ReposFred\cc-consult", @"D:\ReposFred\cc-consult\")]
    [InlineData(@"D:\ReposFred\cc-consult", @"D:\ReposFred\cc-consult/")]
    [InlineData(@"C:\Users\soren\.claude", @"C:\Users\soren\.claude\")]
    public void GetProjectFolder_TrailingSeparator_MatchesTheSamePathWithout(string clean, string withSeparator)
    {
        Assert.Equal(
            ClaudeSessionReader.GetProjectFolder(clean),
            ClaudeSessionReader.GetProjectFolder(withSeparator));
    }

    [Fact]
    public void GetProjectFolder_TrailingSeparator_ProducesNoTrailingDash()
    {
        // The exact shape of the live defect: the name must not pick up a trailing dash.
        var folder = ClaudeSessionReader.GetProjectFolder(@"D:\ReposFred\cc-consult\");
        Assert.Equal("D--ReposFred-cc-consult", folder);
        Assert.False(folder.EndsWith('-'), $"folder name gained a trailing dash: {folder}");
    }

    [Fact]
    public void GetJsonlPath_TrailingSeparator_ResolvesToTheSameFile()
    {
        // The consumer that actually broke: SessionReadExecutor.Turns calls GetJsonlPath and treats a
        // missing file as "no_jsonl" + empty widgets, which downstream reads as "nothing to say".
        const string claudeSessionId = "adc79f6f-5230-46e5-af77-ed0aa6ca4ee7";
        Assert.Equal(
            ClaudeSessionReader.GetJsonlPath(claudeSessionId, @"D:\ReposFred\cc-consult"),
            ClaudeSessionReader.GetJsonlPath(claudeSessionId, @"D:\ReposFred\cc-consult\"));
    }

    [Fact]
    public void GetProjectFolder_DriveRoot_KeepsItsSeparator()
    {
        // The reason this trims with TrimEndingDirectorySeparator rather than a bare TrimEnd: a drive
        // ROOT must keep its separator. "D:\" is a real directory whose sanitized name is "D--";
        // "D:" means "the current directory on drive D:", which is a different place entirely. A bare
        // TrimEnd would silently retarget the root to wherever the process happens to be.
        Assert.Equal("D--", ClaudeSessionReader.GetProjectFolder(@"D:\"));
    }

    [Fact]
    public void GetProjectFolder_StillDashesEveryNonAlphanumeric()
    {
        // The behaviour that must NOT regress: dots, underscores, colons and separators all become
        // dashes (the issue #184 fix - a char-list version once missed dots and hid every transcript
        // under a dotted path).
        Assert.Equal("D--Repos-my-project", ClaudeSessionReader.GetProjectFolder(@"D:\Repos\my_project"));
        Assert.Equal("D--Repos--temp-brain-sandbox", ClaudeSessionReader.GetProjectFolder(@"D:\Repos\.temp\brain-sandbox"));
    }
}
