using CcDirector.Core.Sessions;
using Xunit;
using static CcDirector.Core.Sessions.SpokenTurnRule;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// Ruling R20 of the "Clean up Your Throttle" mission: ONE rule for when a submitted turn is spoken, and
/// the desktop compose box's own record of which of its words came from a microphone. The rule is the
/// phone's (inspection finding I2-01): the transcript alone is voice; typed text before or after it, an
/// earlier dictated segment ahead of it, or an edit to it makes the turn typed. Both surfaces' own tests
/// feed <see cref="SpokenTurnRule.Examples"/> through their real paths; these pin the rule and the table.
/// </summary>
public sealed class SpokenTurnRuleTests
{
    private const string Words = "deploy the gateway and tell me when it is up";

    [Fact]
    public void TheTranscriptAlone_IsSpoken_AndWhitespaceIsNotText()
    {
        Assert.True(IsSpokenAlone("", "", ""));
        Assert.True(IsSpokenAlone(null, null, null));
        Assert.True(IsSpokenAlone("  ", "\t", " \n"));
        Assert.Equal(InputModality.Voice, Classify("", "", ""));
    }

    [Theory]
    [InlineData("please", "", "")]
    [InlineData("", "", "and restart it")]
    [InlineData("", "first check the logs", "")]
    [InlineData("a", "b", "c")]
    public void AnythingComposedAroundTheTranscript_MakesTheTurnTyped(string before, string prefix, string after)
    {
        Assert.False(IsSpokenAlone(before, prefix, after));
        Assert.Equal(InputModality.Typed, Classify(before, prefix, after));
    }

