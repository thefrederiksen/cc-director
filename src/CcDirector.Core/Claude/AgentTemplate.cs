using System.Text;
using System.Text.Json.Serialization;

namespace CcDirector.Core.Claude;

/// <summary>
/// A reusable agent template -- saved combination of model, system prompt,
/// permissions, and tools that can be launched on any project.
/// </summary>
public sealed class AgentTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    // Model
    public string? Model { get; set; }
    public string? FallbackModel { get; set; }

    // Execution
    public int? MaxTurns { get; set; }
    public decimal? MaxBudgetUsd { get; set; }

    // System Prompt
    public string? SystemPrompt { get; set; }
    public string? AppendSystemPrompt { get; set; }

    // Permissions
    public string? PermissionMode { get; set; }
    public bool SkipPermissions { get; set; }

    // Tools
    public string? Tools { get; set; }
    public string? AllowedTools { get; set; }
    public string? DisallowedTools { get; set; }

    // Advanced
    public string? McpConfigPath { get; set; }

    // Metadata
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Build CLI arguments string for launching an interactive session.</summary>
    public string BuildCliArgs()
    {
        var sb = new StringBuilder();

        if (Model is not null)
            sb.Append($" --model {Model}");
        if (FallbackModel is not null)
            sb.Append($" --fallback-model {FallbackModel}");
        if (MaxTurns.HasValue)
            sb.Append($" --max-turns {MaxTurns.Value}");
        if (MaxBudgetUsd.HasValue)
            sb.Append($" --max-budget-usd {MaxBudgetUsd.Value}");
        if (SystemPrompt is not null)
            sb.Append($" --system-prompt \"{SystemPrompt.Replace("\"", "\\\"")}\"");
        if (AppendSystemPrompt is not null)
            sb.Append($" --append-system-prompt \"{AppendSystemPrompt.Replace("\"", "\\\"")}\"");
        if (SkipPermissions)
            sb.Append(" --dangerously-skip-permissions");
        if (PermissionMode is not null)
            sb.Append($" --permission-mode {PermissionMode}");
        if (Tools is not null)
            sb.Append($" --tools \"{Tools.Replace("\"", "\\\"")}\"");
        if (AllowedTools is not null)
            sb.Append($" --allowedTools \"{AllowedTools.Replace("\"", "\\\"")}\"");
        if (DisallowedTools is not null)
            sb.Append($" --disallowedTools \"{DisallowedTools.Replace("\"", "\\\"")}\"");
        if (McpConfigPath is not null)
            sb.Append($" --mcp-config \"{McpConfigPath}\"");

        return sb.ToString().Trim();
    }

    /// <summary>Convert this template to ClaudeOptions for launching a session.</summary>
    public ClaudeOptions ToClaudeOptions()
    {
        return new ClaudeOptions
        {
            Model = Model,
            FallbackModel = FallbackModel,
            MaxTurns = MaxTurns,
            MaxBudgetUsd = MaxBudgetUsd,
            SystemPrompt = SystemPrompt,
            AppendSystemPrompt = AppendSystemPrompt,
            PermissionMode = PermissionMode,
            SkipPermissions = SkipPermissions,
            Tools = Tools,
            AllowedTools = AllowedTools,
            DisallowedTools = DisallowedTools,
            McpConfig = McpConfigPath,
        };
    }
}
