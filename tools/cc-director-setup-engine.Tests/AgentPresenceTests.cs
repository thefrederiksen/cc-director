using System;
using System.IO;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The Complete screen's one claim about the machine: is there a coding agent on it? It drives both
/// "You're ready to go" and the amber "one thing left" state, so a false answer is a false statement
/// on the last screen the user reads.
/// </summary>
public sealed class AgentPresenceTests
{
    // Every agent the Director drives has a plugin; every plugin's command must be probed, or a user
    // who came for that agent is told their board has nothing to run.
    // Revert-proof: drop "claude" (as the list did before this change - it held the non-Claude seven
    // only) and the first case goes red.
    [Fact]
    public void AgentCommands_CoverEveryAgentTheDirectorDrives()
    {
        Assert.Contains("claude", AgentPresence.AgentCommands);
        Assert.Contains("codex", AgentPresence.AgentCommands);
        Assert.Contains("gemini", AgentPresence.AgentCommands);
        Assert.Contains("copilot", AgentPresence.AgentCommands);
        Assert.Contains("cursor-agent", AgentPresence.AgentCommands);
        Assert.Contains("grok", AgentPresence.AgentCommands);
        Assert.Contains("opencode", AgentPresence.AgentCommands);
        Assert.Contains("pi", AgentPresence.AgentCommands);
        Assert.Equal(8, AgentPresence.AgentCommands.Count);
    }

    [Fact]
    public void AnyAgent_FindsASingleAgent_AndReportsNoneWhenThereIsNone()
    {
        Assert.True(AgentPresence.AnyAgent(exe => exe == "codex"));
        Assert.False(AgentPresence.AnyAgent(_ => false));
    }

    // An agent file in a directory is found; an empty directory answers no. Deterministic: the
    // directories are handed in, so this cannot pass because the machine running the test happens to
    // have Claude installed. (It nearly did - the first version of this test set USERPROFILE to a
    // temporary home and would have passed anyway from the real C:\Users\<user>\.local\bin\claude.exe.)
    [Fact]
    public void AnyAgentIn_FindsAnAgentFile_AndSaysNoWhenTheDirectoryIsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentpresence-" + Guid.NewGuid().ToString("N"));
        var withAgent = Path.Combine(root, "bin");
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(withAgent);
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(withAgent, OperatingSystem.IsWindows() ? "claude.exe" : "claude"), "");

        try
        {
            Assert.True(AgentPresence.AnyAgentIn([withAgent]));
            Assert.False(AgentPresence.AnyAgentIn([empty]));
            // Not on the first directory searched - the probe must keep looking.
            Assert.True(AgentPresence.AnyAgentIn([empty, withAgent]));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup only */ }
        }
    }

    // PATH is not enough and answering from PATH alone made this notice lie. The official Claude
    // installer targets ~/.local/bin, and on the Mac where this was found that directory was not on
    // the wizard's PATH: the Complete screen said no coding agent was set up while the Director found
    // Claude immediately.
    // Revert-proof: remove either extra directory from SearchDirectories and this goes red.
    [Fact]
    public void SearchDirectories_WithAnEmptyPath_StillLooksWhereTheDirectorLooks()
    {
        // An EMPTY PATH, so the two extra directories are the only possible answers. Reading the real
        // PATH would have passed for the wrong reason: this developer's PATH already contains
        // ~/.local/bin, which is how claude is found here in the first place.
        var dirs = AgentPresence.SearchDirectories(pathVariable: "");

        Assert.Equal(2, dirs.Count);
        Assert.Contains(dirs, d => d.EndsWith(Path.Combine(".local", "bin"), StringComparison.Ordinal));
        Assert.Contains(dirs, d => d.Contains("npm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SearchDirectories_KeepsPathEntriesToo()
    {
        var dirs = AgentPresence.SearchDirectories($"/one{Path.PathSeparator}/two");

        Assert.Contains("/one", dirs);
        Assert.Contains("/two", dirs);
        Assert.Equal(4, dirs.Count);   // the two given, plus npm-global and ~/.local/bin
    }

    // A malformed PATH entry must not take the notice down with it.
    [Fact]
    public void AnyAgentIn_SurvivesAnUnusableDirectoryEntry()
    {
        Assert.False(AgentPresence.AnyAgentIn(["", "   ", "|not:a<valid>path"]));
    }
}