    [Fact]
    public void TheExamplesTable_IsTheContractBothSurfacesAreHeldTo()
    {
        // Not empty, both outcomes present, every row consistent with the rule it illustrates, and every
        // row named - a table nobody can add a contradiction to without this test noticing.
        Assert.True(Examples.Count >= 6);
        Assert.Contains(Examples, e => e.Expected == InputModality.Voice);
        Assert.Contains(Examples, e => e.Expected == InputModality.Typed);
        foreach (var e in Examples)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
            Assert.False(string.IsNullOrWhiteSpace(e.Transcript));
            Assert.Equal(e.Expected, Classify(e.Before, e.Prefix, e.After));
        }
        Assert.Equal(Examples.Count, Examples.Select(e => e.Name).Distinct().Count());
    }

    // ---- the compose box's provenance: character RANGES, followed through every edit ----------------

    private static ComposerProvenance BoxWith(string text, params (int Start, int Length)[] spans)
    {
        var box = new ComposerProvenance();
        box.Restore(text, spans.Select(s => new SpokenSpan(s.Start, s.Length)));
        return box;
    }

    [Fact]
    public void ATranscriptInsertedAndSentUntouched_IsSpoken()
    {
        var box = new ComposerProvenance();
        box.Inserted(Words, Words, 0);
        Assert.Equal(InputOrigin.DesktopVoice, box.OriginFor(Words));
        // Whitespace typed around it is not text.
        box.TextChanged("  " + Words + " \n");
        Assert.Equal(InputOrigin.DesktopVoice, box.OriginFor("  " + Words + " \n"));
    }

    [Fact]
    public void NothingInserted_IsTyped_AndAnEmptyBoxIsTyped()
    {
        var box = new ComposerProvenance();
        box.TextChanged("please deploy the gateway");
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor("please deploy the gateway"));
        box.TextChanged("");
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor(""));
    }

    [Theory]
    [InlineData("please ", "")]
    [InlineData("", " and restart it")]
    [InlineData("please ", " and restart it")]
    public void TypedTextAroundTheTranscript_MakesItTyped_AndTheSpanFollowsTheTypingBeforeIt(string before, string after)
    {
        var box = new ComposerProvenance();
        box.Inserted(Words, Words, 0);
        // Typed one character at a time, as the box's hook hears it.
        var text = Words;
        foreach (var ch in after) { text += ch; box.TextChanged(text); }
        for (var i = before.Length - 1; i >= 0; i--) { text = before[i] + text; box.TextChanged(text); }
        var span = Assert.Single(box.Spans);
        Assert.Equal(before.Length, span.Start);
        Assert.Equal(Words.Length, span.Length);
        Assert.Equal(before.Length > 0 || after.Trim().Length > 0 ? InputOrigin.DesktopTyped : InputOrigin.DesktopVoice, box.OriginFor(text));
    }

    [Fact]
    public void EditingInsideTheTranscript_MakesItTyped_ButEditingBesideItDoesNot()
    {
        var box = new ComposerProvenance();
        box.Inserted("x " + Words, Words, 2);
        // Deleting the typed characters before it leaves the transcript standing, moved to the front.
        box.TextChanged(Words);
        Assert.Equal(new SpokenSpan(0, Words.Length), Assert.Single(box.Spans));
        Assert.Equal(InputOrigin.DesktopVoice, box.OriginFor(Words));
        // One character changed inside it: those are not the spoken characters any more.
        var edited = Words.Replace("gateway", "gateways");
        box.TextChanged(edited);
        Assert.Empty(box.Spans);
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor(edited));
    }

    [Fact]
    public void TheSameWordsTypedAndThenSpoken_AreToldApart_ByWhichCharactersWereSpoken()
    {
        // The fix-round inspector's case. A record that kept the transcript's STRING called this box spoken
        // after the spoken copy was deleted, because the typed copy still contained the string.
        var typedThenSpoken = Words + " " + Words;
        var box = new ComposerProvenance();
        box.TextChanged(Words);
        box.Inserted(typedThenSpoken, Words, Words.Length + 1);
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor(typedThenSpoken));

        // Delete the SPOKEN copy (the second one): typed words remain.
        box.TextChanged(Words);
        Assert.Empty(box.Spans);
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor(Words));

        // And the mirror: delete the TYPED copy (the first one): the spoken words remain, moved to the front.
        // The surviving text is identical either way, so the caret - which stands at the front after that
        // deletion - is what tells the two apart.
        box = new ComposerProvenance();
        box.TextChanged(Words);
        box.Inserted(typedThenSpoken, Words, Words.Length + 1);
        box.TextChanged(Words, caretAfter: 0);
        Assert.Equal(new SpokenSpan(0, Words.Length), Assert.Single(box.Spans));
        Assert.Equal(InputOrigin.DesktopVoice, box.OriginFor(Words));

        // A caret that does not describe the change (it names characters whose removal does not give the new
        // text) is ignored, and the text alone decides.
        box = new ComposerProvenance();
        box.TextChanged(Words);
        box.Inserted(typedThenSpoken, Words, Words.Length + 1);
        box.TextChanged(Words, caretAfter: 3);
        Assert.Empty(box.Spans);
    }

    [Fact]
    public void ASecondDictation_JoinedToTheFirst_IsAnEarlierSegment_AndTyped()
    {
        var box = new ComposerProvenance();
        box.Inserted("first check the logs", "first check the logs", 0);
        var text = "first check the logs " + Words;
        box.Inserted(text, Words, "first check the logs ".Length);
        Assert.Equal(2, box.Spans.Count);
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor(text));
    }

    [Fact]
    public void AReplacedBox_ForgetsTheTranscript()
    {
        var box = new ComposerProvenance();
        box.Inserted(Words, Words, 0);
        box.TextChanged("/help");
        Assert.Empty(box.Spans);
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor("/help"));
        box.Inserted(Words, Words, 0);
        box.Reset();
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor(Words));
    }

    [Fact]
    public void ARestoredRecord_IsTheSavedOne_AndASpanOutsideTheTextIsRefused()
    {
        var box = BoxWith("please " + Words, (7, Words.Length));
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor("please " + Words));
        box = BoxWith(Words, (0, Words.Length));
        Assert.Equal(InputOrigin.DesktopVoice, box.OriginFor(Words));
        Assert.Throws<ArgumentException>(() => BoxWith("short", (0, 40)));
        Assert.Throws<ArgumentException>(() => new ComposerProvenance().Inserted(Words, "not there", 0));
    }

    [Fact]
    public void ATextTheRecordWasNeverToldAbout_IsRefused_NotClassified()
    {
        // The box's hook is deferred by the toolkit; a send that asked after clearing the box, or a box
        // whose hook was never wired, would be classified against the wrong text. That is a defect and it
        // is thrown, not guessed as typed.
        var box = new ComposerProvenance();
        box.Inserted(Words, Words, 0);
        Assert.Throws<InvalidOperationException>(() => box.OriginFor(""));
        Assert.Throws<InvalidOperationException>(() => box.OriginFor(Words + "!"));
    }

    [Fact]
    public void TheComposeBox_AgreesWithTheRule_OnEveryExampleRow()
    {
        foreach (var e in Examples)
        {
            var box = new ComposerProvenance();
            var text = e.Before;
            box.TextChanged(text);
            if (e.Prefix.Length > 0)
            {
                var at = text.Length;
                text += e.Prefix;
                box.Inserted(text, e.Prefix, at);
            }
            var start = text.Length;
            text += e.Transcript;
            box.Inserted(text, e.Transcript, start);
            text += e.After;
            box.TextChanged(text);
            Assert.True(e.Expected == box.Classify(), e.Name);
        }
    }
}
