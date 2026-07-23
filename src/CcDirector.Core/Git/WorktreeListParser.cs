namespace CcDirector.Core.Git;

/// <summary>
/// Parses the output of <c>git worktree list --porcelain</c> into raw entries.
/// Pure and side-effect free so the parsing can be unit-tested against fixed fixtures.
/// Git always lists the repository's main working tree first, then the linked worktrees.
/// </summary>
public static class WorktreeListParser
{
    /// <summary>
    /// Parses porcelain worktree output. Each record is separated by a blank line and
    /// carries a <c>worktree &lt;path&gt;</c> line, a <c>HEAD &lt;sha&gt;</c> line, and either
    /// a <c>branch refs/heads/&lt;name&gt;</c> line or a <c>detached</c> marker.
    /// </summary>
    public static IReadOnlyList<RawWorktreeEntry> Parse(string porcelain)
    {
        var entries = new List<RawWorktreeEntry>();

        string? path = null;
        string? head = null;
        string? branch = null;
        bool detached = false;
        bool bare = false;

        void Flush()
        {
            if (path != null)
            {
                entries.Add(new RawWorktreeEntry
                {
                    Path = path,
                    Head = head ?? "",
                    Branch = branch,
                    IsDetached = detached,
                    IsBare = bare,
                });
            }
            path = null;
            head = null;
            branch = null;
            detached = false;
            bare = false;
        }

        foreach (var rawLine in (porcelain ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                // A new record begins - flush any in-progress one first.
                Flush();
                path = line["worktree ".Length..].Trim();
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line["HEAD ".Length..].Trim();
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = ShortBranch(line["branch ".Length..].Trim());
            }
            else if (line == "detached")
            {
                detached = true;
            }
            else if (line == "bare")
            {
                bare = true;
            }
        }

        Flush();
        return entries;
    }

    private static string ShortBranch(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..]
            : reference;
}
