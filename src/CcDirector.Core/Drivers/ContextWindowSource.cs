namespace CcDirector.Core.Drivers;

/// <summary>
/// WHERE a reported context window came from (issue #1100).
///
/// The gauge once showed "ctx 184k / 200k (92%)" in red for a session with roughly 800,000 tokens of
/// headroom left. The used count was a real measurement; the denominator was a guess, derived from the
/// letters "opus" in a model name. Nothing on screen distinguished the two, and nothing could - a
/// percentage looks identical whether it was measured or invented.
///
/// This is the field that tells them apart. A window without a source is not reportable, which is the
/// whole point: it makes "we derived this" impossible to write down without saying so.
/// </summary>
public enum ContextWindowSource
{
    /// <summary>
    /// The agent has not told us, so we do not know. The gauge shows the raw used-token count with no
    /// percentage, no bar and no colour.
    ///
    /// This is a CORRECT shipped state, not an unfinished one. A missing number is a minor annoyance; a
    /// confident wrong number gets acted on - it pushes someone to compact or hand over a session that has
    /// barely started to fill.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The agent wrote the window into its own session file and we read it there. Codex does this on every
    /// <c>token_count</c> event, and it is the reference the other drivers are measured against.
    /// </summary>
    AgentSessionFile,

    /// <summary>
    /// The agent pushed the number to us for the live session. Claude Code's status line carries
    /// <c>context_window.context_window_size</c> - the same figure the tool uses for its own display and
    /// for auto-compaction - and it follows a mid-session model switch.
    /// </summary>
    AgentReported,
}
