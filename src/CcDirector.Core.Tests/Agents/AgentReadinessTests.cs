using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Agents;

/// <summary>
/// The two surfaces that tell a user whether they have a coding agent - the status board's readiness
/// row and the first-run wizard's receipt - must give ONE answer (devthrottle_internal issue #1047:
/// the wizard said "1 agent ready" and the board said "No coding agent found", on the same machine,
/// seconds apart). These tests pin the shared answer.
///
/// The detector is injected. With the real one, a developer machine with Claude Code installed would
/// make every case pass by accident - the "present" assertion would be true for the wrong reason and
/// the "absent" assertion could never fail. Here the machine's real agents are invisible, so what is
/// being tested is the rule and not the host.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class AgentReadinessTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    /// <summary>A machine where detection resolves nothing at all: no PATH hit, no known install location.</summary>
    private static readonly AgentReadiness.InstalledToolProbe NothingInstalled = (_, _) => null;

    public AgentReadinessTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-agentpresence-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void SeedConfig(string json)
    {
        var path = CcStorage.ConfigJson();
        var dir = Path.GetDirectoryName(path);
        Assert.NotNull(dir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }

    /// <summary>A real, existing executable inside the test's own root - never something the host provides.</summary>
    private string WriteFakeAgentExecutable(string name)
    {
        var dir = Path.Combine(_root, "fake-agent");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "@echo off");
        return path;
    }

    [Fact]
    public void Scan_AgentInstalledOffPathButRecordedAsAnEntry_ReportsPresent()
    {
        // The clean-machine case the issue was found on: the wizard runs the official installer, which
        // drops the binary somewhere this process's PATH does not cover, and records its ABSOLUTE path
        // on the agent entry. The user can launch it, so every surface must say they have an agent.
        var exe = WriteFakeAgentExecutable("claude.cmd");
        SeedConfig($$"""
        {
          "agent": {
            "entries": [
              {
                "id": "e1",
                "display_name": "Claude Code",
                "type": "ClaudeCode",
                "enabled": true,
                "executable_path": {{System.Text.Json.JsonSerializer.Serialize(exe)}}
              }
            ]
          }
        }
        """);

        var facts = AgentReadiness.Scan(new AgentOptions(), NothingInstalled);

        var claude = facts.Single(f => f.Tool == AgentKind.ClaudeCode);
        Assert.True(claude.Present);
        Assert.Equal(AgentReadinessSource.ConfiguredEntry, claude.Source);
        Assert.Equal(exe, claude.ResolvedPath, ignoreCase: true);
        Assert.Contains(facts, f => f.Present);
    }

    [Fact]
    public void Scan_EntryPointingAtAnExecutableThatIsNotThere_ReportsAbsent()
    {
        // The other direction of the same untruth. An entry left behind after the agent was uninstalled
        // is not an agent, and calling it one would put a green row in front of a user who cannot start
        // anything. The entry's path is RESOLVED, never taken on trust.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              {
                "id": "e1",
                "display_name": "Claude Code",
                "type": "ClaudeCode",
                "enabled": true,
                "executable_path": "C:/nowhere/claude-that-was-uninstalled.cmd"
              }
            ]
          }
        }
        """);

        var facts = AgentReadiness.Scan(new AgentOptions(), NothingInstalled);

        Assert.All(facts, f => Assert.False(f.Present));
        Assert.All(facts, f => Assert.Equal(AgentReadinessSource.NotFound, f.Source));
    }

    [Fact]
    public void Scan_DisabledEntry_ReportsAbsent()
    {
        // A disabled entry is one the user has said they do not want launched. It must not prop up the
        // readiness row.
        var exe = WriteFakeAgentExecutable("codex.cmd");
        SeedConfig($$"""
        {
          "agent": {
            "entries": [
              {
                "id": "e1",
                "display_name": "Codex",
                "type": "Codex",
                "enabled": false,
                "executable_path": {{System.Text.Json.JsonSerializer.Serialize(exe)}}
              }
            ]
          }
        }
        """);

        var facts = AgentReadiness.Scan(new AgentOptions(), NothingInstalled);

        Assert.False(facts.Single(f => f.Tool == AgentKind.Codex).Present);
    }

    [Fact]
    public void Scan_NoEntriesAndNothingInstalled_ReportsAbsentForEveryTool()
    {
        // The genuinely empty machine still has to report empty - the added entry rule must not turn
        // every scan green.
        SeedConfig("""{ "agent": { "entries": [] } }""");

        var facts = AgentReadiness.Scan(new AgentOptions(), NothingInstalled);

        Assert.NotEmpty(facts);
        Assert.All(facts, f => Assert.False(f.Present));
    }

    [Fact]
    public void Scan_DetectedTool_PrefersDetectionAndReportsItsPath()
    {
        // Detection still wins when it resolves: the resolved path is what the surfaces show.
        var exe = WriteFakeAgentExecutable("detected-claude.cmd");
        SeedConfig("""{ "agent": { "entries": [] } }""");

        var facts = AgentReadiness.Scan(
            new AgentOptions(),
            (tool, _) => tool == AgentKind.ClaudeCode ? exe : null);

        var claude = facts.Single(f => f.Tool == AgentKind.ClaudeCode);
        Assert.True(claude.Present);
        Assert.Equal(AgentReadinessSource.Detected, claude.Source);
        Assert.Equal(exe, claude.ResolvedPath);
    }

    [Fact]
    public void SaveEntries_RaisesEntriesChanged()
    {
        // The wire that stops the board holding a startup answer: every writer goes through SaveEntries,
        // so one subscription there catches the wizard, the Settings tab and the Control API alike.
        SeedConfig("""{ }""");

        var raised = 0;
        void Handler() => raised++;

        AgentEntryStore.EntriesChanged += Handler;
        try
        {
            AgentEntryStore.SaveEntries(new List<AgentEntry>
            {
                new() { DisplayName = "Claude Code", Type = AgentKind.ClaudeCode, ExecutablePath = "claude" },
            });
        }
        finally
        {
            AgentEntryStore.EntriesChanged -= Handler;
        }

        Assert.Equal(1, raised);
    }
}
