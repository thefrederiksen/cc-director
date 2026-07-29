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
///   2. the dialog's per-session "Run without approval prompts" checkbox, which DEFAULTS TO ON,
///      appending the agent's unattended-permission flag
///      (<see cref="AgentToolCatalog.UnattendedPermissionArg"/>) on top.
/// A programmatic caller that supplies no args used to get neither layer - it fell through to the
/// bare driver default and came up prompting for approval on everything, which made it unusable for
/// unattended work. This helper reproduces BOTH layers so the two paths launch identically.
///
/// Layer 2 used to cover Claude, Cursor and Copilot ONLY. For every other agent it did nothing at
/// all, silently: a spawn asking for bypassPermissions=true got a Codex launched with an empty
/// command line, sandboxed and prompting on every tool call, while the log said the default had been
/// applied. It now covers every agent that HAS such a flag, and says plainly when one does not.
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
    ///   * PLUS the agent's unattended-permission flag for every kind that HAS one
    ///     (<see cref="AgentToolCatalog.UnattendedPermissionArg"/>), mirroring the dialog's
    ///     run-without-approval checkbox default of ON. It is appended only when the resolved line
    ///     carries no permission flag already, so a deliberately configured line is never doubled or
    ///     overridden, and never for Pi / OpenCode, which have no such flag.
    /// Returns an empty string for a kind with no catalog plugin (e.g. <see cref="AgentKind.RawCli"/>),
    /// whose command line is supplied explicitly by the caller and has nothing to inherit.
    ///
    /// <paramref name="bypassPermissions"/> mirrors the dialog's checkbox: <c>true</c> (the default,
    /// matching the checkbox's default-ON) applies the unattended flag as described above;
    /// <c>false</c> resolves the SAME entry preset and model but WITHOUT it, exactly as the desktop
    /// dialog launches when the user clears the checkbox (issue #1497).
    /// </summary>
    public static string ResolveDefaultArgs(AgentKind agentKind, AgentOptions options, bool bypassPermissions = true)
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

        // Layer 2: the dialog's run-without-approval checkbox defaults to ON, so a UI-created session
        // launches with the unattended-permission flag on top of the preset. Replicate that default so
        // a spawned session has the SAME permission profile and is usable without hand-fixing
        // permissions. When the caller cleared the checkbox (bypassPermissions=false), skip this layer
        // entirely so the session keeps the configured model but stops for each permission prompt.
        if (!bypassPermissions)
        {
            FileLog.Write($"[AgentLaunchDefaults] ResolveDefaultArgs: {agentKind} unattended permissions declined; preset/model only -> \"{baseArgs}\"");
            return baseArgs;
        }

        var unattendedArg = AgentToolCatalog.UnattendedPermissionArg(agentKind);
        if (unattendedArg is null)
        {
            // Pi and OpenCode have no full-permission flag at all. Say so plainly instead of logging
            // that a bypass was applied when nothing was - the silent version of this is exactly the
            // bug that shipped: create logged "applied default agent settings ... bypassPermissions=True"
            // for a Codex session that launched with an empty command line and prompted for everything.
            FileLog.Write($"[AgentLaunchDefaults] ResolveDefaultArgs: {agentKind} has NO unattended-permission flag; launching as configured -> \"{baseArgs}\"");
            return baseArgs;
        }

        // Do not bolt a second permission flag onto a line that already settles the question - an
        // entry deliberately set to "Bypass permissions", or a hand-written override carrying
        // --sandbox / --ask-for-approval, keeps exactly what it asked for.
        var alreadyDecided = AgentToolCatalog.KnownPermissionArgs(agentKind)
            .Any(arg => baseArgs.Contains(arg, StringComparison.OrdinalIgnoreCase));
        if (alreadyDecided)
        {
            FileLog.Write($"[AgentLaunchDefaults] ResolveDefaultArgs: {agentKind} already carries a permission flag; left as configured -> \"{baseArgs}\"");
            return baseArgs;
        }

        baseArgs = string.IsNullOrEmpty(baseArgs) ? unattendedArg : $"{baseArgs} {unattendedArg}";
        FileLog.Write($"[AgentLaunchDefaults] ResolveDefaultArgs: {agentKind} applied unattended permissions ({unattendedArg}) -> \"{baseArgs}\"");
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
}
