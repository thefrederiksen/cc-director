namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The desktop app's "Handover info" for a single session (issue #1214): the small
/// identity/locate block a person or another agent needs to find the session and talk
/// to it. Mirrors the desktop "Copy Handover Info" clipboard block
/// (CcDirector.Avalonia MainWindow.CopySessionNameAndId) with ONE deliberate exclusion -
/// the Director's Control API endpoint. That endpoint is a Director address, and issue
/// #1214 requires the browser to talk only to the Gateway and never learn a Director
/// address, so it is omitted here.
///
/// Returned by the Director Control API GET /sessions/{sid}/handover and proxied
/// verbatim (with DirectorId stamped) by the Gateway GET /sessions/{sid}/handover.
/// </summary>
public sealed class HandoverInfoDto
{
    /// <summary>Stable session id.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The session's composed display name (never the bare folder name).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Repository / working directory the session runs in.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>Id of the Director that hosts the session. Empty in a Director-local
    /// response; the Gateway stamps it when proxying.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Machine name of the host running the session.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>Version of the Director that hosts the session.</summary>
    public string Version { get; set; } = "";
}
