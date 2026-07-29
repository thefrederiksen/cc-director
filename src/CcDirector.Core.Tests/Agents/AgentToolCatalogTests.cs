using Xunit;
using CcDirector.Core.Agents;

namespace CcDirector.Core.Tests.Agents;

public class AgentToolCatalogTests
{
    [Fact]
    public void Entries_ContainsAllKnownTools()
    {
        var kinds = AgentToolCatalog.Entries.Select(e => e.Tool).ToHashSet();

        Assert.Equal(8, AgentToolCatalog.Entries.Count);
        Assert.Contains(AgentKind.ClaudeCode, kinds);
        Assert.Contains(AgentKind.Pi, kinds);
        Assert.Contains(AgentKind.Codex, kinds);
        Assert.Contains(AgentKind.Gemini, kinds);
        Assert.Contains(AgentKind.OpenCode, kinds);
        Assert.Contains(AgentKind.Cursor, kinds);
        Assert.Contains(AgentKind.Grok, kinds);
        Assert.Contains(AgentKind.Copilot, kinds);
    }

    [Fact]
    public void CopilotEntry_DefaultPresetRunsUnattended_WithAllowAll()
    {
        // Issue #625 flag, now the DEFAULT: Copilot launches with --allow-all so it does not stop
        // for approval. Verified from copilot --help ("Enable all permissions").
        var copilot = AgentToolCatalog.GetEntry(AgentKind.Copilot);

        Assert.Equal("GitHub Copilot", copilot.DisplayName);
        Assert.Equal(AgentToolCatalog.CopilotAutomaticPresetName, copilot.DefaultPreset.Name);
        Assert.Equal(AgentToolCatalog.CopilotAllowAllArg, copilot.DefaultPreset.Arguments);
        Assert.Equal("--allow-all", AgentToolCatalog.CopilotAllowAllArg);
    }

    [Fact]
    public void CursorEntry_DefaultPresetRunsUnattended_WithForce()
    {
        var cursor = AgentToolCatalog.GetEntry(AgentKind.Cursor);

        Assert.Equal(AgentToolCatalog.CursorAutomaticPresetName, cursor.DefaultPreset.Name);
        Assert.Equal(AgentToolCatalog.CursorForceArg, cursor.DefaultPreset.Arguments);
        Assert.Equal("--force", AgentToolCatalog.CursorForceArg);
    }

    [Fact]
    public void CodexEntry_DefaultPresetIsFullAccess()
    {
        // The bug this whole change came from: Codex defaulted to Standard, so every spawned Codex
        // session launched sandboxed and prompting, and no caller could ask for anything else.
        var codex = AgentToolCatalog.GetEntry(AgentKind.Codex);

        Assert.Equal(AgentToolCatalog.CodexFullAccessPresetName, codex.DefaultPreset.Name);
        Assert.Equal(AgentToolCatalog.CodexFullAccessArg, codex.DefaultPreset.Arguments);
        Assert.Equal("--dangerously-bypass-approvals-and-sandbox", AgentToolCatalog.CodexFullAccessArg);
    }

    [Fact]
    public void GeminiEntry_DefaultPresetRunsUnattended_WithYolo()
    {
        // Verified from gemini --help: "-y, --yolo  Automatically accept all actions".
        var gemini = AgentToolCatalog.GetEntry(AgentKind.Gemini);

        Assert.Equal(AgentToolCatalog.GeminiAutomaticPresetName, gemini.DefaultPreset.Name);
        Assert.Equal(AgentToolCatalog.GeminiYoloArg, gemini.DefaultPreset.Arguments);
        Assert.Equal("--yolo", AgentToolCatalog.GeminiYoloArg);
    }

    [Fact]
    public void GrokEntry_DefaultPresetRunsUnattended_WithAlwaysApprove()
    {
        // Verified from grok --help: "--always-approve  Auto-approve all tool executions".
        var grok = AgentToolCatalog.GetEntry(AgentKind.Grok);

        Assert.Equal(AgentToolCatalog.GrokAutomaticPresetName, grok.DefaultPreset.Name);
        Assert.Equal(AgentToolCatalog.GrokAlwaysApproveArg, grok.DefaultPreset.Arguments);
        Assert.Equal("--always-approve", AgentToolCatalog.GrokAlwaysApproveArg);
    }

