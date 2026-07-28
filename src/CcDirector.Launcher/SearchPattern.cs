using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Launcher;

/// <summary>
/// The one place a caller's search text is turned into a match test, shared by the application catalogue and
/// the file search so the two cannot answer the same query differently.
///
/// There are two behaviours, chosen by the text itself rather than by a separate mode argument:
///
///   * Text containing * or ? is a wildcard pattern and must match the WHOLE name - "*.pptx" finds
///     presentations, and does not also match "pptx-notes.txt".
///   * Text with no wildcard is a substring and matches ANYWHERE in the name - "budget" finds
///     "Q3-budget-final.xlsx".
///
/// Choosing on the text is what a person already expects from a search box, and it means the common case
/// needs no flag at all. Matching is case-insensitive everywhere, because the two operating systems this
/// launcher runs on disagree about filename case and a search that answered differently on each would be
/// worse than one that is merely permissive.
/// </summary>
public sealed class SearchPattern
{
    private readonly string? _substring;
    private readonly Regex? _wildcard;

    /// <summary>True when the pattern matches everything, which is what an empty search means.</summary>
    public bool MatchesEverything { get; }

    private SearchPattern(string? substring, Regex? wildcard, bool matchesEverything)
    {
        _substring = substring;
        _wildcard = wildcard;
        MatchesEverything = matchesEverything;
    }

    /// <summary>
    /// Build a matcher for the given search text. Empty or whitespace text yields a matcher that accepts
    /// everything, so "list them all" needs no special case at the call site.
    /// </summary>
    public static SearchPattern Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchPattern(null, null, matchesEverything: true);

        var trimmed = query.Trim();
        if (trimmed.IndexOfAny(new[] { '*', '?' }) < 0)
            return new SearchPattern(trimmed, null, matchesEverything: false);

        return new SearchPattern(null, new Regex(ToRegex(trimmed),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), matchesEverything: false);
    }

    /// <summary>Test one name - a filename or an application display name - against the pattern.</summary>
    public bool IsMatch(string name)
    {
        if (MatchesEverything) return true;
        if (_wildcard is not null) return _wildcard.IsMatch(name);
        return name.Contains(_substring!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Translate a wildcard pattern into an anchored regular expression. Every character that is not a
    /// wildcard is escaped, so a pattern containing regular-expression punctuation - a dot in "*.pptx", a
    /// plus in "notes+.txt" - is treated as the literal text a person meant, never as syntax.
    /// </summary>
    private static string ToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        foreach (var character in pattern)
        {
            switch (character)
            {
                case '*': builder.Append(".*"); break;
                case '?': builder.Append('.'); break;
                default: builder.Append(Regex.Escape(character.ToString())); break;
            }
        }
        return builder.Append('$').ToString();
    }
}
