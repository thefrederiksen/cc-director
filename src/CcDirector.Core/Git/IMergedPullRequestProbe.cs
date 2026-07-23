namespace CcDirector.Core.Git;

/// <summary>
/// The authoritative pull-request-merged signal (C1). This is the ONLY signal that reliably
/// recognises a multi-commit branch that was squash-merged into a single commit on main.
/// It is optional: with delete-branch-on-merge on (which it is for our repositories), the
/// origin-branch-gone signal (C2) already catches squashes without a GitHub token. When no
/// token or <c>gh</c> is available the probe returns false, and the local git signals decide.
/// </summary>
public interface IMergedPullRequestProbe
{
    /// <summary>True only if the branch has a MERGED pull request on origin. False if none, or unknown.</summary>
    Task<bool> IsBranchMergedAsync(string repoPath, string branch, CancellationToken ct = default);

    /// <summary>True if the branch has an OPEN pull request on origin (display only). False if none, or unknown.</summary>
    Task<bool> HasOpenPullRequestAsync(string repoPath, string branch, CancellationToken ct = default);
}

/// <summary>
/// The no-signal probe: always reports "not merged" and "no open pull request". Used when a
/// GitHub token is unavailable, so the verdict rests entirely on the local git signals (C2/C3).
/// This is not a fallback that hides a problem - C1 is documented as an optional extra signal.
/// </summary>
public sealed class NullMergedPullRequestProbe : IMergedPullRequestProbe
{
    public Task<bool> IsBranchMergedAsync(string repoPath, string branch, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> HasOpenPullRequestAsync(string repoPath, string branch, CancellationToken ct = default) =>
        Task.FromResult(false);
}
