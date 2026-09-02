namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One conversation message as the Director PUSHES it to the Gateway (the turn-push mission,
/// <c>docs/missions/turn-push-2026-09-01/brief.md</c>). The Gateway stores these and every reader -
/// Chat, the transcript view, the wingman - reads the stored rows instead of asking the Director to
/// re-read the transcript. Mirrors <c>CcDirector.Core.History.ConversationMessage</c>, and its parts
/// are the same <see cref="HistoryPartDto"/> Chat already renders, so the client contract does not move.
/// </summary>
public sealed class PushedTurn
{
    /// <summary>The message's position in its transcript generation, 0-based and contiguous. Must equal
    /// the batch's <see cref="TurnPushBatch.StartOrdinal"/> plus the turn's index in the batch; a batch
    /// that disagrees with itself is refused whole.</summary>
    public int Ordinal { get; set; }

    /// <summary>"User" or "Assistant".</summary>
    public string Role { get; set; } = "";

    public List<HistoryPartDto> Parts { get; set; } = new();

    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>The agent's own context id for this message (Claude mints a new one on /clear and on
    /// compaction), when the source carries one.</summary>
    public string? ContextId { get; set; }

    /// <summary>A message the agent injected rather than the human or the model producing it.</summary>
    public bool IsMeta { get; set; }

    /// <summary>A message from a nested subagent conversation rather than the main thread.</summary>
    public bool IsSidechain { get; set; }
}

/// <summary>
/// One push from a Director: a contiguous run of messages for one session, starting at
/// <see cref="StartOrdinal"/> within one generation.
///
/// A GENERATION is the identity of the transcript source the messages were read from - for Claude Code
/// the resolved transcript path, which changes on /clear (a new file) and when the agent moves into a
/// worktree (the file moves). The Director re-reads the whole current source and pushes the part the
/// Gateway has not seen; when the source changes, the Director starts a new generation at ordinal 0
/// and the Gateway keeps the old rows until retention removes them.
/// </summary>
public sealed class TurnPushBatch
{
    public string SessionId { get; set; } = "";

    /// <summary>The transcript source's identity - for Claude Code the resolved transcript path. Any
    /// string up to 1024 characters; the Gateway keys rows by a digest of it and keeps the text on the
    /// session's head row.</summary>
    public string Generation { get; set; } = "";

    /// <summary>When the Director first observed THIS generation for the session (UTC). The Gateway
    /// switches a session to a generation only when this is not older than the one it is on, so a
    /// delayed or re-sent batch from a source the session has already left cannot switch Chat back to
    /// it. A Director that restarts stamps the current source with now, which is never older.</summary>
    public DateTime GenerationStartedUtc { get; set; }

    /// <summary>The agent kind, as the Director names it ("ClaudeCode", "Codex", ...).</summary>
    public string Agent { get; set; } = "";

    /// <summary>Whether the Director can read this agent's conversation at all. False pushes no turns and
    /// lets the Gateway say "unsupported" exactly as the Director's own history read did.</summary>
    public bool IsSupported { get; set; } = true;

    /// <summary>True when the source is raw terminal text rather than a structured transcript (Gemini).</summary>
    public bool IsRawText { get; set; }

    /// <summary>The transcript-derived history state ("Idle", "BackgroundRunning", ...) the Director
    /// computed at push time, or null when the agent has none. Computed on the Director because it needs
    /// the transcript and process liveness, which only the Director has.</summary>
    public string? HistoryState { get; set; }

    /// <summary>The ordinal of the first turn in <see cref="Turns"/>. Never negative.</summary>
    public int StartOrdinal { get; set; }

    /// <summary>How many messages the whole generation holds on the Director right now. Never less than
    /// <see cref="StartOrdinal"/> plus the batch length.</summary>
    public int TotalCount { get; set; }

    public List<PushedTurn> Turns { get; set; } = new();
}

/// <summary>
/// What the Gateway already holds for one session: the generation it is on and how many contiguous
/// messages of it have arrived. Returned to a Director on <c>Hello</c> (one per live session) and from
/// each push, so a Director only ever sends what is missing.
/// </summary>
public sealed class TurnWatermark
{
    public string SessionId { get; set; } = "";

    /// <summary>The generation the session is CURRENTLY on, as the source text the Director sent - which
    /// may not be the generation of the batch just pushed, if that batch was from a source the session
    /// has already left.</summary>
    public string Generation { get; set; } = "";

    /// <summary>The length of the contiguous prefix held of the current generation - the next ordinal to
    /// send for it.</summary>
    public int Count { get; set; }
}