    [Fact]
    public void ClaudeEntry_DefaultPresetIsAutomaticMode_NotSkipPermissions()
    {
        // Claude's default is the automatic permission mode. Skip permissions is NOT the default
        // because Anthropic does not recommend it, so neither do we.
        // Verified from claude --help: --permission-mode accepts "auto".
        var claude = AgentToolCatalog.GetEntry(AgentKind.ClaudeCode);

        Assert.Equal(AgentToolCatalog.ClaudeAutomaticPresetName, claude.DefaultPreset.Name);
        Assert.Equal(AgentToolCatalog.ClaudeAutomaticModeArg, claude.DefaultPreset.Arguments);
        Assert.Equal("--permission-mode auto", AgentToolCatalog.ClaudeAutomaticModeArg);
        Assert.DoesNotContain(AgentToolCatalog.ClaudeSkipPermissionsArg, claude.DefaultPreset.Arguments);
    }

    [Fact]
    public void ClaudeEntry_StillOffersSkipPermissionsAndStandard_NeitherAsDefault()
    {
        var claude = AgentToolCatalog.GetEntry(AgentKind.ClaudeCode);

        var skip = claude.Presets.FirstOrDefault(p => p.Name == AgentToolCatalog.ClaudeSkipPermissionsPresetName);
        Assert.NotNull(skip);
        Assert.Equal(AgentToolCatalog.ClaudeSkipPermissionsArg, skip.Arguments);

        var standard = claude.Presets.FirstOrDefault(p => p.Name == AgentToolCatalog.StandardPresetName);
        Assert.NotNull(standard);
        Assert.Equal("", standard.Arguments);

        Assert.NotEqual(claude.DefaultPreset.Name, skip.Name);
        Assert.NotEqual(claude.DefaultPreset.Name, standard.Name);
    }

    [Fact]
    public void GetEntry_EveryEntryHasAtLeastOnePreset()
    {
        foreach (var entry in AgentToolCatalog.Entries)
            Assert.NotEmpty(entry.Presets);
    }

    [Fact]
    public void EveryAgentWithAnUnattendedFlag_DefaultsToAPresetCarryingIt()
    {
        // The whole point: an agent that CAN run without approval prompts does so by default. This
        // is the assertion that would have caught the Codex default, and catches the next agent
        // added with a permission flag but a Standard default.
        foreach (var entry in AgentToolCatalog.Entries)
        {
            var unattended = AgentToolCatalog.UnattendedPermissionArg(entry.Tool);
            if (unattended is null)
                continue;

            Assert.Contains(unattended, entry.DefaultPreset.Arguments);
        }
    }

    [Fact]
    public void AgentsWithNoUnattendedFlag_DefaultToStandard()
    {
        // Pi and OpenCode expose no full-permission flag on their command lines, so there is nothing
        // to default them to and they must not pretend otherwise.
        Assert.Null(AgentToolCatalog.UnattendedPermissionArg(AgentKind.Pi));
        Assert.Null(AgentToolCatalog.UnattendedPermissionArg(AgentKind.OpenCode));

        Assert.Equal(AgentToolCatalog.StandardPresetName, AgentToolCatalog.GetEntry(AgentKind.Pi).DefaultPreset.Name);
        Assert.Equal(AgentToolCatalog.StandardPresetName, AgentToolCatalog.GetEntry(AgentKind.OpenCode).DefaultPreset.Name);
    }

    [Fact]
    public void UnattendedPermissionArg_IsAlwaysListedAmongKnownPermissionArgs()
    {
        // Otherwise the "already decided" check could not see the flag it just applied, and a second
        // conflicting permission flag would get appended on the next pass.
        foreach (var entry in AgentToolCatalog.Entries)
        {
            var unattended = AgentToolCatalog.UnattendedPermissionArg(entry.Tool);
            if (unattended is null)
                continue;

            Assert.Contains(
                AgentToolCatalog.KnownPermissionArgs(entry.Tool),
                known => unattended.Contains(known, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void CanonicalPresetName_LegacyClaudeAutomatic_IsTheSkipPermissionsPreset()
    {
        // A RENAME, not a migration: the old label always meant --dangerously-skip-permissions and
        // still does, so an existing entry keeps launching exactly as it did.
        Assert.Equal(
            AgentToolCatalog.ClaudeSkipPermissionsPresetName,
            AgentToolCatalog.CanonicalPresetName(AgentKind.ClaudeCode, AgentToolCatalog.LegacyClaudeAutomaticPresetName));
    }

    [Fact]
    public void CanonicalPresetName_UnknownName_IsReturnedUnchanged()
    {
        Assert.Equal("Nonsense", AgentToolCatalog.CanonicalPresetName(AgentKind.ClaudeCode, "Nonsense"));
        Assert.Equal("Standard", AgentToolCatalog.CanonicalPresetName(AgentKind.Codex, "Standard"));
    }

    [Fact]
    public void Contains_RawCli_ReturnsFalse()
    {
        Assert.False(AgentToolCatalog.Contains(AgentKind.RawCli));
    }
}
