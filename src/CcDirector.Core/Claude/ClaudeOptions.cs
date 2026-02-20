namespace CcDirector.Core.Claude;

/// <summary>
/// Options for a Claude CLI invocation. All properties are optional.
/// Only non-null/non-default values are included in the generated CLI arguments.
/// </summary>
public sealed class ClaudeOptions
{
    // --- Model ---

    /// <summary>Model name: "haiku", "sonnet", "opus", or a full model ID.</summary>
    public string? Model { get; init; }

    /// <summary>Fallback model if primary model fails.</summary>
    public string? FallbackModel { get; init; }

    // --- Execution Control ---

    /// <summary>Maximum number of agentic turns.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>Maximum budget in USD. 0 = immediate failure.</summary>
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>Override timeout for this request (milliseconds).</summary>
    public int? TimeoutMs { get; init; }

    // --- System Prompt ---

    /// <summary>Replace the default system prompt entirely.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Append to the default system prompt.</summary>
    public string? AppendSystemPrompt { get; init; }

    /// <summary>Replace system prompt from a file path.</summary>
    public string? SystemPromptFile { get; init; }

    /// <summary>Append system prompt from a file path.</summary>
    public string? AppendSystemPromptFile { get; init; }

    // --- Tools and Permissions ---

    /// <summary>Permission mode: "plan", "acceptEdits", "bypassPermissions".</summary>
    public string? PermissionMode { get; init; }

    /// <summary>Skip all permission checks. Default true for automation safety.</summary>
    public bool SkipPermissions { get; init; }

    /// <summary>Tool set: "default", "" (none), or "Read,Bash,..." (specific list).</summary>
    public string? Tools { get; init; }

    /// <summary>Additional tools to allow (comma-separated, supports patterns like "Bash(git *)").</summary>
    public string? AllowedTools { get; init; }

    /// <summary>Tools to disallow (comma-separated).</summary>
    public string? DisallowedTools { get; init; }

    // --- Session ---

    /// <summary>Explicit session ID (UUID).</summary>
    public string? SessionId { get; init; }

    /// <summary>Continue the most recent session.</summary>
    public bool Continue { get; init; }

    /// <summary>Fork the session instead of continuing in-place.</summary>
    public bool ForkSession { get; init; }

    /// <summary>Don't persist session to disk (ephemeral).</summary>
    public bool NoSessionPersistence { get; init; }

    // --- Context ---

    /// <summary>Additional directories to include in context.</summary>
    public List<string>? AddDirs { get; init; }

    // --- Advanced ---

    /// <summary>Path to MCP config file.</summary>
    public string? McpConfig { get; init; }

    /// <summary>Inline JSON agents definition.</summary>
    public string? Agents { get; init; }

    /// <summary>Path to custom settings file.</summary>
    public string? Settings { get; init; }
}
