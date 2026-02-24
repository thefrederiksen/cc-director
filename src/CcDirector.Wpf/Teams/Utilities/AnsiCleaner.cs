using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Wpf.Teams.Utilities;

/// <summary>
/// Utility for stripping ANSI escape sequences from terminal text.
/// </summary>
public static class AnsiCleaner
{
    // Timeout to prevent catastrophic backtracking on malicious input
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    // Match ANSI escape sequences:
    // - CSI sequences: ESC [ ... final byte
    // - OSC sequences: ESC ] ... ST
    // - Simple escape sequences: ESC single char
    private static readonly Regex AnsiPattern = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~]|\][^\x07]*\x07|\][^\x1B]*\x1B\\)",
        RegexOptions.Compiled,
        RegexTimeout);

    // Additional cleanup patterns for terminal artifacts
    private static readonly Regex ControlCharsPattern = new(
        @"[\x00-\x08\x0B\x0C\x0E-\x1F]",
        RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>
    /// Remove all ANSI escape sequences from text.
    /// </summary>
    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Remove ANSI sequences
        var cleaned = AnsiPattern.Replace(text, string.Empty);

        // Remove remaining control characters (except \n, \r, \t)
        cleaned = ControlCharsPattern.Replace(cleaned, string.Empty);

        return cleaned;
    }

    /// <summary>
    /// Clean text and get the last N lines.
    /// </summary>
    public static string GetLastLines(string text, int lineCount)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var cleaned = Clean(text);
        var lines = cleaned.Split('\n');

        if (lines.Length <= lineCount)
            return cleaned;

        var lastLines = lines.Skip(lines.Length - lineCount);
        return string.Join('\n', lastLines);
    }

    /// <summary>
    /// Clean text and truncate to max length with ellipsis.
    /// </summary>
    public static string CleanAndTruncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var cleaned = Clean(text);

        if (cleaned.Length <= maxLength)
            return cleaned;

        return cleaned.Substring(0, maxLength - 3) + "...";
    }
}
