namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Issue #1215 (Cockpit plan phase 6): the per-Director reachability presentation record carried in
/// the envelope response of <c>GET /sessions?envelope=true</c>. It gives the Cockpit the three states
/// it renders in place - Online, Wobbly (a recent poll failure absorbed by the grace window, its
/// last-known-good sessions still served but shown dimmed with a "last seen N seconds ago" age), and
/// Offline (the grace window is exhausted).
///
/// Epic #1159 step A changed what OFFLINE does to the sessions, and this record's wording had to change
/// with it (inspection 1, finding 4). An offline Director's sessions used to be DROPPED from the roster;
/// they are now SERVED, dimmed and dated, exactly like a wobbly one's, and they leave only when the
/// Director says a session ended or when the machine passes the eviction horizon. The difference between
/// wobbly and offline is therefore no longer "served or dropped" but how much confidence the rows deserve.
///
/// One entry per Director the roster fan-out considered on this refresh. The Cockpit joins a session
/// to its Director by <see cref="DirectorId"/> (also stamped on each <c>SessionDto.DirectorId</c>) to
/// decide how to present that session, so the list changes appearance in place and never reflows just
/// because one Director missed a single poll.
/// </summary>
public sealed class DirectorReachabilityDto
{
    /// <summary>The Director this reachability record describes (matches <c>SessionDto.DirectorId</c>).</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Machine hostname (best-effort copy of the Director's registered MachineName).</summary>
    public string MachineName { get; set; } = "";

    /// <summary>
    /// The presentation state, one of <see cref="StateOnline"/>, <see cref="StateWobbly"/>, or
    /// <see cref="StateOffline"/>. Never null.
    /// </summary>
    public string State { get; set; } = StateOnline;

    /// <summary>
    /// When the Gateway last successfully read this Director's sessions (ISO 8601 UTC on the wire), or
    /// null if it has never been reached. For <see cref="StateWobbly"/> this is the timestamp of the
    /// last-known-good snapshot being served; for <see cref="StateOnline"/> it is this refresh.
    /// </summary>
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>
    /// How long ago (seconds) the Director was last seen, computed at serve time so the client renders
    /// "last seen N seconds ago" without depending on its own clock being in sync with the Gateway's.
    /// Zero for <see cref="StateOnline"/>; null when never seen.
    /// </summary>
    public double? LastSeenAgeSeconds { get; set; }

    /// <summary>
    /// The reason the last poll failed, for <see cref="StateWobbly"/> and <see cref="StateOffline"/>.
    /// Null while <see cref="StateOnline"/>.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>Fully reachable: the last poll succeeded.</summary>
    public const string StateOnline = "online";

    /// <summary>Recent poll failures absorbed by the grace window; last-known-good sessions are still served, dimmed.</summary>
    public const string StateWobbly = "wobbly";

    /// <summary>
    /// The grace window is exhausted. The Director's last-known sessions are STILL SERVED - dimmed and dated
    /// like a wobbly one's - and they leave only when the Director reports a session ended or the machine
    /// passes the eviction horizon. What this state costs is authority, not presence: the machine did not
    /// answer, so its rows are the last thing it said rather than a confirmed present state.
    /// </summary>
    public const string StateOffline = "offline";
}
