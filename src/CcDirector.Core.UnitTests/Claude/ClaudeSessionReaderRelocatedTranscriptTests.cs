using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// A Claude Code transcript follows the agent's WORKING DIRECTORY, not the folder the session started in.
/// When the agent enters a Claude Code worktree, Claude Code moves the transcript into that worktree's own
/// project folder, and every Director reader that rebuilt the path from the repository folder went on
/// answering "transcript not found" for the rest of the session (session 111, 1 September 2026: hours of
/// <c>no_jsonl</c> from a Director looking one folder away from the live file). The lookup is now by the
/// transcript's identity - its GUID file name - across the projects root.
/// </summary>
public sealed class ClaudeSessionReaderRelocatedTranscriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csr-reloc-" + Guid.NewGuid().ToString("N"));

    public ClaudeSessionReaderRelocatedTranscriptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Folder(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Transcript(string folder, string id)
    {
        var path = Path.Combine(folder, id + ".jsonl");
        File.WriteAllText(path, "{}\n");
        return path;
    }

    [Fact]
    public void InTheStartFolder_IsFoundThere_WithoutLookingAnywhereElse()
    {
        var id = Guid.NewGuid().ToString();
        var start = Folder("D--Repos-app");
        var expected = Transcript(start, id);
        // A decoy elsewhere with the same name must not win over the start folder.
        Transcript(Folder("D--Repos-app--claude-worktrees-x"), id);

        Assert.Equal(expected, ClaudeSessionReader.LocateJsonl(_root, id, "D--Repos-app"));
    }

    [Fact]
    public void MovedIntoAWorktreeFolder_IsFoundThere()
    {
        // The session 111 shape: started in the repository, entered a worktree, and the transcript now lives
        // ONLY under the worktree's project folder. The start folder exists and is empty of it.
        var id = Guid.NewGuid().ToString();
        Folder("D--ReposMindzie-mindzieWeb");
        var moved = Transcript(Folder("D--ReposMindzie-mindzieWeb--claude-worktrees-ns-8922-load-upload"), id);

        Assert.Equal(moved, ClaudeSessionReader.LocateJsonl(_root, id, "D--ReposMindzie-mindzieWeb"));
    }

    [Fact]
    public void MovedAgain_IsFollowedAgain_NotPinnedToTheFirstRelocation()
    {
        // The remembered relocation is a shortcut, not a fact: if the file is no longer where it was last
        // seen, the scan runs again and finds its new home.
        var id = Guid.NewGuid().ToString();
        Folder("D--Repos-app");
        var first = Transcript(Folder("D--Repos-app--claude-worktrees-one"), id);
        Assert.Equal(first, ClaudeSessionReader.LocateJsonl(_root, id, "D--Repos-app"));

        File.Delete(first);
        var second = Transcript(Folder("D--Repos-app--claude-worktrees-two"), id);

        Assert.Equal(second, ClaudeSessionReader.LocateJsonl(_root, id, "D--Repos-app"));
    }

    [Fact]
    public void Nowhere_AnswersTheStartFolderPath_SoNotFoundMessagesNameTheExpectedPlace()
    {
        var id = Guid.NewGuid().ToString();
        Folder("D--Repos-app");
        Folder("D--Repos-other");

        var answer = ClaudeSessionReader.LocateJsonl(_root, id, "D--Repos-app");

        Assert.Equal(Path.Combine(_root, "D--Repos-app", id + ".jsonl"), answer);
        Assert.False(File.Exists(answer));
    }

    [Fact]
    public void AfterAMiss_TheStartFolderIsStillCheckedEveryTime_SoANewTranscriptIsSeenAtOnce()
    {
        // The ordinary new-session case: the first read happens before Claude Code has written the file. The
        // miss is remembered so the root is not re-scanned every read - but the START folder is checked on
        // every call regardless, so the transcript is seen the moment it appears where it belongs.
        var id = Guid.NewGuid().ToString();
        var start = Folder("D--Repos-app");
        Folder("D--Repos-other");
        Assert.False(File.Exists(ClaudeSessionReader.LocateJsonl(_root, id, "D--Repos-app")));

        var written = Transcript(start, id);

        Assert.Equal(written, ClaudeSessionReader.LocateJsonl(_root, id, "D--Repos-app"));
    }

    [Fact]
    public void AMissingProjectsRoot_AnswersTheStartFolderPath_AndDoesNotThrow()
    {
        var answer = ClaudeSessionReader.LocateJsonl(Path.Combine(_root, "does-not-exist"), Guid.NewGuid().ToString(), "D--Repos-app");
        Assert.False(File.Exists(answer));
    }

    [Fact]
    public void AnEmptySessionId_DoesNotScan()
    {
        // With no name to look for there is nothing to find; the caller gets the (nonsense) start path back
        // exactly as before and reports it, rather than this helper matching some unrelated file.
        Transcript(Folder("D--Repos-app--claude-worktrees-x"), "");
        var answer = ClaudeSessionReader.LocateJsonl(_root, "", "D--Repos-app");
        Assert.Equal(Path.Combine(_root, "D--Repos-app", ".jsonl"), answer);
    }
}
