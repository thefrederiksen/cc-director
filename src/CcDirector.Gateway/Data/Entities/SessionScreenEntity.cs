namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One stored turn-end terminal screen for one session - the row the Terminal Rules mission puts behind
/// every Gateway screen read (<c>docs/missions/terminal-rules-2026-09-02/brief.md</c>). Pushed by the
/// owning Director at the moment its detector flips the session from Working to WaitingForInput, stored
/// per tenant, and read by the wingman's screen readers, the supervisor, and later the rules engine.
///
/// Keyed by (tenant, session, captured-at). The capture time is part of the key so a Director that
/// re-sends a capture after a reconnect stores it once rather than twice, and so the history of a
/// session's turn-end screens is kept in order rather than one row being overwritten - the rules engine
/// has to be able to say WHICH screen a decision was made on, and the Cockpit has to be able to show
/// yesterday's.
///
/// This is a SEPARATE store from the conversation the turn-push mission (#2638) holds, deliberately: a
/// screen is bulky, loses its value in days rather than months, and is captured from a different source.
/// Retention here is SEVEN days, run by <c>SessionScreenSweep</c>, not the ninety of session history.
/// </summary>
public sealed class SessionScreenEntity : TenantScopedEntity
{
    public string SessionId { get; set; } = "";

    /// <summary>When the Director captured the screen (UTC, whole milliseconds - see
    /// <c>SessionScreenStore.CapturePrecision</c> for why the precision is pinned).</summary>
    public DateTime CapturedAtUtc { get; set; }

    public string DirectorId { get; set; } = "";

    /// <summary>The visible grid's plain-text rows as a JSON array - the bulky sub-document in one
    /// column, the CronJob pattern the other Gateway stores use.</summary>
    public string RowsJson { get; set; } = "[]";

    public int CursorRow { get; set; } = -1;

    public int CursorCol { get; set; } = -1;

    /// <summary>Whether the hardware cursor was visible at capture (composer) or hidden (drawn picker).</summary>
    public bool CursorVisible { get; set; }

    public bool IsAlternateScreen { get; set; }

    /// <summary>False for a session with no server-side grid parser: an UNREADABLE screen, which a reader
    /// must not mistake for an empty one.</summary>
    public bool HasGrid { get; set; }

    /// <summary>The session's total bytes ever written at capture. A reader serves this screen as the
    /// live one only while the session's pushed snapshot still reports this exact number.</summary>
    public long BufferBytes { get; set; }

    /// <summary>The activity state at capture, as the Director names it.</summary>
    public string ActivityState { get; set; } = "";

    public string Agent { get; set; } = "";

    /// <summary>When this Gateway received the row. Retention cuts on this rather than on the capture
    /// time, so a screen that took a long path to arrive still gets its full seven days.</summary>
    public DateTime ReceivedAtUtc { get; set; }
}
