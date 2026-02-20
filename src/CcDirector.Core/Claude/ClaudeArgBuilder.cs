using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Builds CLI argument strings from ClaudeOptions.
/// Handles Windows-specific quoting (double quotes only, never single quotes).
/// </summary>
internal static class ClaudeArgBuilder
{
    /// <summary>
    /// Build arguments for a one-shot chat invocation.
    /// Always includes: -p --output-format json
    /// </summary>
    public static string BuildChatArgs(ClaudeOptions? options)
    {
        var sb = new StringBuilder("-p --output-format json");
        AppendCommonArgs(sb, options);
        return sb.ToString();
    }

    /// <summary>
    /// Build arguments for a structured output invocation with JSON schema.
    /// Always includes: -p --output-format json --json-schema "..."
    /// </summary>
    public static string BuildStructuredArgs(Type resultType, ClaudeOptions? options)
    {
        var sb = new StringBuilder("-p --output-format json");

        var schema = GenerateJsonSchema(resultType);
        var escaped = EscapeJsonForWindowsArg(schema);
        sb.Append($" --json-schema \"{escaped}\"");

        AppendCommonArgs(sb, options);
        return sb.ToString();
    }

    /// <summary>
    /// Build arguments for a streaming invocation.
    /// Always includes: -p --output-format stream-json --verbose
    /// </summary>
    public static string BuildStreamArgs(ClaudeOptions? options)
    {
        var sb = new StringBuilder("-p --output-format stream-json --verbose");
        AppendCommonArgs(sb, options);
        return sb.ToString();
    }

    /// <summary>
    /// Build arguments for a resume invocation.
    /// Always includes: -p --output-format json --resume {sessionId}
    /// </summary>
    public static string BuildResumeArgs(string sessionId, ClaudeOptions? options)
    {
        var sb = new StringBuilder($"-p --output-format json --resume {sessionId}");
        AppendCommonArgs(sb, options);
        return sb.ToString();
    }

    private static void AppendCommonArgs(StringBuilder sb, ClaudeOptions? options)
    {
        if (options is null)
            return;

        AppendModelAndExecutionArgs(sb, options);
        AppendPromptArgs(sb, options);
        AppendToolsAndPermissionArgs(sb, options);
        AppendSessionArgs(sb, options);
        AppendContextAndAdvancedArgs(sb, options);
    }

    private static void AppendModelAndExecutionArgs(StringBuilder sb, ClaudeOptions options)
    {
        if (options.Model is not null)
            sb.Append($" --model {options.Model}");
        if (options.FallbackModel is not null)
            sb.Append($" --fallback-model {options.FallbackModel}");
        if (options.MaxTurns.HasValue)
            sb.Append($" --max-turns {options.MaxTurns.Value}");
        if (options.MaxBudgetUsd.HasValue)
            sb.Append($" --max-budget-usd {options.MaxBudgetUsd.Value}");
    }

    private static void AppendPromptArgs(StringBuilder sb, ClaudeOptions options)
    {
        if (options.SystemPrompt is not null)
            sb.Append($" --system-prompt \"{EscapeDoubleQuotes(options.SystemPrompt)}\"");
        if (options.AppendSystemPrompt is not null)
            sb.Append($" --append-system-prompt \"{EscapeDoubleQuotes(options.AppendSystemPrompt)}\"");
        if (options.SystemPromptFile is not null)
            sb.Append($" --system-prompt-file \"{options.SystemPromptFile}\"");
        if (options.AppendSystemPromptFile is not null)
            sb.Append($" --append-system-prompt-file \"{options.AppendSystemPromptFile}\"");
    }

    private static void AppendToolsAndPermissionArgs(StringBuilder sb, ClaudeOptions options)
    {
        if (options.SkipPermissions)
            sb.Append(" --dangerously-skip-permissions");
        if (options.PermissionMode is not null)
            sb.Append($" --permission-mode {options.PermissionMode}");
        if (options.Tools is not null)
            sb.Append($" --tools \"{EscapeDoubleQuotes(options.Tools)}\"");
        if (options.AllowedTools is not null)
            sb.Append($" --allowedTools \"{EscapeDoubleQuotes(options.AllowedTools)}\"");
        if (options.DisallowedTools is not null)
            sb.Append($" --disallowedTools \"{EscapeDoubleQuotes(options.DisallowedTools)}\"");
    }

    private static void AppendSessionArgs(StringBuilder sb, ClaudeOptions options)
    {
        if (options.SessionId is not null)
            sb.Append($" --session-id {options.SessionId}");
        if (options.Continue)
            sb.Append(" --continue");
        if (options.ForkSession)
            sb.Append(" --fork-session");
        if (options.NoSessionPersistence)
            sb.Append(" --no-session-persistence");
    }

    private static void AppendContextAndAdvancedArgs(StringBuilder sb, ClaudeOptions options)
    {
        if (options.AddDirs is not null)
        {
            foreach (var dir in options.AddDirs)
                sb.Append($" --add-dir \"{dir}\"");
        }

        if (options.McpConfig is not null)
            sb.Append($" --mcp-config \"{options.McpConfig}\"");
        if (options.Agents is not null)
            sb.Append($" --agents \"{EscapeJsonForWindowsArg(options.Agents)}\"");
        if (options.Settings is not null)
            sb.Append($" --settings \"{options.Settings}\"");
    }

    /// <summary>
    /// Generate a JSON schema string from a C# type using .NET 10 JsonSchemaExporter.
    /// </summary>
    internal static string GenerateJsonSchema(Type type)
    {
        FileLog.Write($"[ClaudeArgBuilder] GenerateJsonSchema: type={type.Name}");

        var node = JsonSerializerOptions.Default.GetJsonSchemaAsNode(type);
        var schema = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        FileLog.Write($"[ClaudeArgBuilder] GenerateJsonSchema: schema={schema}");
        return schema;
    }

    /// <summary>
    /// Escape a JSON string for use as a Windows command-line argument inside double quotes.
    /// Each literal " in the JSON becomes \" in the argument string.
    /// </summary>
    internal static string EscapeJsonForWindowsArg(string json)
    {
        return json.Replace("\"", "\\\"");
    }

    /// <summary>
    /// Escape double quotes in a string for use inside a double-quoted CLI argument.
    /// </summary>
    private static string EscapeDoubleQuotes(string value)
    {
        return value.Replace("\"", "\\\"");
    }
}
