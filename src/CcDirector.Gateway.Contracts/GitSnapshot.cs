using System.Collections.Generic;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Phase 6: a quick snapshot of the git state of a session's repo.
/// Produced by the Wingman (or a direct `git` invocation) after each turn.
/// Surfaced in the Agent View and feeds the status colour when "idle + dirty".
/// </summary>
public sealed class GitSnapshot
{
    /// <summary>Current branch.  Empty if not a git repo or git unavailable.</summary>
    public string Branch { get; set; } = "";

    /// <summary>True when there are uncommitted changes (working tree or index).</summary>
    public bool Dirty { get; set; }

    /// <summary>Number of commits ahead of upstream.  0 when unknown.</summary>
    public int Ahead { get; set; }

    /// <summary>Number of commits behind upstream.  0 when unknown.</summary>
    public int Behind { get; set; }

    /// <summary>The last commit's short SHA + subject  (e.g. "a1b2c3d feat: thing").  Empty if unknown.</summary>
    public string LastCommit { get; set; } = "";

    /// <summary>"ok" | "not_a_repo" | "git_failed".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Free-text error detail when Status != "ok".</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Issue #1266 (additive): the files staged for the next commit (the index side of git status).
    /// Populated only by the read endpoint that serves the Cockpit's Source Control tab; the older
    /// Wingman consumer never sets it, so this stays empty on that path and the summary fields above
    /// are unchanged. Empty when there are no staged changes.
    /// </summary>
    public List<GitChangeEntry> StagedChanges { get; set; } = new();

    /// <summary>
    /// Issue #1266 (additive): the files changed in the working tree but not staged (the worktree side
    /// of git status), including untracked files. See <see cref="StagedChanges"/>.
    /// </summary>
    public List<GitChangeEntry> UnstagedChanges { get; set; } = new();
}

/// <summary>
/// One changed file in a session's repository (issue #1266): its repository-relative path and the
/// one-letter git change kind. This is the read-only unit the Cockpit's Source Control tab lists and
/// clicks to insert the path into the composer - it carries no staging or write capability.
/// </summary>
public sealed class GitChangeEntry
{
    /// <summary>Repository-relative path of the changed file.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// The one-letter git change kind: "M" modified, "A" added, "D" deleted, "R" renamed, "C" copied,
    /// "?" untracked.
    /// </summary>
    public string ChangeKind { get; set; } = "";
}

/// <summary>
/// Phase 7: a markdown-ish blob the user can paste into a new session after
/// the previous one crashed, capturing what was happening and where to pick up.
/// </summary>
public sealed class RecoveryPrompt
{
    public string SessionId { get; set; } = "";
    public string MarkdownBlob { get; set; } = "";

    /// <summary>"ok" | "no_data" | "generated_with_warnings".</summary>
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }
}
