namespace CcDirector.Core.Git;

/// <summary>The outcome the owner chose when handing a repository to an agent.</summary>
public enum AgentHandOffKind
{
    /// <summary>Sort the uncommitted changes, commit in sensible pieces, push a branch, open a pull request.</summary>
    ProtectChanges,

    /// <summary>Identify junk vs real work; propose a discard list for approval before touching anything.</summary>
    CleanUp,

    /// <summary>Explain what is sitting in this repository. No changes at all.</summary>
    ExplainOnly,

    /// <summary>A stranded worktree: land the work or propose discarding it, with proof either way.</summary>
    LandOrDiscardWorktree,
}

/// <summary>
/// Builds the briefs a spawned agent session receives when the owner hands a repository (or a
/// stranded worktree) over. Pure string templates - golden-tested, including the standing law that
/// no AI attribution ever appears in anything the agent produces. The brief is staged into the new
/// session's prompt box for the owner to read and send - the agent never starts unseen.
/// </summary>
public static class AgentBriefTemplates
{
    /// <summary>The hard rules every hand-off carries, verbatim.</summary>
    public const string HardRules = """
        Hard rules for this task:
        - NEVER force-push, rebase shared branches, or delete branches with unmerged commits.
        - NEVER discard files without first showing the list and getting explicit approval in this session.
        - Open a pull request rather than merging to main yourself.
        - Write all commits and pull requests as the repository owner. Do not add any attribution,
          Co-authored-by trailer, robot emoji, or generated-with line naming any AI assistant or vendor.
        - If anything is genuinely ambiguous, stop and ask in this session rather than guessing.
        """;

    public static string Build(AgentHandOffKind kind, RepositoryStatus repo, WorktreeInfo? worktree = null)
    {
        var header = $"""
            You are working in the repository at {repo.Path} (branch {repo.Branch}).
            State right now: {repo.UncommittedCount} uncommitted file(s){DirtyAge(repo)}, ahead {repo.AheadCount} / behind {repo.BehindCount} of origin{BehindMain(repo)}.
            """;

        var task = kind switch
        {
            AgentHandOffKind.ProtectChanges => """
                TASK - protect the uncommitted changes:
                1. Run git status and review every uncommitted file.
                2. Group the changes into coherent pieces and commit each with a clear message.
                3. Push a branch and open a pull request for the owner's review. Nothing merges without them.
                4. Reply with a summary: what was committed, what looked like junk (do NOT delete it), and the pull request link.
                """,
            AgentHandOffKind.CleanUp => """
                TASK - clean up, keeping only what matters:
                1. Classify every uncommitted/untracked file: real work, generated output, or junk.
                2. Present the full discard candidate list here and WAIT for approval.
                3. Only after approval: discard the approved list, and commit anything that is real work.
                """,
            AgentHandOffKind.ExplainOnly => """
                TASK - explain only, change nothing:
                1. Investigate what the uncommitted files are and how they likely got here.
                2. Write a plain-English summary: what is real work, what is generated, what can go.
                3. Make NO changes of any kind - not even staging.
                """,
            AgentHandOffKind.LandOrDiscardWorktree => $"""
                TASK - decide a stranded worktree:
                The worktree at {worktree?.Path} (branch {worktree?.Branch}) has work that never reached origin/main
                (ahead {worktree?.AheadOfMain}, behind {worktree?.BehindMain}).
                1. Review the commits and any uncommitted files in that worktree.
                2. Recommend: land it (rebase onto origin/main, push, open a pull request) or discard it - with reasons.
                3. If landing: do it, and link the pull request. If discarding: list exactly what would be lost and WAIT for approval.
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        return $"{header}\n\n{task}\n\n{HardRules}";
    }

    private static string DirtyAge(RepositoryStatus repo)
        => repo.DirtySinceUtc is { } since && !repo.IsClean
            ? $" (sitting since {since:yyyy-MM-dd}, {(int)Math.Max(0, (DateTime.UtcNow - since).TotalDays)} day(s))"
            : "";

    private static string BehindMain(RepositoryStatus repo)
        => repo.BehindMainCount > 0 ? $", {repo.BehindMainCount} behind main" : "";
}
