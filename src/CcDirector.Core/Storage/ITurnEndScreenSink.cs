namespace CcDirector.Core.Storage;

/// <summary>
/// One session's resolved terminal screen at the moment its turn ended, as
/// <see cref="TurnReviewLogger"/> hands it to a sink. Plain rows plus the flags a reader classifies
/// on - the same shape the Director's <c>screen-grid</c> verb answers - so a consumer of a STORED
/// screen and a consumer of a LIVE pull read the same thing and cannot drift apart.
/// </summary>
public sealed class TurnEndScreen
{
    public string SessionId { get; set; } = "";

    /// <summary>When the screen was captured (UTC) - the turn-end flip.</summary>
    public DateTime CapturedAtUtc { get; set; }

    /// <summary>The visible grid, top to bottom, each row trailing-trimmed.</summary>
    public string[] Rows { get; set; } = Array.Empty<string>();

    public int CursorRow { get; set; } = -1;

    public int CursorCol { get; set; } = -1;

    /// <summary>Whether the hardware cursor was VISIBLE - a text composer shows it, a drawn picker
    /// hides it and draws its own marker, so a consumer must check this before trusting the cursor
    /// cell.</summary>
    public bool CursorVisible { get; set; }

    public bool IsAlternateScreen { get; set; }

    /// <summary>False when the session has no server-side grid parser at all. That is an UNREADABLE
    /// screen, not an empty one, and a consumer must fail closed on it.</summary>
    public bool HasGrid { get; set; }

    /// <summary>How many terminal bytes THIS FRAME REFLECTS, counted inside the same lock that produced
    /// the grid (<c>Session.SnapshotLiveScreenWithBufferMark</c>) - so the mark and the rows are one
    /// observation of one moment rather than two reads of a moving terminal.</summary>
    public long BufferBytes { get; set; }

    /// <summary>The activity state at capture, as the Director names it.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>The agent kind, as the Director names it ("ClaudeCode", "Codex", ...).</summary>
    public string Agent { get; set; } = "";
}

/// <summary>
/// Where <see cref="TurnReviewLogger"/> sends a turn-end screen in addition to writing its local
/// review record. Exists so <c>CcDirector.Core</c> does not have to know what a Gateway is: the
/// Terminal Rules mission's implementation lives beside the Director's Gateway connection and pushes
/// over the same tunnel everything else is reported on.
///
/// <see cref="Send"/> is deliberately void and non-blocking to its caller: it is invoked on the
/// session's activity-state event, and the local turn review must not wait on a network send or fail
/// because one did.
/// </summary>
public interface ITurnEndScreenSink
{
    void Send(TurnEndScreen screen);
}
