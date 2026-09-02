namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One turn-end terminal screen as the Director PUSHES it to the Gateway (the Terminal Rules mission,
/// <c>docs/missions/terminal-rules-2026-09-02/brief.md</c>). It is the SAME snapshot the
/// <c>screen-grid</c> verb answers - rows, cursor, cursor visibility, the alternate-screen flag - taken
/// at the moment the Director's own detector flips a session from Working to WaitingForInput, which is
/// the moment the screen stops moving and starts meaning something.
///
/// WHY THE BUFFER MARK IS ON HERE. A stored screen is only useful to a reader that is about to act if
/// the reader can prove it is still what is on the terminal. <see cref="BufferBytes"/> is the session's
/// total bytes ever written at the instant of capture; the Gateway serves this screen only while the
/// session's live pushed snapshot still reports the same number, which is a positive proof that not one
/// byte has reached that terminal since. A repaint, a picker opening, or a person typing on the machine
/// all move the counter, and the reader falls back to a live tunnel pull. The dictation moved-on guard
/// already decides "has this session moved on?" from the same counter.
/// </summary>
public sealed class ScreenPush
{
    public string SessionId { get; set; } = "";

    /// <summary>When the Director captured the screen (UTC). Part of the row's key, so the same capture
    /// re-sent after a reconnect is stored once.</summary>
    public DateTime CapturedAtUtc { get; set; }

    /// <summary>The visible grid as plain-text rows, top to bottom, each trailing-trimmed - exactly the
    /// rows <see cref="ScreenGridResponse.Rows"/> carries.</summary>
    public List<string> Rows { get; set; } = new();

    /// <summary>0-based grid row of the cursor at capture, or -1 when there is no grid.</summary>
    public int CursorRow { get; set; } = -1;

    /// <summary>0-based grid column of the cursor at capture, or -1 when there is no grid.</summary>
    public int CursorCol { get; set; } = -1;

    /// <summary>Whether the hardware cursor was VISIBLE - the discriminator between a text composer and a
    /// drawn full-screen picker, which hides it. A consumer must check this before trusting the cursor
    /// cell.</summary>
    public bool CursorVisible { get; set; }

    /// <summary>Whether the agent had the terminal in the alternate screen buffer at capture.</summary>
    public bool IsAlternateScreen { get; set; }

    /// <summary>Whether the session has a resolved grid to read at all. False for an Embedded session with
    /// no server-side parser: the screen is UNREADABLE, and a reader must fail closed rather than read an
    /// empty grid as "nothing on screen".</summary>
    public bool HasGrid { get; set; }

    /// <summary>The session's total bytes ever written to its terminal at the instant of capture. The
    /// currency proof - see the type comment.</summary>
    public long BufferBytes { get; set; }

    /// <summary>The activity state the session was in at capture, as the Director names it. Always the
    /// turn-end state today ("WaitingForInput"); stored because a later reader should not have to assume
    /// it.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>The agent kind, as the Director names it ("ClaudeCode", "Codex", ...).</summary>
    public string Agent { get; set; } = "";
}

/// <summary>
/// A stored screen as a Gateway reader gets it back, with the two facts a raw
/// <see cref="ScreenGridResponse"/> cannot carry: when it was captured, and whether the Gateway can still
/// prove it is what is on the terminal right now.
/// </summary>
public sealed class StoredScreen
{
    public string SessionId { get; set; } = "";

    public DateTime CapturedAtUtc { get; set; }

    /// <summary>The screen itself, in the shape every existing reader already consumes.</summary>
    public ScreenGridResponse Grid { get; set; } = new();

    /// <summary>The buffer mark taken at capture, so a caller can re-check currency itself.</summary>
    public long BufferBytes { get; set; }

    public string ActivityState { get; set; } = "";

    public string Agent { get; set; } = "";

    /// <summary>The Director that captured it.</summary>
    public string DirectorId { get; set; } = "";
}
