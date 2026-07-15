namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /sessions/{sid}/prompt on both the Director Control API and the Gateway.
/// </summary>
public sealed class PromptRequest
{
    /// <summary>The text to send to the session.</summary>
    public string Text { get; set; } = "";

    /// <summary>If true (default), append Enter after the text so the session executes the prompt.</summary>
    public bool AppendEnter { get; set; } = true;

    /// <summary>
    /// If true, the Gateway holds the response open until the session returns to Idle (or timeout).
    /// Ignored by the Director's own Control API (which always returns immediately after queueing).
    /// </summary>
    public bool WaitForIdle { get; set; } = false;

    /// <summary>Wait timeout in milliseconds. Default 120000 (2 min). Only used when WaitForIdle=true.</summary>
    public int TimeoutMs { get; set; } = 120_000;

    /// <summary>
    /// True when this prompt is one AGENT prompting another across the fleet (issue #1636), rather than a
    /// person. Set by the fleet relay when a message is addressed to a session on ANOTHER Director: such a
    /// message arrives here as an ordinary prompt, and without this marker it is indistinguishable from a
    /// human one - so the same fleet message would count as agent-driven or not purely by the accident of
    /// whether the two sessions happened to share a Director.
    ///
    /// Same trick as <see cref="DeliveryUploadId"/>, which marks a dictation delivery over the tunnel: the
    /// DTO carries what an HTTP header used to.
    ///
    /// Never counted as a human turn, whatever Surface accompanies it.
    /// </summary>
    public bool AgentDriven { get; set; }

    /// <summary>
    /// DevThrottle Stats surface tag: which surface this remote prompt came from - "phone", "cockpit",
    /// or null when unknown. GATEWAY-AUTHORITATIVE: the Gateway resolves it from the verified per-device
    /// key and OVERWRITES any client-supplied value before forwarding, so it cannot be forged from a
    /// client. Null on a direct Director call (there is no device key to resolve). Rides both prompt
    /// delivery paths (the HTTP body and the SignalR command payload), so the Director's choke-point tally
    /// gets the surface without a separate header. The modality (typed vs voice) is NOT carried here - it
    /// comes from the existing X-Dictation-Delivery marker (a voice delivery) via <c>SendSource</c>.
    /// </summary>
    public string? Surface { get; set; }

    /// <summary>
    /// Gateway Cleanup mission, Phase 2: the dictation delivery marker (issue #1181). When set, this prompt
    /// is a dictation's OWN arrival, so the Director sends it as <c>SendSource.Delivery</c> (a voice turn),
    /// exempt from the dictation lock. Over the REST path this used to ride ONLY the <c>X-Dictation-Delivery</c>
    /// header; carrying it in the request DTO instead lets the tunnel prompt verb mark a Delivery without any
    /// HTTP header (the frozen tunnel envelope is unchanged - this is a request-DTO field). The header is still
    /// honored on the REST path for back-compat; either signal marks the send as a Delivery. Null for a normal
    /// operator prompt.
    /// </summary>
    public string? DeliveryUploadId { get; set; }
}

/// <summary>
/// Response from POST /sessions/{sid}/prompt.
/// </summary>
public sealed class PromptResponse
{
    /// <summary>True if the prompt was accepted and dispatched to the session.</summary>
    public bool Accepted { get; set; }

    /// <summary>UTC timestamp the prompt was accepted by the Director.</summary>
    public DateTime SentAt { get; set; }

    /// <summary>Buffer position (TotalBufferBytes) immediately before the prompt was sent. Use as ?since= cursor.</summary>
    public long BufferCursor { get; set; }

    /// <summary>Activity state after dispatch. Working if accepted; whatever the session was if rejected.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>Only set if WaitForIdle=true: cleaned output produced by the session for this prompt.</summary>
    public string? Output { get; set; }

    /// <summary>Only set if WaitForIdle=true: idle | timeout | failed.</summary>
    public string? WaitStatus { get; set; }

    /// <summary>Error message if Accepted == false.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Body of POST /sessions/{sid}/resize on the Director Control API. Sets the session's PTY
/// grid so a remote terminal (the Cockpit) can use the full window width.
/// </summary>
public sealed class ResizeRequest
{
    /// <summary>Column count (must be &gt; 0).</summary>
    public int Cols { get; set; }

    /// <summary>Row count (must be &gt; 0).</summary>
    public int Rows { get; set; }
}

/// <summary>
/// Response of the resize verb / POST /sessions/{sid}/resize: the grid the session settled on after the
/// resize (clamped to the PTY's limits). Gateway Cleanup mission, Phase 0: the resize verb returns this so
/// the REST route and the tunnel verb share one typed body.
/// </summary>
public sealed class ResizeResponse
{
    /// <summary>Always true on success (the resize was applied).</summary>
    public bool Accepted { get; set; }

    /// <summary>The session's resulting column count.</summary>
    public int Cols { get; set; }

    /// <summary>The session's resulting row count.</summary>
    public int Rows { get; set; }
}

/// <summary>
/// Payload of the terminal-input verb (Gateway Cleanup mission, Phase 0): a browser keystroke frame to
/// forward verbatim to the session's PTY. The bytes are base64-encoded so control bytes (arrows, Ctrl+C,
/// Esc) survive the JSON envelope intact. The target session is the command's SessionId. This is a plain
/// unary write, not a stream verb.
/// </summary>
public sealed class TerminalInputRequest
{
    /// <summary>The raw keystroke bytes, base64-encoded.</summary>
    public string Bytes { get; set; } = "";
}

/// <summary>Body of the git stage/unstage/discard endpoints. Empty paths means "all" (stage/unstage only).</summary>
public sealed class GitPathsRequest
{
    public List<string> Paths { get; set; } = new();
}

/// <summary>Body of POST /sessions/{sid}/git/commit.</summary>
public sealed class GitCommitRequest
{
    public string Message { get; set; } = "";
}

/// <summary>Body of POST /sessions/{sid}/relink - re-point a Director session at a different Claude session id.</summary>
public sealed class RelinkRequest
{
    public string ClaudeSessionId { get; set; } = "";
}
