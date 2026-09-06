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

    // ---- the compose box's provenance ---------------------------------------------------------------

    [Fact]
    public void ATranscriptInsertedAndSentUntouched_IsSpoken()
    {
        var box = new ComposerProvenance();
        box.Inserted(Words);
        box.TextChanged(Words);
        Assert.Equal(InputOrigin.DesktopVoice, box.OriginFor(Words));
        Assert.Equal(InputOrigin.DesktopVoice, box.OriginFor("  " + Words + " \n"));
    }

    [Fact]
    public void NothingInserted_IsTyped_ThisIsTheOrdinaryComposer()
    {
        var box = new ComposerProvenance();
        box.TextChanged("please deploy the gateway");
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor("please deploy the gateway"));
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor(""));
    }

    [Theory]
    [InlineData("please " + Words)]
    [InlineData(Words + " and restart it")]
    [InlineData("please " + Words + " and restart it")]
    public void TypedWordsAroundAnInsertedTranscript_MakeTheTurnTyped(string sent)
    {
        var box = new ComposerProvenance();
        box.Inserted(Words);
        box.TextChanged(sent);
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor(sent));
    }

    [Fact]
    public void AnEditedTranscript_IsTyped_AndSoIsATranscriptDeletedAndReplacedByTyping()
    {
        var box = new ComposerProvenance();
        box.Inserted(Words);
        box.TextChanged(Words);
        // The user changes one word: the transcript no longer stands in the box.
        var edited = Words.Replace("gateway", "cockpit");
        box.TextChanged(edited);
        Assert.Empty(box.Transcripts);
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor(edited));
    }

    [Fact]
    public void TwoDictationsJoined_AreTyped_AnEarlierSegmentIsAPrefix()
    {
        var box = new ComposerProvenance();
        box.Inserted("first check the logs");
        box.TextChanged("first check the logs");
        box.Inserted(Words);
        var text = "first check the logs " + Words;
        box.TextChanged(text);
        Assert.Equal(2, box.Transcripts.Count);
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor(text));
    }

    [Fact]
    public void TheBoxReplacedBySomethingElse_ForgetsTheTranscript_AndResetClearsIt()
    {
        var box = new ComposerProvenance();
        box.Inserted(Words);
        box.TextChanged(Words);
        // A session switch or a slash command replaces the text wholesale.
        box.TextChanged("/help");
        Assert.Empty(box.Transcripts);
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor("/help"));

        box.Inserted(Words);
        box.TextChanged(Words);
        box.Reset();
        Assert.Equal(InputOrigin.DesktopTyped, box.OriginFor(Words));
    }

    [Fact]
    public void TheComposeBox_AgreesWithTheRule_OnEveryExampleRow()
    {
        foreach (var e in Examples)
        {
            var box = new ComposerProvenance();
            var text = e.Before.Trim();
            if (e.Prefix.Length > 0)
            {
                text = (text + " " + e.Prefix).Trim();
                box.Inserted(e.Prefix);
                box.TextChanged(text);
            }
            text = (text + " " + e.Transcript + " " + e.After).Trim();
            box.Inserted(e.Transcript);
            box.TextChanged(text);
            Assert.True(e.Expected == box.Classify(text), e.Name);
        }
    }
}
