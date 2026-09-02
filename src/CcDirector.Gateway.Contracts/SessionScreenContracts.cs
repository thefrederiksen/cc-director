namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One turn-end terminal screen as the Director PUSHES it to the Gateway (the Terminal Rules mission,
/// <c>docs/missions/terminal-rules-2026-09-02/brief.md</c>). It is the SAME snapshot the
/// <c>screen-grid</c> verb answers - rows, cursor, cursor visibility, the alternate-screen flag - taken
/// at the moment the Director's own detector flips a session from Working to WaitingForInput, which is
/// the moment the screen stops moving and starts meaning something.
///
/// WHY THE BYTE MARK IS ON HERE, and what it does NOT mean. <see cref="BufferBytes"/> is the number of
/// terminal bytes THIS FRAME REFLECTS, counted inside the same lock that produced the rows, so the mark
/// and the rows are one observation of one moment - it orders a session's captures against its terminal
/// output and lets a reviewer say how far through the session a screen sat.
///
/// It is NOT a currency proof and must never be used as one. An earlier design served a stored screen as
/// the LIVE screen while this mark still equalled the session's pushed byte total; that total reaches the
/// Gateway on a ten-second snapshot and is never refreshed by a terminal write, so it could not establish
/// what its name claimed. A live screen is now always read from the owning Director - see
/// <c>GatewayScreenReader</c> and ruling 13.
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

    /// <summary>How many terminal bytes the captured frame reflects, counted in the same locked
    /// observation that produced <see cref="Rows"/> - see the type comment for what it is not.</summary>
    public long BufferBytes { get; set; }

    /// <summary>The activity state the session was in at capture, as the Director names it. Always the
    /// turn-end state today ("WaitingForInput"); stored because a later reader should not have to assume
    /// it.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>The agent kind, as the Director names it ("ClaudeCode", "Codex", ...).</summary>
    public string Agent { get; set; } = "";
}

/// <summary>
/// A stored screen as a Gateway reader gets it back, with the facts a raw
/// <see cref="ScreenGridResponse"/> cannot carry: when it was captured, which Director captured it, and
/// how far through that terminal's output the captured frame sat. It is HISTORY - the Gateway never
/// serves it as the live screen.
/// </summary>
public sealed class StoredScreen
{
    public string SessionId { get; set; } = "";

    public DateTime CapturedAtUtc { get; set; }

    /// <summary>The screen itself, in the shape every existing reader already consumes.</summary>
    public ScreenGridResponse Grid { get; set; } = new();

    /// <summary>How many terminal bytes the captured frame reflected. See <see cref="ScreenPush"/> for
    /// what this is and, more importantly, what it is not.</summary>
    public long BufferBytes { get; set; }

    public string ActivityState { get; set; } = "";

    public string Agent { get; set; } = "";

    /// <summary>The Director that captured it.</summary>
    public string DirectorId { get; set; } = "";
}
