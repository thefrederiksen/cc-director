namespace CcDirector.Core.Claude;

/// <summary>
/// Response from a Claude CLI invocation with full metadata.
/// Deserialized from --output-format json output.
/// </summary>
public sealed class ClaudeResponse
{
    /// <summary>The text response from Claude.</summary>
    public required string Result { get; init; }

    /// <summary>The Claude session ID (UUID).</summary>
    public required string SessionId { get; init; }

    /// <summary>"success" or "error_max_turns".</summary>
    public required string Subtype { get; init; }

    /// <summary>Whether the response is an error.</summary>
    public bool IsError { get; init; }

    /// <summary>Total API cost in USD.</summary>
    public decimal TotalCostUsd { get; init; }

    /// <summary>Number of agentic turns used.</summary>
    public int NumTurns { get; init; }

    /// <summary>Total wall-clock duration in milliseconds.</summary>
    public int DurationMs { get; init; }

    /// <summary>Duration of API calls only, in milliseconds.</summary>
    public int DurationApiMs { get; init; }

    /// <summary>Token usage breakdown.</summary>
    public required ClaudeUsage Usage { get; init; }

    /// <summary>The process exit code.</summary>
    public int ExitCode { get; init; }
}

/// <summary>
/// Response from a Claude CLI invocation with a typed structured result.
/// </summary>
public sealed class ClaudeResponse<T>
{
    /// <summary>The deserialized structured result.</summary>
    public required T Result { get; init; }

    /// <summary>The raw text result before deserialization.</summary>
    public required string RawResult { get; init; }

    /// <summary>The Claude session ID (UUID).</summary>
    public required string SessionId { get; init; }

    /// <summary>"success" or "error_max_turns".</summary>
    public required string Subtype { get; init; }

    /// <summary>Whether the response is an error.</summary>
    public bool IsError { get; init; }

    /// <summary>Total API cost in USD.</summary>
    public decimal TotalCostUsd { get; init; }

    /// <summary>Number of agentic turns used.</summary>
    public int NumTurns { get; init; }

    /// <summary>Total wall-clock duration in milliseconds.</summary>
    public int DurationMs { get; init; }

    /// <summary>Token usage breakdown.</summary>
    public required ClaudeUsage Usage { get; init; }

    /// <summary>The process exit code.</summary>
    public int ExitCode { get; init; }
}

/// <summary>
/// Token usage from a Claude response.
/// </summary>
public sealed class ClaudeUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CacheReadInputTokens { get; init; }
    public int CacheCreationInputTokens { get; init; }
}

/// <summary>
/// A single event from a stream-json Claude response.
/// </summary>
public sealed class ClaudeStreamEvent
{
    /// <summary>"system", "assistant", "result", "tool_use", "tool_result", etc.</summary>
    public required string Type { get; init; }

    /// <summary>Subtype if present: "init", "hook_started", "success", "error_max_turns".</summary>
    public string? Subtype { get; init; }

    /// <summary>Session ID if present in this event.</summary>
    public string? SessionId { get; init; }

    /// <summary>Text content for assistant messages or result text.</summary>
    public string? Text { get; init; }

    /// <summary>The raw JSON line for advanced parsing.</summary>
    public required string RawJson { get; init; }
}

/// <summary>
/// Handle to a streaming Claude session. Provides async enumerable access to events.
/// MUST be disposed to clean up the underlying process.
/// </summary>
public sealed class ClaudeStreamResult : IAsyncDisposable
{
    private readonly System.Diagnostics.Process _process;
    private readonly CancellationTokenSource _cts;

    /// <summary>Session ID captured from the init message. Available after first event.</summary>
    public string? SessionId { get; internal set; }

    /// <summary>Stream of events from Claude.</summary>
    public IAsyncEnumerable<ClaudeStreamEvent> Events { get; }

    internal ClaudeStreamResult(
        System.Diagnostics.Process process,
        IAsyncEnumerable<ClaudeStreamEvent> events,
        CancellationTokenSource cts)
    {
        _process = process;
        Events = events;
        _cts = cts;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited
            }

            try
            {
                using var exitCts = new CancellationTokenSource(5000);
                await _process.WaitForExitAsync(exitCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Best effort
            }
        }

        _cts.Dispose();
        _process.Dispose();
    }
}
