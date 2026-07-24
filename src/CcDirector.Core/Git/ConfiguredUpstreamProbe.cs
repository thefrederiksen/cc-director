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
        // --get-all, not --get: git permits MULTIPLE merge values (an octopus pull), and --get
        // silently returns only the last one - which could be gone while another configured
        // merge ref survives, a false "upstream gone" on a destructive path (ruling R2-7).
        var merge = await git.RunAsync(repoPath, new[] { "config", "--get-all", $"branch.{branch}.merge" }, ct);
        var remoteName = remote.Success ? remote.Output.Trim() : "";
        var mergeRefs = merge.Success
            ? merge.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        if (remoteName.Length == 0 || mergeRefs.Length == 0)
            return new UpstreamVerdict(HasConfiguredUpstream: false, UpstreamGone: false, InspectionSucceeded: true);

        if (mergeRefs.Length > 1)
        {
            // Ambiguous configuration fails closed: the branch is simply NOT eligible for the
            // origin-gone signal, exactly as if no upstream were configured.
            FileLog.Write($"[ConfiguredUpstreamProbe] branch {branch} has {mergeRefs.Length} configured merge values - C2 does not apply");
            return new UpstreamVerdict(HasConfiguredUpstream: false, UpstreamGone: false, InspectionSucceeded: true);
        }

        var mergeRef = mergeRefs[0];

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
