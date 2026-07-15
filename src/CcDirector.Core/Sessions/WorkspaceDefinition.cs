using CcDirector.Core.Agents;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// A single session entry within a workspace definition.
/// Workspaces always start fresh sessions (no ClaudeSessionId).
/// </summary>
public class WorkspaceSessionEntry
{
    public string RepoPath { get; set; } = string.Empty;
    public string? CustomName { get; set; }
    public string? CustomColor { get; set; }
    public int SortOrder { get; set; }
    public string? ClaudeArgs { get; set; }

    /// <summary>
    /// Which agent CLI this session ran (the <see cref="Agents.AgentKind"/> name, e.g. "Codex").
    /// Restored so a saved Codex session comes back as Codex rather than as the CreateSession default
    /// (issue #1635) - without this the restored session reports the wrong agent on its snapshot and its
    /// turns are miscounted against Claude Code on the Agents tab.
    ///
    /// Null means the workspace was saved before this field existed. That is not a guess: every session in
    /// such a file was created through the old default, so those genuinely were ClaudeCode.
    /// </summary>
    public string? Agent { get; set; }

    /// <summary>
    /// Which agent this entry should be restored as (issue #1635).
    ///
    /// Null/empty means the workspace predates <see cref="Agent"/>, and ClaudeCode is what those sessions
    /// actually were - see the field's note.
    ///
    /// A non-empty value that does not parse is a real problem (a hand-edited file, or a workspace written
    /// by a newer build that knows an agent this one does not). It is logged with the offending value
    /// rather than passed over in silence: restoring a session as the wrong agent is the very defect this
    /// method exists to fix, so the log is what makes it findable.
    /// </summary>
    public AgentKind ResolveAgentKind()
    {
        var raw = (Agent ?? "").Trim();
        if (raw.Length == 0) return AgentKind.ClaudeCode;

        if (Enum.TryParse<AgentKind>(raw, ignoreCase: true, out var kind))
            return kind;

        FileLog.Write($"[WorkspaceSessionEntry] ResolveAgentKind: unknown agent '{raw}' for {RepoPath} - " +
                      $"restoring as {AgentKind.ClaudeCode}; the session will run the wrong agent CLI");
        return AgentKind.ClaudeCode;
    }
}

/// <summary>
/// A named collection of sessions that can be saved and loaded.
/// Stored as individual JSON files in the workspaces directory.
/// </summary>
public class WorkspaceDefinition
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<WorkspaceSessionEntry> Sessions { get; set; } = new();
}
