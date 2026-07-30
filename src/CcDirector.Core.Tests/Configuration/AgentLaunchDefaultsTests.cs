using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Tests for <see cref="AgentLaunchDefaults.ResolveDefaultArgs"/> (issue #1017): a session created
/// via the Control API / CLI with no explicit args must inherit the SAME default agent settings the
/// desktop New Session dialog applies - the selected entry's preset and default model PLUS the
/// dialog's run-without-approval default (ON) - so it is not born prompting for approval on
/// everything. Shares an isolated CC_DIRECTOR_ROOT (xUnit runs a class's methods sequentially) via
/// the CcStorageRoot collection, mirroring AgentEntryTests.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class AgentLaunchDefaultsTests : IDisposable
{
    private const string ClaudeSkip = "--dangerously-skip-permissions";
    private const string ClaudeAuto = "--permission-mode auto";
    private const string CopilotAllow = "--allow-all";
    private const string CodexFullAccess = "--dangerously-bypass-approvals-and-sandbox";

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
        // run-without-approval checkbox defaults to ON, so a UI-created session launches unattended.
        // A spawned session with no args must match: Standard preset (no args) + the automatic mode.
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

        Assert.Equal(ClaudeAuto, args);
    }

    [Fact]
    public void ResolveDefaultArgs_ClaudeLegacyAutomaticEntry_KeepsSkipPermissions()
    {
        // "Automatic (skip permissions)" is the OLD label for the skip-permissions preset. Nothing
        // is migrated: the entry keeps the flag it always had, and nothing is appended on top.
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
        Assert.DoesNotContain(ClaudeAuto, args);
    }

    [Fact]
    public void ResolveDefaultArgs_ClaudeSkipPermissionsEntry_KeepsIt_AndDoesNotAddAutomatic()
    {
        // A deliberate "Skip permissions" choice is preserved exactly - the unattended default must
        // not bolt --permission-mode auto onto a line that already settles the question.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": true, "executable_path": "C:/tools/claude.cmd",
                "preset_id": "Skip permissions", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.ClaudeCode, new AgentOptions());

        Assert.Equal(ClaudeSkip, args);
        Assert.DoesNotContain(ClaudeAuto, args);
    }

    [Fact]
    public void ResolveDefaultArgs_ClaudeStandardWithModel_IncludesModelAndUnattended()
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
        Assert.Contains(ClaudeAuto, args);
    }

    [Fact]
    public void ResolveDefaultArgs_BypassPermissionsFalse_KeepsModelDropsBypass()
    {
        // Issue #1497: when the caller clears the Bypass-permissions checkbox, the session keeps the
        // agent's configured model but is launched WITHOUT the permission-bypass flag, so it stops for
        // each permission prompt - exactly as the desktop dialog launches with the checkbox cleared.
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

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.ClaudeCode, new AgentOptions(), bypassPermissions: false);

        Assert.Contains("--model opus[1m]", args);
        Assert.DoesNotContain(ClaudeSkip, args);
        Assert.DoesNotContain(ClaudeAuto, args);
    }

    [Fact]
    public void ResolveDefaultArgs_CodexBypassPermissionsFalse_LaunchesWithoutFullAccess()
    {
        // The refusal direction of the same switch: declining unattended permissions must actually
        // withhold the flag, or the parameter is decorative in the other direction.
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

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.Codex, new AgentOptions(), bypassPermissions: false);

        Assert.Equal("", args);
    }

    [Fact]
    public void ResolveDefaultArgs_GeminiStandardEntry_AppliesYolo()
    {
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "Gemini", "enabled": true, "executable_path": "C:/tools/gemini.cmd",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.Gemini, new AgentOptions());

        Assert.Equal("--yolo", args);
    }

    [Fact]
    public void ResolveDefaultArgs_GrokStandardEntry_AppliesAlwaysApprove()
    {
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "Grok", "enabled": true, "executable_path": "C:/tools/grok.exe",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.Grok, new AgentOptions());

        Assert.Equal("--always-approve", args);
    }

    [Fact]
    public void ResolveDefaultArgs_PiEntry_StaysEmpty_BecausePiHasNoSuchFlag()
    {
        // Pi's --approve only trusts project-local files; it is not an approval bypass. Inventing a
        // flag here would launch Pi with an argument it does not understand.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "Pi", "enabled": true, "executable_path": "C:/tools/pi.exe",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.Pi, new AgentOptions());

        Assert.Equal("", args);
    }

    [Fact]
    public void ResolveDefaultArgs_CustomLaunchModeOverride_IsNeverAmended()
    {
        // A hand-written command line is the user's own; the unattended default must not append to
        // it even when it carries no permission flag we recognize.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "Codex", "enabled": true, "executable_path": "C:/tools/codex.cmd",
                "preset_id": "Standard", "args_override": "--sandbox read-only",
                "launch_mode": "Custom" }
            ]
          }
        }
        """);

        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.Codex, new AgentOptions());

        Assert.Equal("--sandbox read-only", args);
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
        Assert.Contains(ClaudeAuto, args);
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

        Assert.Equal(ClaudeAuto, args);
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
    public void ResolveDefaultArgs_CodexStandardEntry_AppliesFullAccess()
    {
        // THE REGRESSION TEST. This previously asserted "" and was titled NoBypassFlag, on the false
        // premise that Codex has no permission flag - so a Codex session spawned by an agent launched
        // sandboxed and prompted on every tool call while the log claimed defaults had been applied.
        // An entry left on Standard must still come up able to work unattended.
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

        Assert.Equal(CodexFullAccess, args);
    }

    [Fact]
    public void ResolveDefaultArgs_NoEntryForKind_FallsBackToCatalogDefaultPreset()
    {
        // Codex is not in the configured library, so there is no entry to read. The per-tool config
        // load then supplies the catalog default preset, which is now Full access.
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

        Assert.Equal(CodexFullAccess, args);
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

    // ===== The executable, from the SAME entry as the arguments (issue #1050) =====
    //
    // The clean-machine install failure: the onboarding wizard installs Claude Code, records the
    // binary's absolute path on the agent entry, and reports it ready - while the launch path took its
    // arguments from that entry and its EXECUTABLE from AgentOptions, whose bare "claude" default is
    // what nothing writes any more. These pin the property that closes it: for a caller that knows
    // only the KIND, the executable is the one recorded on the entry.

    [Fact]
    public void CreateAgentForKind_EntryHasPath_LaunchesTheEntrysExecutable()
    {
        // The exact clean-install shape: the machine-level ClaudePath is the untouched bare default,
        // and the only place that knows where the wizard put the binary is the entry.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": true,
                "executable_path": "C:/Users/qa/.local/bin/claude.exe",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var agent = AgentLaunchDefaults.CreateAgentForKind(AgentKind.ClaudeCode, new AgentOptions());

        Assert.Equal("C:/Users/qa/.local/bin/claude.exe", agent.ExecutablePath);
        Assert.Equal(AgentKind.ClaudeCode, agent.Kind);
    }

    [Fact]
    public void CreateAgentForKind_EntryHasPath_DoesNotUseTheBareDefault()
    {
        // Stated separately because the bare default is the failure: "claude" is what CreateProcess
        // was handed, and it is what must NOT come out of here when an entry knows better.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": true,
                "executable_path": "C:/Users/qa/.local/bin/claude.exe",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var agent = AgentLaunchDefaults.CreateAgentForKind(AgentKind.ClaudeCode, new AgentOptions());

        Assert.NotEqual("claude", agent.ExecutablePath);
    }

    [Fact]
    public void CreateAgentForKind_ArgsAndExecutableComeFromTheSameEntry()
    {
        // The defect class in one assertion: two halves that must not be able to come from two
        // sources. The entry's preset decides the arguments AND the entry's path decides the binary.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": true,
                "executable_path": "C:/Users/qa/.local/bin/claude.exe",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);
        var options = new AgentOptions();

        var agent = AgentLaunchDefaults.CreateAgentForKind(AgentKind.ClaudeCode, options);
        var args = AgentLaunchDefaults.ResolveDefaultArgs(AgentKind.ClaudeCode, options);

        Assert.Equal("C:/Users/qa/.local/bin/claude.exe", agent.ExecutablePath);
        Assert.Equal(ClaudeAuto, args);
    }

    [Fact]
    public void CreateAgentForKind_BlankEntryPath_UsesThePerTypeDefault()
    {
        // An entry with no recorded path knows nothing, so the per-type path in AgentOptions is the
        // only candidate. That is the documented default for a machine with no agent library, and it
        // is not silent: an unresolvable command is refused by name at launch, never handed to
        // CreateProcess to fail with a bare error.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": true, "executable_path": "",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var agent = AgentLaunchDefaults.CreateAgentForKind(
            AgentKind.ClaudeCode, new AgentOptions { ClaudePath = "C:/tools/claude.exe" });

        Assert.Equal("C:/tools/claude.exe", agent.ExecutablePath);
    }

    [Fact]
    public void CreateAgentForKind_PrefersTheEnabledEntry()
    {
        // Same entry the desktop New Session dialog pre-selects: the first ENABLED one of that kind.
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": false, "executable_path": "C:/old/claude.exe",
                "preset_id": "Standard", "launch_mode": "Guided" },
              { "type": "ClaudeCode", "enabled": true, "executable_path": "C:/current/claude.exe",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        var agent = AgentLaunchDefaults.CreateAgentForKind(AgentKind.ClaudeCode, new AgentOptions());

        Assert.Equal("C:/current/claude.exe", agent.ExecutablePath);
    }

    [Fact]
    public void ResolveEntryExecutablePath_NoEntryForKind_ReturnsNull()
    {
        SeedConfig("""
        {
          "agent": {
            "entries": [
              { "type": "ClaudeCode", "enabled": true, "executable_path": "C:/tools/claude.exe",
                "preset_id": "Standard", "launch_mode": "Guided" }
            ]
          }
        }
        """);

        Assert.Null(AgentLaunchDefaults.ResolveEntryExecutablePath(AgentKind.Codex, new AgentOptions()));
    }

    [Fact]
    public void CreateAgentForKind_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => AgentLaunchDefaults.CreateAgentForKind(AgentKind.ClaudeCode, null!));
    }
}
