namespace CcDirector.Gateway.Contracts;

// Gateway Cleanup mission, Phase 0 (Wave 4a): the request shapes for the two saved-handover-DOCUMENT read
// verbs moved onto the tunnel command surface (handovers-list and handovers-content). These are the reads
// of the saved handover documents on this machine - DISTINCT from the per-session "handover" info verb.
// They are ADDITIVE: they name the exact query-string arguments the old REST lambdas took (which have no
// home on DirectorCommand, so they ride in the command payload), so the tunnel verb and the re-pointed REST
// route serialize identical JSON. The response shapes (HandoverDto, HandoverContentDto) are the DTOs the old
// routes already produced - unchanged. Kept in one new file so no shared Contracts file is edited.

/// <summary>
/// GET /handovers request. Carries the optional <c>repo</c> query-string argument the old REST route took to
/// filter the saved-handover-document list to those touching one repository. Null / absent means no filter
/// (every document), matching the route's <c>string? repo</c>.
/// </summary>
public sealed class HandoversListRequest
{
    /// <summary>When set, keep only documents whose repo paths include this one (normalized comparison).</summary>
    public string? Repo { get; set; }
}

/// <summary>
/// GET /handovers/content request. Carries the required <c>path</c> query-string argument the old REST route
/// took: the absolute path of the saved handover document whose content is being read. A null / blank path is
/// the route's own BadRequest, exactly as before.
/// </summary>
public sealed class HandoverContentRequest
{
    /// <summary>The absolute path of the saved handover document to read.</summary>
    public string? Path { get; set; }
}
