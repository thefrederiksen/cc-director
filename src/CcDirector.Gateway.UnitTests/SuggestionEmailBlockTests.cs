using CcDirector.Core.Dictation.Models;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The daily-email suggestions block (issue #2074, mockup screen 5) and the batch fingerprint the send cadence
/// is keyed on. The renderer is pure, so these assert the EXACT text the owner would receive rather than
/// paraphrasing it - the whole point of a pure renderer is that the message is testable before it is sent.
/// </summary>
public sealed class SuggestionEmailBlockTests
{
    private static MistranscriptionSuggestion Sug(string term, int wrong, int total, params (string heard, int count)[] variants)
        => new(term, variants.Select(v => new MistranscriptionVariant(v.heard, v.count)).ToList(), wrong, total);

    private static IReadOnlyList<MistranscriptionSuggestion> FourTerms() => new[]
    {
        Sug("mindzie", 53, 97, ("Mindsee", 25), ("Mindsy", 16), ("Mindzee", 12)),
        Sug("ConPty", 80, 144, ("Con-TY", 55), ("ConTY", 25)),
        Sug("Frederiksen", 98, 364, ("Fredriksson", 60), ("Fredrickson", 38)),
        Sug("Kubernetes", 4, 20, ("Cooper Netties", 4)),
    };

    // ---- the fingerprint ------------------------------------------------------------------------------

    /// <summary>The same set of terms fingerprints the same however the miner happened to rank them. Ranking
    /// shifts as counts move; a fingerprint that shifted with it would mint a "new batch" on every send and the
    /// cadence would never go quiet, which is the exact failure it exists to prevent.</summary>
    [Fact]
    public void Fingerprint_IgnoresOrder()
    {
        var forward = FourTerms();
        var reversed = forward.Reverse().ToList();

        Assert.Equal(SuggestionEmailBlock.Fingerprint(forward), SuggestionEmailBlock.Fingerprint(reversed));
    }

    /// <summary>Counts tick up every time the user speaks. The batch is about WHICH WORDS, not how often, so a
    /// changed count is the same batch.</summary>
    [Fact]
    public void Fingerprint_IgnoresCounts()
    {
        var a = new[] { Sug("mindzie", 3, 10, ("Mindsee", 3)) };
        var b = new[] { Sug("mindzie", 900, 4000, ("Mindsee", 900), ("Mindsy", 12)) };

        Assert.Equal(SuggestionEmailBlock.Fingerprint(a), SuggestionEmailBlock.Fingerprint(b));
    }

    /// <summary>A different word IS a different batch - which is what earns a new set of mentions.</summary>
    [Fact]
    public void Fingerprint_ChangesWhenATermChanges()
    {
        var a = new[] { Sug("mindzie", 3, 10, ("Mindsee", 3)) };
        var b = new[] { Sug("ConPty", 3, 10, ("Con-TY", 3)) };

        Assert.NotEqual(SuggestionEmailBlock.Fingerprint(a), SuggestionEmailBlock.Fingerprint(b));
    }

    /// <summary>Adding a term changes the batch, so a genuinely new word is never silenced by an earlier
    /// batch's spent mentions.</summary>
    [Fact]
    public void Fingerprint_ChangesWhenATermIsAdded()
    {
        var one = new[] { Sug("mindzie", 3, 10, ("Mindsee", 3)) };
        var two = one.Concat(new[] { Sug("ConPty", 3, 10, ("Con-TY", 3)) }).ToList();

        Assert.NotEqual(SuggestionEmailBlock.Fingerprint(one), SuggestionEmailBlock.Fingerprint(two));
    }

    /// <summary>Casing and punctuation are not identity: the miner's canonical spelling can settle without
    /// minting a new batch.</summary>
    [Fact]
    public void Fingerprint_IsCaseAndPunctuationInsensitive()
    {
        var a = new[] { Sug("ConPty", 3, 10, ("Con-TY", 3)) };
        var b = new[] { Sug("con-pty.", 3, 10, ("Con-TY", 3)) };

        Assert.Equal(SuggestionEmailBlock.Fingerprint(a), SuggestionEmailBlock.Fingerprint(b));
    }

    [Fact]
    public void Fingerprint_EmptyBatch_IsEmpty()
        => Assert.Equal("", SuggestionEmailBlock.Fingerprint(Array.Empty<MistranscriptionSuggestion>()));

    // ---- the rendered block ---------------------------------------------------------------------------

    /// <summary>The heading counts ALL pending terms, not the three that are shown - the reader is being told
    /// how much is waiting, and the "+ 1 more" line is what reconciles the two numbers.</summary>
    [Fact]
    public void Render_HeadingCountsEveryPendingTerm()
    {
        var r = SuggestionEmailBlock.Render(FourTerms(), "https://gw.example.com/dictionary");

        Assert.Equal("Dictation: 4 words worth adding to your dictionary", r.Heading);
        Assert.Contains(r.Heading, r.Html, StringComparison.Ordinal);
        Assert.StartsWith(r.Heading, r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OneTerm_UsesTheSingular()
    {
        var r = SuggestionEmailBlock.Render(new[] { Sug("mindzie", 3, 10, ("Mindsee", 3)) }, null);

        Assert.Equal("Dictation: 1 word worth adding to your dictionary", r.Heading);
    }

    /// <summary>Top three in full, the rest summarized. The email is a doorbell, not a workbench.</summary>
    [Fact]
    public void Render_ShowsTopThreeAndSummarizesTheRest()
    {
        var r = SuggestionEmailBlock.Render(FourTerms(), "https://gw.example.com/dictionary");

        Assert.Contains("mindzie", r.Text, StringComparison.Ordinal);
        Assert.Contains("ConPty", r.Text, StringComparison.Ordinal);
        Assert.Contains("Frederiksen", r.Text, StringComparison.Ordinal);
        // The fourth term is NOT given a row of its own - it appears only on the summary line, named.
        Assert.Contains("+ 1 more: Kubernetes", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Cooper Netties", r.Text, StringComparison.Ordinal);
    }

    /// <summary>The evidence is the reason to care, so it travels with the term: what was heard, and how often
    /// the word was got wrong out of how many times it was said.</summary>
    [Fact]
    public void Render_CarriesTheEvidenceForEachShownTerm()
    {
        var r = SuggestionEmailBlock.Render(FourTerms(), null);

        Assert.Contains("(heard as Mindsee, Mindsy, Mindzee)", r.Text, StringComparison.Ordinal);
        Assert.Contains("wrong 53 of 97 times", r.Text, StringComparison.Ordinal);
        Assert.Contains("wrong 98 of 364 times", r.Text, StringComparison.Ordinal);
    }

    /// <summary>Only the most frequent few misheardings are listed - a term the model gets wrong a dozen
    /// different ways would otherwise push the whole block off a phone screen.</summary>
    [Fact]
    public void Render_CapsTheVariantsListedPerTerm()
    {
        var many = new[]
        {
            Sug("mindzie", 30, 60,
                ("Mindsee", 12), ("Mindsy", 8), ("Mindzee", 6), ("Mind Z", 3), ("Mind Sea", 1)),
        };

        var r = SuggestionEmailBlock.Render(many, null);

        Assert.Contains("(heard as Mindsee, Mindsy, Mindzee)", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Mind Sea", r.Text, StringComparison.Ordinal);
    }

    /// <summary>Large counts are grouped so the evidence stays readable in a message.</summary>
    [Fact]
    public void Render_GroupsLargeCounts()
    {
        var r = SuggestionEmailBlock.Render(new[] { Sug("mindzie", 1234, 56789, ("Mindsee", 1234)) }, null);

        Assert.Contains("wrong 1,234 of 56,789 times", r.Text, StringComparison.Ordinal);
    }

    /// <summary>The one action in the block is a link to the page where the real approve flow lives - never an
    /// accept action in the message itself.</summary>
    [Fact]
    public void Render_LinksToTheDictionaryPage()
    {
        var r = SuggestionEmailBlock.Render(FourTerms(), "https://gw.example.com/dictionary");

        Assert.Contains("href=\"https://gw.example.com/dictionary\"", r.Html, StringComparison.Ordinal);
        Assert.Contains("Review and add in Dictionary: https://gw.example.com/dictionary", r.Text, StringComparison.Ordinal);
    }

    /// <summary>With no publicly reachable address there is no honest link, so the block NAMES the page instead
    /// of emitting a dead one. A localhost link in a message read on a phone is worse than no link.</summary>
    [Fact]
    public void Render_NoPublicAddress_NamesThePageInsteadOfLinking()
    {
        var r = SuggestionEmailBlock.Render(FourTerms(), null);

        Assert.DoesNotContain("href=", r.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("http", r.Text, StringComparison.Ordinal);
        Assert.Contains("Review and add on the Dictionary page in your Cockpit", r.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every term and heard-variant is user speech that arrived through a transcription model, so it is escaped
    /// on the way into the markup. A term that happened to be heard as markup must render as those characters,
    /// not run as them in the reader's mail client.
    /// </summary>
    [Fact]
    public void Render_EscapesSpeechInTheMarkup()
    {
        var hostile = new[] { Sug("<script>alert(1)</script>", 5, 10, ("\"quoted\" & odd", 5)) };

        var r = SuggestionEmailBlock.Render(hostile, "https://gw.example.com/dictionary");

        Assert.DoesNotContain("<script>", r.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", r.Html, StringComparison.Ordinal);
        Assert.Contains("&quot;quoted&quot; &amp; odd", r.Html, StringComparison.Ordinal);
        // The plain-text rendering is the same content with no markup, so it is NOT escaped there.
        Assert.Contains("<script>alert(1)</script>", r.Text, StringComparison.Ordinal);
    }

    /// <summary>An empty batch has no block. The caller decides that before rendering, so being handed one is a
    /// programming error and says so rather than producing an empty block that looks like a rendering bug.</summary>
    [Fact]
    public void Render_EmptyBatch_Throws()
        => Assert.Throws<ArgumentException>(() =>
            SuggestionEmailBlock.Render(Array.Empty<MistranscriptionSuggestion>(), null));

    /// <summary>The footer names the exact setting that controls the block, closing the loop with the Settings
    /// screen inside the message itself.</summary>
    [Fact]
    public void Footer_NamesTheSettingThatControlsIt()
    {
        Assert.Contains("Suggestions in my daily email", SuggestionEmailBlock.Footer, StringComparison.Ordinal);
        Assert.Contains("Settings", SuggestionEmailBlock.Footer, StringComparison.Ordinal);
    }

}
