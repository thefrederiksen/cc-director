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
    /// devthrottle_internal#1176: the Director's user-editable display name (copy of the registered
    /// <c>DirectorDto.DisplayName</c>), carried here because the Fleet Map reads ONLY this envelope -
    /// no second fetch - and its director labels need a real name. Empty when unnamed; clients fall
    /// back to <see cref="MachineName"/>.
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// The presentation state, one of <see cref="StateOnline"/>, <see cref="StateWobbly"/>,
    /// <see cref="StateOffline"/>, or <see cref="StateStopped"/>. Never null.
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

    /// <summary>
    /// The Director SAID GOODBYE: it sent the tunnel farewell at the start of an orderly shutdown, so its
    /// registration is retired (see <see cref="DirectorDto.StoppedAtUtc"/>) and its absence is expected.
    ///
    /// This is deliberately NOT <see cref="StateOffline"/>, and the difference is the whole point of the
    /// state. Offline means the Gateway CANNOT REACH a Director it still expects to be there - a fact the
    /// owner needs, because something is wrong. Stopped means nobody is there and nobody should be - which is
    /// the ordinary end of every Director that is ever shut down, and is not a fault at all. Reporting the
    /// second as the first turned every clean shutdown into a day-long warning on a machine that was fine.
    ///
    /// A stopped Director still cannot be acted on (its tunnel is down, so a command has nothing to travel
    /// over), so it is not offered as free capacity either - it is simply not running.
    ///
    /// ON ADDING A FOURTH VALUE TO A WIRE FIELD OLDER CLIENTS ALREADY READ, since that is a fair question to
    /// ask of this line. A Cockpit built before this state exists treats an unrecognised value as online, so
    /// against a newer Gateway it would offer "+ New session" on a Director that cannot take one. That skew
    /// cannot occur: the Cockpit bundle is SERVED BY THE GATEWAY and redeploys with it, so the client reading
    /// this field is always the one shipped beside it. The surface that genuinely can be older is the mobile
    /// app, and it renders no Director badge and no start action - it reads reachability only through the
    /// roster retention fold, which routes unknown states to its unreachable treatment (conservative, and
    /// wrong only in tone). Directors of any age are unaffected: an older one simply never sends the farewell,
    /// so it never reaches this state.
    /// </summary>
    public const string StateStopped = "stopped";

    /// <summary>
    /// THE BADGE WORD for this Director, decided here and printed verbatim. Empty while
    /// <see cref="StateOnline"/> - a healthy Director wears no badge.
    ///
    /// This and the three members below exist because of the standing rule that the Gateway owns all ruling
    /// and the client only renders (CLAUDE.md rule 7). The Cockpit used to map state to a word, to a
    /// placeholder sentence, to whether a button appeared, and to whether a card dimmed - four judgements
    /// about what a state MEANS, in a view file, re-made in each of three places. Adding one state to that
    /// shape means finding every branch; a client that meets a state it does not know renders something
    /// plausible instead of something true. Adding a state HERE is one edit, and an unknown state degrades to
    /// a Gateway-written label rather than to a guess.
    /// </summary>
    public string StateLabel { get; set; } = "";

    /// <summary>
    /// True when this Director's rows are LAST-KNOWN rather than confirmed - it did not answer on this
    /// refresh. The client dims its cards and shows the last-seen age. True for wobbly, offline and stopped;
    /// false only for online.
    /// </summary>
    public bool DataIsStale { get; set; }

    /// <summary>
    /// True when a new session started here could actually be delivered. A start is routed to the Director
    /// down its tunnel, so this is false whenever that tunnel is down (offline, stopped) and true when it is
    /// up (online, and wobbly - where only the last push is late, and the command still lands). A button that
    /// cannot do what it says reads as the app being broken, so the client shows the action only when this
    /// says it can be honoured.
    /// </summary>
    public bool CanStartSession { get; set; } = true;

    /// <summary>
    /// The finished line to print where this Director has NO sessions, saying why there are none - free
    /// capacity, an unreachable link, or a Director that is simply not running. Printed verbatim.
    /// </summary>
    public string EmptySlotText { get; set; } = "";
}
