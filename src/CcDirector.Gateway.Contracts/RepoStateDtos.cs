namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One repository's git-hygiene snapshot as a Director observes it (issue #2118, slice 1 of #2096) - the
/// one data feed the morning report cannot get from any store the Gateway already has. The Gateway knows a
/// repository EXISTS; only the machine holding the checkout can say what its branches and worktrees look
/// like.
///
/// NAMES, PATHS, COUNTS AND DATES ONLY - AND THAT IS A HARD BOUNDARY, NOT A STYLE PREFERENCE. There is no
/// field here for file contents, for a diff, or for a commit MESSAGE, and none may be added: this payload
/// leaves the owner's machine and lands in a hosted, multi-tenant database, so anything it can carry is
/// something a private repository can leak. A commit SHA is a name; a commit message is content.
/// <c>RepoStatePayloadTests</c> asserts the serialized payload against that boundary by inspection of the
/// contract's own shape, so a well-meaning "just add the subject line" fails the build rather than shipping.
/// </summary>
public sealed class RepoStateSnapshotDto
{
    /// <summary>The repository's display name as the Director's registry knows it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The absolute path of the PRIMARY checkout on the Director's machine.</summary>
    public string Path { get; set; } = "";

    /// <summary>When the Director collected this snapshot.</summary>
    public DateTime CollectedAtUtc { get; set; }

    /// <summary>
    /// The repository's default branch (e.g. "main"), or NULL when the Director could not determine it.
    /// Null is a real answer and is never guessed: without a default branch there is nothing to be merged
    /// INTO, so every <see cref="RepoStateBranchDto.MergedIntoDefault"/> in this snapshot is also null and
    /// no hygiene recommendation is made about this repository.
    /// </summary>
    public string? DefaultBranch { get; set; }

    /// <summary>The branch checked out in the MAIN working tree, or null when it is on a detached head.</summary>
    public string? CurrentBranch { get; set; }

    /// <summary>True when the main working tree has any uncommitted change.</summary>
    public bool IsDirty { get; set; }

    /// <summary>Local branches. Excludes nothing - the report does the excluding, so the feed stays factual.</summary>
    public List<RepoStateBranchDto> Branches { get; set; } = new();

    /// <summary>Linked worktrees (the primary checkout is not one).</summary>
    public List<RepoStateWorktreeDto> Worktrees { get; set; } = new();
}

/// <summary>One local branch: its name, how old its tip is, how far ahead it is, and whether it is merged.</summary>
public sealed class RepoStateBranchDto
{
    public string Name { get; set; } = "";

    /// <summary>The tip commit's author/committer date, or null when git did not report one.</summary>
    public DateTime? TipCommitUtc { get; set; }

    /// <summary>Commits this branch carries that the default branch does not.</summary>
    public int CommitsAheadOfDefault { get; set; }

    /// <summary>
    /// Whether every commit on this branch is already contained in the default branch. NULL means NOT
    /// DETERMINED - the default branch was undetectable, or the inspection failed - and a null must never
    /// be read as either true or false: "we could not tell" is the answer, and a recommendation built on
    /// it would be a guess about the owner's unmerged work.
    /// </summary>
    public bool? MergedIntoDefault { get; set; }

    /// <summary>True when this branch is checked out in the main tree or in a linked worktree.</summary>
    public bool CheckedOut { get; set; }
}

/// <summary>One linked worktree: where it is, what it holds, and whether its branch is already merged.</summary>
public sealed class RepoStateWorktreeDto
{
    /// <summary>The worktree's absolute path on the Director's machine.</summary>
    public string Path { get; set; } = "";

    /// <summary>The branch it holds, or null on a detached head.</summary>
    public string? Branch { get; set; }

    /// <summary>The tip commit's date of the branch this worktree holds, or null when it holds no named
    /// branch (detached head) or git reported no date.</summary>
    public DateTime? TipCommitUtc { get; set; }

    /// <summary>
    /// The most recent of the worktree's last commit, its folder's change time, and its HEAD reflog - the
    /// honest "when was this last touched". Carried ALONGSIDE <see cref="TipCommitUtc"/> and not instead of
    /// it, because they answer different questions and the report needs this one: a worktree whose last
    /// commit is three weeks old but whose files were edited this morning is being worked in, and calling
    /// it stale on the commit date alone would recommend deleting live work.
    /// </summary>
    public DateTime? LastActivityUtc { get; set; }

    /// <summary>True when the worktree has any uncommitted change.</summary>
    public bool IsDirty { get; set; }

    /// <summary>Whether its branch is contained in the default branch. NULL means not determined - see
    /// <see cref="RepoStateBranchDto.MergedIntoDefault"/>.</summary>
    public bool? BranchMergedIntoDefault { get; set; }

    /// <summary>True when a live Director session is working in this worktree right now.</summary>
    public bool HasLiveSession { get; set; }
}

/// <summary>
/// A Director's batched repo-state push: every registered repository in one request. The DIRECTOR is
/// identified by its authenticated device key, never by anything in this body - the tenant and the Director
/// identity both come from the credential, so a payload cannot claim to be someone else's machine.
/// </summary>
public sealed class RepoStatePushRequest
{
    /// <summary>The pushing Director's id. Used as the per-repository row key beside the tenant; it is NOT
    /// an authorization claim (the device key is).</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>The machine's display name, for the report's deep links.</summary>
    public string MachineName { get; set; } = "";

    public List<RepoStateSnapshotDto> Repositories { get; set; } = new();
}

/// <summary>What the Gateway tells a pushing Director it durably stored.</summary>
public sealed class RepoStatePushResponse
{
    public int Stored { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
}
