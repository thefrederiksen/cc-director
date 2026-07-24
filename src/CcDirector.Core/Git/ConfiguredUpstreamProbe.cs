using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Answers the C2 question - "was this branch's upstream deleted on its remote?" - against
/// the branch's CONFIGURED upstream: <c>branch.&lt;name&gt;.remote</c> plus
/// <c>branch.&lt;name&gt;.merge</c>. Querying origin for the LOCAL branch name is wrong twice
/// over: the upstream ref can carry a different name than the local branch, and the remote can
/// be one other than origin. Either mismatch makes a live upstream look deleted, which would
/// rule real unmerged work "merged and safe to delete". If either config value is missing the
/// branch has no configured upstream and C2 does not apply at all.
/// </summary>
public static class ConfiguredUpstreamProbe
{
    /// <summary>
    /// <paramref name="HasConfiguredUpstream"/>: both branch.&lt;name&gt;.remote and
    /// branch.&lt;name&gt;.merge are set. <paramref name="UpstreamGone"/>: the configured ref no
    /// longer exists on the configured remote (only meaningful when a configured upstream exists).
    /// <paramref name="InspectionSucceeded"/>: false when the remote could not be queried - the
    /// caller must fail closed.
    /// </summary>
    public readonly record struct UpstreamVerdict(bool HasConfiguredUpstream, bool UpstreamGone, bool InspectionSucceeded);

    public static async Task<UpstreamVerdict> ProbeAsync(GitCommandRunner git, string repoPath, string branch, CancellationToken ct)
    {
        var remote = await git.RunAsync(repoPath, new[] { "config", "--get", $"branch.{branch}.remote" }, ct);
        var merge = await git.RunAsync(repoPath, new[] { "config", "--get", $"branch.{branch}.merge" }, ct);
        var remoteName = remote.Success ? remote.Output.Trim() : "";
        var mergeRef = merge.Success ? merge.Output.Trim() : "";

        if (remoteName.Length == 0 || mergeRef.Length == 0)
            return new UpstreamVerdict(HasConfiguredUpstream: false, UpstreamGone: false, InspectionSucceeded: true);

        // The merge ref is a full ref name (refs/heads/...), so the ls-remote pattern is exact.
        var lsRemote = await git.RunAsync(repoPath, new[] { "ls-remote", remoteName, mergeRef }, ct);
        if (!lsRemote.Success)
        {
            FileLog.Write($"[ConfiguredUpstreamProbe] ls-remote {remoteName} {mergeRef} FAILED for branch {branch}: {lsRemote.Error}");
            return new UpstreamVerdict(HasConfiguredUpstream: true, UpstreamGone: false, InspectionSucceeded: false);
        }

        return new UpstreamVerdict(
            HasConfiguredUpstream: true,
            UpstreamGone: string.IsNullOrWhiteSpace(lsRemote.Output),
            InspectionSucceeded: true);
    }
}
