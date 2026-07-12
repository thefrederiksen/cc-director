namespace CcDirector.Gateway.Contracts;

// Gateway Cleanup mission, Phase 0 (Wave 4a): the request and response shapes for the screenshot-delete unary
// byte verb (the tunnel twin of DELETE /screenshots/file). ADDITIVE: the request DTO names the bare file
// name the old route took as a ?name= query argument (which has no home on DirectorCommand, so it rides in
// the command payload), and the response reproduces the exact anonymous-object wire shape the old route
// returned. Kept in one new file so no shared Contracts file is edited.

/// <summary>
/// DELETE /screenshots/file request. Carries the required <c>name</c> query-string argument the old REST
/// route took: the bare screenshot file name to delete from THIS Director's screenshots folder. The name is
/// resolved traversal-safe on the Director side, so an escaping or non-image name is the route's own 404.
/// </summary>
public sealed class ScreenshotDeleteRequest
{
    /// <summary>The bare screenshot file name to delete (must resolve inside the screenshots folder).</summary>
    public string? Name { get; set; }
}

/// <summary>
/// DELETE /screenshots/file response. Byte-identical to the <c>{ deleted = true, fileName }</c> object the
/// REST route returned on success: the delete flag and the deleted file's bare name. A name that does not
/// resolve to an existing screenshot is the route's 404 (carried as a NotFound command result), not a body.
/// </summary>
public sealed class ScreenshotDeleteResponse
{
    /// <summary>Always true on success (the not-found case is a NotFound, not this body).</summary>
    public bool Deleted { get; set; }

    /// <summary>The deleted file's bare name.</summary>
    public string FileName { get; set; } = "";
}
