using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Tests for <see cref="AgentLaunchDefaults.ResolveDefaultArgs"/> (issue #1017): a session created
/// via the Control API / CLI with no explicit args must inherit the SAME default agent settings the
/// desktop New Session dialog applies - the selected entry's preset and default model PLUS the
/// dialog's Bypass-permissions default (ON) - so it is not born prompting for approval on
/// everything. Shares an isolated CC_DIRECTOR_ROOT (xUnit runs a class's methods sequentially) via
/// the CcStorageRoot collection, mirroring AgentEntryTests.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class AgentLaunchDefaultsTests : IDisposable
{
    private const string ClaudeSkip = "--dangerously-skip-permissions";
    private const string CopilotAllow = "--allow-all";

    private readonly string _root;
    private readonly string? _prevRoot;

    public AgentLaunchDefaultsTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-launchdefaults-test-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void ResolveDefaultArgs_ClaudeStandardEntry_AppliesBypassDefault()
    {
        // The user configured the Standard (permission-prompting) preset, but the dialog's
        // Bypass-permissions checkbox defaults to ON, so a UI-created session launches bypassed.
        // A spawned session with no args must match: Standard preset (no args) + the bypass flag.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": true, "executable_path": "C:/tools/claude.cmd",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.ClaudeCode, new AgentOptions());

        Assert.Equal(ClaudeSkip, args);
    }

    [Fact]
    public void ResolveDefaultArgs_ClaudeAutomaticEntry_DoesNotDoubleBypass()
    {
        // The "Automatic (skip permissions)" preset already carries the bypass flag; the default
        // must not append a second copy.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": true, "executable_path": "C:/tools/claude.cmd",
                "preset_id": "Automatic (skip permissions)", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.ClaudeCode, new AgentOptions());

        Assert.Equal(ClaudeSkip, args);
    }

    [Fact]
    public void ResolveDefaultArgs_ClaudeStandardWithModel_IncludesModelAndBypass()
    {
        // Mirrors the real desktop default: Standard preset + a default model + the bypass default,
        // so a spawned session runs on the chosen model (not the 200K bare default) AND is usable
        // without approval prompts (issue #803 / #1017).
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": true, "executable_path": "C:/tools/claude.cmd",
                "preset_id": "Standard", "default_model": "opus[1m]", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.ClaudeCode, new AgentOptions());

        Assert.Contains("--model opus[1m]", args);
        Assert.Contains(ClaudeSkip, args);
    }

    [Fact]
    public void ResolveDefaultArgs_MultipleClaudeEntries_PicksFirstEnabled()
    {
        // The New Session dialog pre-selects the first ENABLED entry of the kind; ResolveDefaultArgs
        // must match. The first entry is disabled and the first enabled one carries model "sonnet",
        // so the result reflects that entry (not the later "opus" one), plus the bypass default.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": false, "executable_path": "C:/tools/claude.cmd",
                "preset_id": "Standard", "default_model": "haiku", "launch_mode": "Guided" },
              { "type": "ClaudeCode", "enabled": true, "executable_path": "C:/tools/claude.cmd",
                "preset_id": "Standard", "default_model": "sonnet", "launch_mode": "Guided" },
              { "type": "ClaudeCode", "enabled": true, "executable_path": "C:/tools/claude.cmd",
                "preset_id": "Standard", "default_model": "opus", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.ClaudeCode, new AgentOptions());

        Assert.Contains("--model sonnet", args);
        Assert.DoesNotContain("opus", args);
        Assert.DoesNotContain("haiku", args);
        Assert.Contains(ClaudeSkip, args);
    }

    [Fact]
    public void ResolveDefaultArgs_OnlyDisabledEntry_StillResolvesThatEntry()
    {
        // When no entry of the kind is enabled, fall back to any entry of that kind rather than
        // dropping to the bare driver default - the user did configure this agent.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": false, "executable_path": "C:/tools/claude.cmd",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.ClaudeCode, new AgentOptions());

        Assert.Equal(ClaudeSkip, args);
    }

    [Fact]
    public void ResolveDefaultArgs_CopilotStandardEntry_AppliesAllowAll()
    {
        // Copilot's permission-bypass equivalent is --allow-all; the dialog default applies it, so a
        // spawned Copilot session must inherit it too.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "Copilot", "enabled": true, "executable_path": "C:/tools/copilot.cmd",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.Copilot, new AgentOptions());

        Assert.Equal(CopilotAllow, args);
    }

    [Fact]
    public void ResolveDefaultArgs_CodexEntry_NoBypassFlag()
    {
        // Codex has no permission-bypass flag, so the default is just its preset (Standard = empty).
        // This confirms the bypass default is applied only to kinds that actually have such a flag.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "Codex", "enabled": true, "executable_path": "C:/tools/codex.cmd",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.Codex, new AgentOptions());

        Assert.Equal("", args);
    }

    [Fact]
    public void ResolveDefaultArgs_NoEntryForKind_FallsBackToCatalogDefaultPreset()
    {
        // Codex is not in the configured library, so there is no entry to read. The per-tool config
        // load then supplies the catalog default preset (Codex's default is Standard = no args), and
        // Codex has no bypass flag, so the result is empty.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": true, "executable_path": "C:/tools/claude.cmd",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.Codex, new AgentOptions());

        Assert.Equal("", args);
    }

    [Fact]
    public void ResolveDefaultArgs_RawCli_YieldsEmpty()
    {
        // RawCli has no catalog plugin: its command line is supplied explicitly by the caller, so
        // there is nothing to inherit and no config is read.
        SeedConfig("{}");

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.RawCli, new AgentOptions());

        Assert.Equal("", args);
    }

    [Fact]
    public void ResolveDefaultArgs_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.ClaudeCode, null!));
    }
}
