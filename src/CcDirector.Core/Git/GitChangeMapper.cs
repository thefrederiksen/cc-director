using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Git;

/// <summary>
/// Maps a <see cref="GitStatusProvider"/> per-file result onto a <see cref="GitSnapshot"/>'s additive
/// staged/unstaged lists (issue #1266). The read endpoint that serves the Cockpit's Source Control tab
/// enriches the existing summary snapshot with these lists; the older Wingman consumer never calls this,
/// so its snapshot stays byte-identical. Pure and side-effect-free apart from mutating the two additive
/// lists, so it is unit-testable against a parsed git status without a repository or a web host.
/// </summary>
public static class GitChangeMapper
{
    /// <summary>
    /// Populate <paramref name="snapshot"/>'s <see cref="GitSnapshot.StagedChanges"/> and
    /// <see cref="GitSnapshot.UnstagedChanges"/> from <paramref name="status"/>. Additive only: no
    /// summary field (branch, dirty, ahead/behind, last commit, status) is touched. Each entry carries
    /// the repository-relative path and the one-letter change kind - all the read-only browser view needs.
    /// </summary>
    public static void Enrich(GitSnapshot snapshot, GitStatusResult status)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (status is null) throw new ArgumentNullException(nameof(status));

        snapshot.StagedChanges = status.StagedChanges.Select(ToEntry).ToList();
        snapshot.UnstagedChanges = status.UnstagedChanges.Select(ToEntry).ToList();
    }

    private static GitChangeEntry ToEntry(GitFileEntry entry) => new()
    {
        Path = entry.FilePath,
        ChangeKind = entry.StatusChar,
    };
}
