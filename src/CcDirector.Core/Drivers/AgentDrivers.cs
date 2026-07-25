using System.Collections.Concurrent;
using CcDirector.Core.Agents;

namespace CcDirector.Core.Drivers;

/// <summary>
/// The driver registry: one shared driver instance per CLI kind. Drivers are
/// stateless behavior bundles, so singletons are safe. Kinds without a written-and-
/// live-verified driver get a <see cref="GenericDriver"/> (today's exact keystrokes,
/// minimal declared capabilities) - consumers read <see cref="IAgentDriver.Capabilities"/>
/// to know what is actually available for a given session.
/// </summary>
public static class AgentDrivers
{
    private static readonly ConcurrentDictionary<AgentKind, IAgentDriver> Cache = new();

    public static IAgentDriver For(AgentKind kind) => Cache.GetOrAdd(kind, k => k switch
    {
        AgentKind.ClaudeCode => new ClaudeDriver(),
        AgentKind.Pi => new PiDriver(),
        AgentKind.Cursor => new CursorDriver(),
        AgentKind.Copilot => new CopilotDriver(),
        AgentKind.Codex => new CodexDriver(),
        // Gemini wires NO current-model reader: the installed binary (0.1.11) records no model
        // anywhere readable (issue #1637) - re-probe after it is upgraded.
        // The compaction command each tool documents in its OWN catalog (issue #2150) - gemini alone
        // spells it /compress, which is exactly why the command belongs to the driver and not to one
        // shared string at the call site.
        AgentKind.Gemini => new GenericDriver(k, GeminiSlashCommands.All, compactCommand: "/compress"),
        AgentKind.OpenCode => new GenericDriver(k, OpenCodeSlashCommands.All,
            currentModelReader: OpenCode.OpenCodeCurrentModel.ReadForRepo, compactCommand: "/compact"),
        AgentKind.Grok => new GenericDriver(k, GrokSlashCommands.All, emitsContinuousIdleOutput: true,
            currentModelReader: Grok.GrokCurrentModel.ReadForRepo, compactCommand: "/compact"),
        _ => new GenericDriver(k),
    });
}
