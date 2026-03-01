using Microsoft.SemanticKernel;

namespace CCComputer.Agent.Filters;

/// <summary>
/// Semantic Kernel filter that intercepts all function/tool invocations.
/// Raises events before and after each tool call for logging and UI updates.
/// </summary>
public class ToolLoggingFilter : IFunctionInvocationFilter
{
    private readonly ActivityLogger? _logger;
    private readonly Action<string, string?>? _onToolCall;
    private readonly Action<string, bool, string>? _onToolResult;

    public ToolLoggingFilter(
        ActivityLogger? logger = null,
        Action<string, string?>? onToolCall = null,
        Action<string, bool, string>? onToolResult = null)
    {
        _logger = logger;
        _onToolCall = onToolCall;
        _onToolResult = onToolResult;
    }

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        var toolName = $"{context.Function.PluginName}.{context.Function.Name}";
        var arguments = FormatArguments(context.Arguments);

        // Log tool call start
        _logger?.LogToolCall(toolName, context.Arguments);
        _onToolCall?.Invoke(toolName, arguments);

        try
        {
            // Execute the tool
            await next(context);

            // Get result
            var result = context.Result?.ToString() ?? "(no result)";
            var success = true;

            // Log tool result
            _logger?.LogToolResult(toolName, success, result);
            _onToolResult?.Invoke(toolName, success, result);
        }
        catch (Exception ex)
        {
            // Log error
            var errorMessage = $"Error: {ex.Message}";
            _logger?.LogToolResult(toolName, false, errorMessage);
            _logger?.LogError($"Tool {toolName} failed", ex);
            _onToolResult?.Invoke(toolName, false, errorMessage);

            throw;
        }
    }

    private static string? FormatArguments(KernelArguments? args)
    {
        if (args == null || args.Count == 0)
            return null;

        var parts = new List<string>();
        foreach (var kvp in args)
        {
            var value = kvp.Value?.ToString() ?? "null";
            // Truncate long values
            if (value.Length > 100)
                value = value[..100] + "...";
            parts.Add($"{kvp.Key}={value}");
        }

        return string.Join(", ", parts);
    }
}
