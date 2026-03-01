using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace CommunicationManager.Converters;

/// <summary>
/// Converts HTML content to plain text with proper paragraph breaks for display.
/// Handles simple HTML tags like p, br, ul, li without requiring a full HTML renderer.
/// </summary>
public partial class HtmlContentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string html || string.IsNullOrEmpty(html))
            return value;

        // Check if content contains HTML tags
        if (!html.Contains('<'))
            return html;

        string result = html;

        // Handle list items: <li>text</li> -> "- text\n"
        result = LiTagRegex().Replace(result, "- $1\n");

        // Remove ul/ol tags
        result = UlOlTagRegex().Replace(result, "\n");

        // Handle <br> and <br/> tags -> newline
        result = BrTagRegex().Replace(result, "\n");

        // Handle </p> -> double newline (paragraph break)
        result = ClosingPTagRegex().Replace(result, "\n\n");

        // Remove opening <p> tags
        result = OpeningPTagRegex().Replace(result, "");

        // Handle <strong> and <b> - just remove tags, keep content
        result = StrongBTagRegex().Replace(result, "$1");

        // Handle <em> and <i> - just remove tags, keep content
        result = EmITagRegex().Replace(result, "$1");

        // Remove any remaining HTML tags
        result = AnyHtmlTagRegex().Replace(result, "");

        // Decode common HTML entities
        result = result
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'");

        // Clean up excessive whitespace
        result = MultipleNewlinesRegex().Replace(result, "\n\n");
        result = result.Trim();

        return result;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    // Regex patterns using source generators for performance
    [GeneratedRegex(@"<li[^>]*>(.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LiTagRegex();

    [GeneratedRegex(@"</?(?:ul|ol)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex UlOlTagRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrTagRegex();

    [GeneratedRegex(@"</p>", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingPTagRegex();

    [GeneratedRegex(@"<p[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex OpeningPTagRegex();

    [GeneratedRegex(@"<(?:strong|b)[^>]*>(.*?)</(?:strong|b)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StrongBTagRegex();

    [GeneratedRegex(@"<(?:em|i)[^>]*>(.*?)</(?:em|i)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex EmITagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyHtmlTagRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleNewlinesRegex();
}
