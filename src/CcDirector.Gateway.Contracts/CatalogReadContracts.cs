namespace CcDirector.Gateway.Contracts;

// Gateway Cleanup mission, Phase 0 (Worker R2): the request shapes for the CATALOG / director-level READ
// verbs that were moved onto the tunnel command surface. These are ADDITIVE - they name the exact
// query-string arguments the old REST lambdas took (they have no home on DirectorCommand, so they ride in
// the command payload), so the tunnel verb and the re-pointed REST route serialize identical JSON. The
// response shapes are the DTOs the old routes already produced (ClaudeSessionDto, CoachingCategoryDto,
// DirectoryListingDto, GitSnapshot, DirectorCrashJournalData) - unchanged. Kept in one new file so no shared
// Contracts file is edited.

/// <summary>
/// GET /claude-sessions request. Carries the optional <c>repo</c> query-string argument the old REST route
/// took to filter the resumable-session list to one repository. Null / absent means no filter (every repo),
/// matching the route's <c>string? repo</c>.
/// </summary>
public sealed class ClaudeSessionsRequest
{
    /// <summary>When set, keep only sessions whose repo path matches this one (normalized comparison).</summary>
    public string? Repo { get; set; }
}

/// <summary>
/// GET /fs/list request. Carries the optional <c>path</c> query-string argument the old REST route took. Null
/// / absent lists the drive roots; otherwise the named directory is listed, matching the route's
/// <c>string? path</c>.
/// </summary>
public sealed class FsListRequest
{
    /// <summary>The directory to list, or null / absent to list the drive roots.</summary>
    public string? Path { get; set; }
}
