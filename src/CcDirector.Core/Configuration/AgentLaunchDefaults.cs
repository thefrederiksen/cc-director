using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Resolves the default effective command-line arguments a programmatically-created session should
/// launch with, so a session spawned via the Control API / CLI (<c>cc-devthrottle session spawn</c>)
/// inherits the SAME default agent settings - most importantly the permission mode - as a session
/// created in the desktop New Session dialog (issue #1017).
///
/// The desktop dialog builds a session's launch line in two layers (see
/// <c>MainWindow.ShowNewSessionDialog</c>):
///   1. the user's SELECTED <see cref="AgentEntry"/> resolved through
///      <see cref="AgentToolConfig.ResolveEffectiveCommandLineArguments"/> (preset + default model), then
///   2. the dialog's per-session "Bypass permissions" checkbox, which DEFAULTS TO ON, appending the
///      agent's permission-bypass flag (Claude <c>--dangerously-skip-permissions</c>, Cursor
///      <c>--force</c>, Copilot <c>--allow-all</c>) on top.
/// A programmatic caller that supplies no args used to get neither layer - it fell through to the
/// bare driver default and came up prompting for approval on everything, which made it unusable for
/// unattended work. This helper reproduces BOTH layers so the two paths launch identically.
/// </summary>
public static class AgentLaunchDefaults
{
    /// <summary>
    /// The default effective launch arguments for the given agent kind, matching what the desktop
    /// New Session dialog launches by default:
    ///   * the FIRST ENABLED entry of that kind in the configured agent library (the entry the
    ///     dialog pre-selects), resolved through the shared
    ///     <see cref="AgentToolConfig.ResolveEffectiveCommandLineArguments"/>; when no enabled entry
    ///     exists, any entry of that kind, then the persisted per-tool config
    ///     (<see cref="AgentToolConfig.Load"/>), which itself falls back to the catalog default preset;
    ///   * PLUS the agent's permission-bypass flag for the kinds that have one
    ///     (Claude / Cursor / Copilot), mirroring the dialog's Bypass-permissions checkbox default of
    ///     ON. The flag is appended only when the resolved preset does not already carry it, so an
    ///     entry configured with the "Automatic" preset is not doubled.
    /// Returns an empty string for a kind with no catalog plugin (e.g. <see cref="AgentKind.RawCli"/>),
    /// whose command line is supplied explicitly by the caller and has nothing to inherit.
    /// </summary>
    public static string ResolveDefaultArgs(AgentKind agentKind, AgentOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        // A kind with no catalog plugin (RawCli / Custom) has no presets, model, or permission flag
        // to inherit; its full command line is always supplied explicitly by the caller.
        if (!AgentPluginRegistry.Contains(agentKind))
        {
            FileLog.Write($"[AgentLaunchDefaults] ResolveDefaultArgs: {agentKind} has no catalog plugin; no defaults to apply");
            return "";
        }

        var baseArgs = ResolveEntryArgs(agentKind, options);

        // Layer 2: the dialog's Bypass-permissions checkbox defaults to ON, so a UI-created session
        // launches with the permission-bypass flag on top of the preset. Replicate that default so a
        // spawned session has the SAME permission profile and is usable without hand-fixing
        // permissions. Add only when absent so an "Automatic" preset that already carries the flag is
        // not doubled.
        var bypassArg = PermissionBypassArgFor(agentKind);
        if (bypassArg is not null && !baseArgs.Contains(bypassArg, StringComparison.Ordinal))
        {
            baseArgs = string.IsNullOrEmpty(baseArgs) ? bypassArg : $"{baseArgs} {bypassArg}";
            FileLog.Write($"[AgentLaunchDefaults] ResolveDefaultArgs: {agentKind} applied default permission bypass -> \"{baseArgs}\"");
        }

        return baseArgs;
    }

    /// <summary>
    /// Layer 1: the entry-preset-plus-model arguments for the kind, resolved exactly as the dialog
    /// resolves its selected entry. Never includes the per-session bypass flag.
    /// </summary>
    private static string ResolveEntryArgs(AgentKind agentKind, AgentOptions options)
    {
        var entries = AgentEntryStore.LoadEntries(options);
        var entry = entries.FirstOrDefault(e => e.Enabled && e.Type == agentKind)
                    ?? entries.FirstOrDefault(e => e.Type == agentKind);
        if (entry is not null)
        {
            var argsFromEntry = entry.ToToolConfig().ResolveEffectiveCommandLineArguments();
            FileLog.Write($"[AgentLaunchDefaults] ResolveEntryArgs: {agentKind} resolved from entry id={entry.Id}, preset={entry.PresetId}, args=\"{argsFromEntry}\"");
            return argsFromEntry;
        }

        var args = AgentToolConfig.Load(agentKind).ResolveEffectiveCommandLineArguments();
        FileLog.Write($"[AgentLaunchDefaults] ResolveEntryArgs: {agentKind} no configured entry; per-tool config args=\"{args}\"");
        return args;
    }

    /// <summary>
    /// The permission-bypass command-line flag for the kinds that have one, matching the desktop
    /// dialog's Bypass-permissions checkbox behavior (Claude / Cursor / Copilot). Null for kinds that
    /// have no such flag (Pi, Codex, Gemini, OpenCode, Grok), which never get a bypass default.
    /// </summary>
    private static string? PermissionBypassArgFor(AgentKind agentKind) => agentKind switch
    {
        AgentKind.ClaudeCode => AgentToolCatalog.ClaudeSkipPermissionsArg,
        AgentKind.Cursor => AgentToolCatalog.CursorForceArg,
        AgentKind.Copilot => AgentToolCatalog.CopilotAllowAllArg,
        _ => null,
    };
}
