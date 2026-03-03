using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Generates friendly chat summaries using Claude Haiku.
/// Static methods following the SessionSummarizer pattern.
/// </summary>
public static class SimpleChatSummarizer
{
    private const int MaxTerminalChars = 4000;

    /// <summary>
    /// Summarize what Claude is doing right now (periodic progress update).
    /// Called every ~30 seconds while ActivityState is Working.
    /// </summary>
    public static async Task<string> SummarizeProgressAsync(
        ClaudeClient client, string terminalText, CancellationToken ct = default)
    {
        FileLog.Write($"[SimpleChatSummarizer] SummarizeProgressAsync: terminalTextLen={terminalText.Length}");

        var truncated = TruncateToEnd(terminalText, MaxTerminalChars);
        if (string.IsNullOrWhiteSpace(truncated))
            return "Working...";

        var prompt = $"Here is the latest terminal output from a Claude Code session:\n\n{truncated}\n\nWhat is Claude doing right now? One sentence, max 120 characters.";

        var response = await client.ChatAsync(prompt, new ClaudeOptions
        {
            Model = "haiku",
            MaxTurns = 1,
            SkipPermissions = true,
            SystemPrompt = "You summarize terminal activity in a friendly, non-technical way. "
                         + "One sentence, max 120 characters. No markdown. No code. "
                         + "Example: 'Reading the authentication module to understand how login works.'",
        }, ct);

        var summary = response.Result.Trim();
        if (summary.Length > 120)
            summary = summary[..117] + "...";

        FileLog.Write($"[SimpleChatSummarizer] SummarizeProgressAsync completed: summaryLen={summary.Length}");
        return summary;
    }

    /// <summary>
    /// Summarize what Claude accomplished at the end of a turn.
    /// Called when the Stop hook event fires.
    /// </summary>
    public static async Task<string> SummarizeCompletionAsync(
        ClaudeClient client, TurnData turn, string terminalText, CancellationToken ct = default)
    {
        FileLog.Write($"[SimpleChatSummarizer] SummarizeCompletionAsync: promptLen={turn.UserPrompt.Length}, tools={turn.ToolsUsed.Count}");

        // Simple prompt with no tool use -- show directly
        if (turn.ToolsUsed.Count == 0 && turn.UserPrompt.Length < 50)
            return $"Done: {turn.UserPrompt}";

        var prompt = BuildCompletionPrompt(turn, terminalText);

        var response = await client.ChatAsync(prompt, new ClaudeOptions
        {
            Model = "haiku",
            MaxTurns = 1,
            SkipPermissions = true,
            SystemPrompt = "You extract Claude's response from noisy terminal output. "
                         + "Show the user what Claude actually said, preserving useful content.\n\n"
                         + "Rules:\n"
                         + "- Extract and reproduce Claude's actual response text faithfully\n"
                         + "- Preserve lists, tables, and structured content -- do NOT flatten into prose\n"
                         + "- Remove tool usage noise (Read file, Edit file, Bash commands, etc.)\n"
                         + "- Remove file path listings and tool permission blocks\n"
                         + "- Remove progress indicators and status lines\n"
                         + "- Keep the response concise but complete -- no useful details lost\n"
                         + "- Format using markdown: pipe tables for tabular data, # for headers, - for bullet lists, backticks for code\n"
                         + "- Maximum 1000 characters",
        }, ct);

        var summary = response.Result.Trim();
        if (summary.Length > 1500)
            summary = summary[..1497] + "...";

        FileLog.Write($"[SimpleChatSummarizer] SummarizeCompletionAsync completed: summaryLen={summary.Length}");
        return summary;
    }

    /// <summary>Build the prompt for progress summarization (exposed for testing).</summary>
    internal static string BuildProgressPrompt(string terminalText)
    {
        var truncated = TruncateToEnd(terminalText, MaxTerminalChars);
        return $"Here is the latest terminal output from a Claude Code session:\n\n{truncated}\n\nWhat is Claude doing right now? One sentence, max 120 characters.";
    }

    /// <summary>Build the prompt for completion summarization (exposed for testing).</summary>
    internal static string BuildCompletionPrompt(TurnData turn, string terminalText)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"The user asked: {Truncate(turn.UserPrompt, 500)}");
        sb.AppendLine();
        sb.AppendLine("Extract Claude's actual response from this terminal output.");
        sb.AppendLine("Remove all tool-use noise (file reads, edits, bash commands, permission requests).");
        sb.AppendLine("Keep lists, tables, and explanations intact.");

        var truncatedTerminal = TruncateToEnd(terminalText, MaxTerminalChars);
        if (!string.IsNullOrWhiteSpace(truncatedTerminal))
        {
            sb.AppendLine();
            sb.AppendLine("Terminal output:");
            sb.AppendLine(truncatedTerminal);
        }

        return sb.ToString();
    }

    private static string TruncateToEnd(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;
        return text[^maxLength..];
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }
}
