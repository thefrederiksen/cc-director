using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Dictation.Models;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The rendered "words worth adding to your dictionary" block for the daily report email (issue #2074, mockup
/// screen 5), plus the batch fingerprint the send cadence is keyed on.
///
/// PURE AND STATIC, like <see cref="CcDirector.Core.Dictation.MistranscriptionMiner"/>: it takes the
/// suggestions and a link and returns text. It reads no clock, no store, and no tenant - the caller has already
/// resolved all of that - so the exact bytes the owner will receive can be asserted in a unit test.
///
/// WHAT THE BLOCK IS FOR, and therefore what it deliberately is NOT: the email is a DOORBELL, not a workbench.
/// It carries the top <see cref="TopTerms"/> terms with their evidence, a count of the rest, and ONE link to
/// the Dictionary page where the real approve flow lives. There is no accept action in the email, because
/// nothing may be added to a person's dictionary by their clicking a link in a message - approval happens on
/// the page, against the live evidence.
///
/// It is also a BLOCK, not an email: no subject, no greeting, no signature, no sender. It is meant to be
/// dropped into the existing daily report between that report's own sections, which is what keeps this feature
/// from becoming a second email stream with its own unsubscribe to manage (mockup screen 5, note 1).
///
/// ESCAPING: every term and heard-variant is user speech that reached us through a transcription model, so all
/// of it is HTML-escaped on the way into the markup. The plain-text rendering is the same content with no
/// markup at all, for the text part of a multipart message.
/// </summary>
public static class SuggestionEmailBlock
{
    /// <summary>How many terms are shown in full. The rest are summarized as a "+ N more" line.</summary>
    public const int TopTerms = 3;

    /// <summary>How many heard-variants are listed for one term before the list is truncated.</summary>
    public const int VariantsPerTerm = 3;

    /// <summary>The Cockpit path the block's one link points at - where the approve flow lives.</summary>
    public const string DictionaryPath = "/dictionary";

    /// <summary>One rendered block: the same content as markup and as plain text, plus the heading so a caller
    /// can log or preview what was produced without parsing the markup back apart.</summary>
    /// <param name="Heading">The block's own heading line, e.g. "Dictation: 4 words worth adding to your dictionary".</param>
    /// <param name="Html">The block as HTML, ready to drop between the daily report's other sections.</param>
    /// <param name="Text">The same block as plain text, for the text part of the message.</param>
    public sealed record Rendered(string Heading, string Html, string Text);

    /// <summary>
    /// A stable fingerprint of a batch of suggestions - the identity the "mention a batch at most twice"
    /// cadence is keyed on (<see cref="Settings.DictationEmailCadenceState"/>).
    ///
    /// Built from the TERMS ONLY, normalized and sorted: a batch is "the same batch" when it is about the same
    /// words. Deliberately NOT sensitive to the counts, which tick up every time the user speaks - folding
    /// those in would make every send a new batch and the cadence would never go quiet, which is the one
    /// failure this whole mechanism exists to prevent. Sorting means the miner's ranking can reshuffle without
    /// inventing a new batch.
    ///
    /// An empty batch fingerprints as the empty string; there is nothing to mention, so it never reaches the
    /// cadence at all.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<MistranscriptionSuggestion> suggestions)
    {
        if (suggestions is null) throw new ArgumentNullException(nameof(suggestions));
        if (suggestions.Count == 0) return "";

        var terms = suggestions
            .Select(s => Normalize(s.Term))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal);

        var joined = string.Join("\n", terms);
        if (joined.Length == 0) return "";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Render the block for a batch of suggestions.
    /// </summary>
    /// <param name="suggestions">The pending suggestions, in the miner's ranked order. Required, non-empty -
    /// an empty batch has no block, and the caller decides that before rendering rather than being handed an
    /// empty block that looks like a rendering bug.</param>
    /// <param name="dictionaryUrl">The absolute link to the Dictionary page. When null (self-host with no
    /// reachable public address), the block renders WITHOUT a link and names the page instead - it never emits
    /// a machine-local address, which would be a dead link in a message read on a phone.</param>
    /// <exception cref="ArgumentException">The suggestion list is empty.</exception>
    public static Rendered Render(IReadOnlyList<MistranscriptionSuggestion> suggestions, string? dictionaryUrl)
    {
        if (suggestions is null) throw new ArgumentNullException(nameof(suggestions));
        if (suggestions.Count == 0)
            throw new ArgumentException("An empty batch has no block; check the count before rendering.", nameof(suggestions));

        var heading = $"Dictation: {Plural(suggestions.Count, "word", "words")} worth adding to your dictionary";
        const string lede = "Your dictation keeps getting these wrong. Review takes one press - nothing is added until you approve it.";

        var shown = suggestions.Take(TopTerms).ToList();
        var rest = suggestions.Skip(TopTerms).ToList();

        var html = new StringBuilder();
        html.Append("<div style=\"border:1px solid #d8dee8;border-left:3px solid #c8102e;border-radius:6px;padding:14px 16px;margin:16px 0;\">");
        html.Append("<h3 style=\"margin:0 0 4px;font-size:15px;\">").Append(Escape(heading)).Append("</h3>");
        html.Append("<p style=\"margin:0 0 10px;font-size:13px;color:#5a6472;\">").Append(Escape(lede)).Append("</p>");
        foreach (var s in shown)
            html.Append(RowHtml(s));
        if (rest.Count > 0)
            html.Append(MoreRowHtml(rest));
        html.Append("<p style=\"margin:14px 0 2px;\">");
        html.Append(dictionaryUrl is null
            ? "<span style=\"font-size:13px;font-weight:600;\">Review and add on the Dictionary page in your Cockpit</span>"
            : $"<a href=\"{Escape(dictionaryUrl)}\" style=\"font-size:13px;font-weight:600;\">Review and add in Dictionary</a>");
        html.Append("</p></div>");

        var text = new StringBuilder();
        text.Append(heading).Append('\n');
        text.Append(lede).Append("\n\n");
        foreach (var s in shown)
            text.Append(RowText(s)).Append('\n');
        if (rest.Count > 0)
            text.Append(MoreRowText(rest)).Append('\n');
        text.Append('\n');
        text.Append(dictionaryUrl is null
            ? "Review and add on the Dictionary page in your Cockpit."
            : $"Review and add in Dictionary: {dictionaryUrl}");

        return new Rendered(heading, html.ToString(), text.ToString());
    }

    /// <summary>The one-sentence footer naming the setting that controls this block, so the loop back to the
    /// Settings screen is closed inside the message itself (mockup screen 5, note 3). Returned separately from
    /// the block because it belongs at the foot of the whole report, not inside the block.</summary>
    public const string Footer =
        "You are getting suggestion notes because \"Suggestions in my daily email\" is on in Settings. " +
        "Turn it off there any time.";

    // ---- rows -------------------------------------------------------------------------------------------

    private static string RowHtml(MistranscriptionSuggestion s)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"display:flex;justify-content:space-between;gap:12px;padding:6px 0;border-top:1px solid #eef1f5;\">");
        sb.Append("<div><span style=\"font-weight:600;font-size:13px;\">").Append(Escape(s.Term)).Append("</span>");
        var heard = HeardList(s);
        if (heard.Length > 0)
            sb.Append("<div style=\"font-size:12px;color:#5a6472;\">heard as ").Append(Escape(heard)).Append("</div>");
        sb.Append("</div>");
        sb.Append("<span style=\"font-size:12px;color:#5a6472;white-space:nowrap;\">").Append(Escape(WrongOf(s))).Append("</span>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string RowText(MistranscriptionSuggestion s)
    {
        var heard = HeardList(s);
        var evidence = heard.Length > 0 ? $" (heard as {heard})" : "";
        return $"  {s.Term}{evidence} - {WrongOf(s)}";
    }

    // The "+ N more" line names the remaining terms rather than only counting them: a bare "+ 1 more" tells the
    // reader nothing about whether it is worth opening the page, and the terms are short.
    private static string MoreRowHtml(IReadOnlyList<MistranscriptionSuggestion> rest)
        => "<div style=\"display:flex;justify-content:space-between;gap:12px;padding:6px 0;border-top:1px solid #eef1f5;\">"
           + "<div><span style=\"font-weight:600;font-size:13px;\">+ " + rest.Count + " more</span>"
           + "<div style=\"font-size:12px;color:#5a6472;\">" + Escape(string.Join(", ", rest.Select(r => r.Term))) + "</div>"
           + "</div></div>";

    private static string MoreRowText(IReadOnlyList<MistranscriptionSuggestion> rest)
        => $"  + {rest.Count} more: {string.Join(", ", rest.Select(r => r.Term))}";

    /// <summary>The heard-variants for one term, most frequent first, capped at <see cref="VariantsPerTerm"/>.</summary>
    private static string HeardList(MistranscriptionSuggestion s)
        => string.Join(", ", s.Variants
            .OrderByDescending(v => v.Count)
            .ThenBy(v => v.Heard, StringComparer.Ordinal)
            .Take(VariantsPerTerm)
            .Select(v => v.Heard));

    /// <summary>The evidence line: "wrong 53 of 97 times". Thousands are grouped so a large count stays
    /// readable in a message.</summary>
    private static string WrongOf(MistranscriptionSuggestion s)
        => $"wrong {s.WrongCount.ToString("N0", CultureInfo.InvariantCulture)} of "
           + $"{s.TotalCount.ToString("N0", CultureInfo.InvariantCulture)} times";

    private static string Plural(int n, string one, string many)
        => $"{n.ToString("N0", CultureInfo.InvariantCulture)} {(n == 1 ? one : many)}";

    /// <summary>Letters and digits only, lower-cased - the same normalization the suggestion service uses to
    /// match a term, so the fingerprint and the term lookup agree on what "the same word" means.</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string Escape(string s)
        => s.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
