namespace CcDirector.Core.Git;

/// <summary>
/// The canonical key for "are these two strings the same repository?" (issue #1111, item 3).
///
/// A session's <c>RepoPath</c> is stored however it arrived, so ONE directory routinely appears under
/// several spellings at once. On the machine in the issue, twenty-three sessions over six repositories
/// carried eight distinct <c>RepoPath</c> strings - <c>D:/ReposFred/devthrottle</c> and
/// <c>D:\ReposFred\devthrottle</c> being the same tree written two ways. Anything that groups on the raw
/// value therefore counts one repository as two, and any cache keyed on it keeps two entries that can
/// disagree.
///
/// That is not a hypothetical: an unnormalized <c>RepoPath</c> (a trailing separator) is the known root
/// cause of the transcript-folder failure behind the stuck-yellow voice bug. Same defect class.
///
/// This deliberately produces a COMPARISON KEY and not a display string. It lowercases, which is right for
/// comparing paths on Windows and wrong for showing them to a person - so it is never what gets rendered,
/// and <c>RepoPath</c> keeps the casing the user chose.
/// </summary>
public static class RepoPathKey
{
    /// <summary>
    /// Canonical comparison form: full path, trailing separators trimmed, lowercased. Matches the form
    /// the Control API already uses for its <c>?repo=</c> filters and overview grouping, so the desktop
    /// and the wire agree on what counts as one repository.
    ///
    /// Never throws. A path that cannot be resolved (empty, embedded NUL, a shape Windows rejects) falls
    /// back to a trimmed, lowercased form of the original: two identical unresolvable strings still group
    /// together, which is the property callers depend on, and no caller is asked to handle an exception
    /// for a value it only wants to compare.
    /// </summary>
    public static string For(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return string.Empty;

        try
        {
            return Path.GetFullPath(repoPath).TrimEnd('\\', '/').ToLowerInvariant();
        }
        catch
        {
            return repoPath.Trim().TrimEnd('\\', '/').ToLowerInvariant();
        }
    }
}
