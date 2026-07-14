namespace CcDirector.Core.History;

/// <summary>The speaker of a normalized conversation message.</summary>
public enum ConversationRole
{
    User,
    Assistant,
}

/// <summary>The kind of a single content part within a message.</summary>
public enum ConversationPartKind
{
    /// <summary>Plain message text (a user prompt or an assistant reply).</summary>
    Text,

    /// <summary>An assistant reasoning / thinking block.</summary>
    Thinking,

    /// <summary>An assistant tool invocation.</summary>
    ToolUse,

    /// <summary>The result returned to the agent for a prior tool invocation.</summary>
    ToolResult,
}

/// <summary>
/// One content part of a normalized message. A single message can carry several parts
/// (for example an assistant turn with a thinking block, some text, then a tool call).
/// </summary>
/// <param name="Kind">What this part is.</param>
/// <param name="Text">The human-readable text: the message text, the thinking text, the
/// tool input as raw JSON (for <see cref="ConversationPartKind.ToolUse"/>), or the tool
/// result text (for <see cref="ConversationPartKind.ToolResult"/>).</param>
/// <param name="ToolName">For a tool call, the tool's name; otherwise null.</param>
/// <param name="ToolId">For a tool call, its id; for a tool result, the id of the call it
/// answers. Lets a consumer pair a call with its result. Null when not applicable.</param>
/// <param name="IsError">For a <see cref="ConversationPartKind.ToolResult"/>, whether the tool
/// reported a failure. False for every other kind. Without this a rebuilt view cannot tell a failed
/// tool call from a successful one.</param>
public sealed record ConversationPart(
    ConversationPartKind Kind,
    string Text,
    string? ToolName = null,
    string? ToolId = null,
    bool IsError = false);

/// <summary>One normalized message: a role plus its ordered content parts.</summary>
/// <param name="Role">Who produced the message.</param>
/// <param name="Parts">The message's content parts, in order.</param>
/// <param name="Timestamp">When the message was recorded, if the source carries it.</param>
/// <param name="ContextId">The agent's OWN id for the context this message belongs to, when the source
/// carries one; null otherwise. This is the CONTEXT's identity, not the Director session's: agents mint
/// a new one whenever the context restarts (Claude does so on /clear and on auto-compaction), so it is
/// what groups messages that actually shared a context window. Distinct from the Director session id,
/// which spans every context in one window. A source holding several contexts in one file (Gemini's
/// logs.json) carries it per message, which is why it lives here rather than on the history.</param>
/// <param name="IsMeta">True for a message the agent injected rather than the human or the model
/// producing it. Consumers that render a conversation skip these - showing them adds cards a user never
/// saw. Also the turn boundary for usage accounting. Sources that have no such concept leave it false.</param>
/// <param name="IsSidechain">True when this belongs to a nested subagent conversation rather than the
/// main thread. Kept rather than filtered at parse time so each consumer decides: the Agent view shows
/// them, a conversation replay may not. Filtering here would silently change what the UI displays.</param>
/// <param name="LineNumber">Where this message sits in its source, 1-based, when the source is a
/// line-oriented file; null otherwise. Lets a consumer resume from an offset instead of re-reading a
/// whole transcript.</param>
public sealed record ConversationMessage(
    ConversationRole Role,
    IReadOnlyList<ConversationPart> Parts,
    DateTimeOffset? Timestamp = null,
    string? ContextId = null,
    bool IsMeta = false,
    bool IsSidechain = false,
    int? LineNumber = null);

/// <summary>
/// An agent-agnostic, normalized view of a session's conversation. Every agent provider
/// (the Claude transcript reader, a future Codex rollout reader, and so on) maps its
/// native store into this one shape, so Wingman and session-save consume a single schema
/// regardless of which agent produced the conversation.
/// </summary>
/// <param name="Messages">The conversation messages, in chronological order.</param>
public sealed record ConversationHistory(IReadOnlyList<ConversationMessage> Messages)
{
    /// <summary>An empty history (no messages).</summary>
    public static ConversationHistory Empty { get; } = new(Array.Empty<ConversationMessage>());

    /// <summary>
    /// The main-thread conversation: what a reader of the conversation expects to see. Drops nested
    /// subagent turns, and drops messages carrying no content (an assistant line can exist purely to
    /// hold a token-usage block - real for accounting, not something anyone said).
    ///
    /// A parse keeps everything so it can serve every consumer from one read; the ones that render a
    /// conversation narrow it here. Sources with no sidechain concept are unaffected.
    /// </summary>
    public ConversationHistory MainThread
    {
        get
        {
            var kept = Messages.Where(m => !m.IsSidechain && m.Parts.Count > 0).ToList();
            return kept.Count == Messages.Count ? this
                : kept.Count == 0 ? Empty
                : new ConversationHistory(kept);
        }
    }
}
