using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>How this machine knows about an agent CLI.</summary>
public enum AgentReadinessSource
{
    /// <summary>Nothing found: no executable resolved and no usable configured entry.</summary>
    NotFound,

    /// <summary>Resolved by <see cref="ToolDetectionService"/> - on PATH, the configured path, or a known install location.</summary>
    Detected,

    /// <summary>Not resolvable by detection, but the user has an enabled agent entry whose executable exists.</summary>
    ConfiguredEntry,
}

/// <summary>
/// One agent CLI's presence on this machine: whether it is usable, where it resolved, how it was
/// found, and the last version it reported.
/// </summary>
public sealed record AgentReadinessFact(
    AgentKind Tool,
    string DisplayName,
    bool Present,
    string ResolvedPath,
    string? Version,
    AgentReadinessSource Source);

/// <summary>
/// THE one answer to "does this machine have a usable coding agent". Every surface that shows the
/// user that fact - the status board's readiness row and the first-run wizard - reads it from here,
/// so they cannot give different answers to the same question (devthrottle_internal issue #1047,
/// where the wizard's receipt said "1 agent ready" and the board said "No coding agent found").
///
/// An agent counts as present when EITHER of two things is true, because both mean the user can
/// launch it:
/// <list type="bullet">
/// <item>detection resolves an executable - on PATH, at the configured path, or at a known install
/// location (this is what the wizard's scan reports); or</item>
/// <item>the user has an ENABLED entry in <c>agent.entries</c> whose executable actually exists.
/// The wizard records an absolute path there after installing an agent, and the New Session picker
/// launches from it, so an entry that resolves IS a working agent even when nothing puts it on this
/// process's PATH - which is exactly the clean-machine case (the official Claude installer drops the
/// binary in <c>~/.local\bin</c> and the running Director's PATH predates it).</item>
/// </list>
/// The entry's path is resolved, never trusted: an entry left pointing at a deleted executable is not
/// an agent, and reporting it as one would be the same untruth in the other direction.
/// </summary>
public static class AgentReadiness
{
    /// <summary>
    /// Resolves a tool to an executable path, or null when it is not installed. Injected so tests can
    /// scan a known machine state instead of whatever agent CLIs the test machine happens to have -
    /// with the real detector, a developer box with Claude Code installed passes every case by accident.
    /// </summary>
    internal delegate string? InstalledToolProbe(AgentKind tool, AgentOptions options);

    /// <summary>Production probe: ask <see cref="ToolDetectionService"/>.</summary>
    private static string? DefaultDetect(AgentKind tool, AgentOptions options)
    {
        var result = new ToolDetectionService().DetectTool(tool, options);
        return result.Found ? result.ResolvedPath : null;
    }

    /// <summary>
    /// Scan every built-in agent CLI and report its presence on this machine. One fact per tool,
    /// in registry order, so a caller can both count what is usable and name it. Does file and PATH
    /// probing - callers run it off the interface thread.
    /// </summary>
    public static IReadOnlyList<AgentReadinessFact> Scan(AgentOptions options)
        => Scan(options, DefaultDetect);

    internal static IReadOnlyList<AgentReadinessFact> Scan(AgentOptions options, InstalledToolProbe detect)
    {
        FileLog.Write("[AgentReadiness] Scan");
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (detect is null) throw new ArgumentNullException(nameof(detect));

        var entries = AgentEntryStore.ReadCurrentEntries();
        var facts = new List<AgentReadinessFact>();

        foreach (var plugin in AgentPluginRegistry.BuiltIns)
        {
            var validation = ToolDetectionService.ReadValidationStatus(plugin.Kind, options);
            var version = validation?.Ok == true ? validation.Version : null;

            var detected = detect(plugin.Kind, options);
            if (!string.IsNullOrWhiteSpace(detected))
            {
                facts.Add(new AgentReadinessFact(
                    plugin.Kind, plugin.DisplayName, true, detected!, version, AgentReadinessSource.Detected));
                continue;
            }

            var configured = ResolveConfiguredEntry(entries, plugin.Kind);
            if (configured is not null)
            {
                FileLog.Write($"[AgentReadiness] Scan: tool={plugin.Kind} present via configured entry at {configured}");
                facts.Add(new AgentReadinessFact(
                    plugin.Kind, plugin.DisplayName, true, configured, version, AgentReadinessSource.ConfiguredEntry));
                continue;
            }

            facts.Add(new AgentReadinessFact(
                plugin.Kind, plugin.DisplayName, false, "", null, AgentReadinessSource.NotFound));
        }

        FileLog.Write($"[AgentReadiness] Scan: present={facts.Count(f => f.Present)} of {facts.Count}");
        return facts;
    }

    /// <summary>
    /// The first enabled entry of this type whose executable actually exists on disk, or null.
    /// A disabled entry is deliberately excluded - the user has said they do not want to launch it.
    /// </summary>
    private static string? ResolveConfiguredEntry(IReadOnlyList<AgentEntry> entries, AgentKind tool)
    {
        foreach (var entry in entries)
        {
            if (entry.Type != tool || !entry.Enabled || string.IsNullOrWhiteSpace(entry.ExecutablePath))
                continue;

            var resolved = ExecutableResolver.Resolve(entry.ExecutablePath);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }
}
