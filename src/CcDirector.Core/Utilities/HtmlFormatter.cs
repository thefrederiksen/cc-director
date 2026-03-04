using System.Text;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Converts plain text email bodies to proper HTML for email delivery.
/// Plain text with \n newlines must be converted to HTML paragraph/break tags
/// before sending via --html flag, otherwise email clients ignore the newlines.
/// </summary>
public static class HtmlFormatter
{
    /// <summary>
    /// Converts a plain text body to HTML with proper paragraph and line break tags.
    /// If the body already contains HTML block-level tags, it is returned as-is.
    /// </summary>
    /// <remarks>
    /// No logging here -- this is also called from WPF data-binding converters
    /// on the UI thread, where file I/O would block rendering.
    /// </remarks>
    public static string ConvertPlainTextToHtml(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        // If body already contains HTML block tags, assume it's already formatted
        if (ContainsHtmlBlockTags(body))
            return body;

        // Normalize line endings to \n
        var normalized = body.Replace("\r\n", "\n").Replace("\r", "\n");

        // Split on double newlines for paragraphs
        var paragraphs = normalized.Split(["\n\n"], StringSplitOptions.None);

        var sb = new StringBuilder();
        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();
            if (trimmed.Length == 0)
                continue;

            // Convert single newlines within a paragraph to <br>
            var withBreaks = trimmed.Replace("\n", "<br>\n");
            sb.AppendLine($"<p>{withBreaks}</p>");
        }

        return sb.ToString().TrimEnd();
    }

    private static bool ContainsHtmlBlockTags(string text)
    {
        return text.Contains("<p>", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<p ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<br", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<div", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<table", StringComparison.OrdinalIgnoreCase);
    }
}
