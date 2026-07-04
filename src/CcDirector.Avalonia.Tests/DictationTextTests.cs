using CcDirector.Avalonia.Voice;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Unit tests for <see cref="DictationText.Join"/>, the whitespace-aware join that
/// assembles the dictation transcript. These pin the "Resume appends new speech to
/// the (edited) transcript without overwriting edits" invariant from #156: the left
/// side is whatever the user currently has in the editable review box and the right
/// side is the freshly cleaned segment.
/// </summary>
public class DictationTextTests
{
    [Fact]
    public void Join_EmptyLeft_ReturnsRight()
    {
        Assert.Equal("hello", DictationText.Join("", "hello"));
    }

    [Fact]
    public void Join_EmptyRight_ReturnsLeft()
    {
        Assert.Equal("hello", DictationText.Join("hello", ""));
    }

    [Fact]
    public void Join_BothEmpty_ReturnsEmpty()
    {
        Assert.Equal("", DictationText.Join("", ""));
    }

    [Fact]
    public void Join_TwoWords_InsertsSingleSpace()
    {
        Assert.Equal("hello world", DictationText.Join("hello", "world"));
    }

    [Fact]
    public void Join_LeftEndsWithSpace_DoesNotDoubleSpace()
    {
        Assert.Equal("hello world", DictationText.Join("hello ", "world"));
    }

    [Fact]
    public void Join_RightStartsWithSpace_DoesNotDoubleSpace()
    {
        Assert.Equal("hello world", DictationText.Join("hello", " world"));
    }

    [Fact]
    public void Join_LeftEndsWithNewline_PreservesBoundaryWithoutAddingSpace()
    {
        Assert.Equal("hello\nworld", DictationText.Join("hello\n", "world"));
    }

    [Fact]
    public void Join_EditedTextThenNewSpeech_PreservesEditsAndAppends()
    {
        // The #156 invariant: the user edited the reviewed transcript, then resumed
        // and spoke more. The edited text must survive verbatim as a prefix, with
        // the new cleaned segment appended after a single separating space.
        var edited = "We need to fix the desktop transcription tool.";
        var newSpeech = "It now lets us edit before sending.";

        var result = DictationText.Join(edited, newSpeech);

        Assert.StartsWith(edited, result);
        Assert.EndsWith(newSpeech, result);
        Assert.Equal(edited + " " + newSpeech, result);
    }

    // InsertAt underpins "Send drops the dictation at the caret, exactly like the Insert button".

    [Fact]
    public void InsertAt_EmptyExisting_ReturnsInsertUnchanged()
    {
        Assert.Equal("hello", DictationText.InsertAt("", 0, "hello"));
    }

    [Fact]
    public void InsertAt_EmptyInsert_ReturnsExistingUnchanged()
    {
        Assert.Equal("fix the bug", DictationText.InsertAt("fix the bug", 4, ""));
    }

    [Fact]
    public void InsertAt_AtEnd_AppendsWithSingleSpace()
    {
        Assert.Equal("fix the login bug and add a test",
            DictationText.InsertAt("fix the login bug", 17, "and add a test"));
    }

    [Fact]
    public void InsertAt_InMiddleOnWordBoundary_SpacesOnlyTheOpenSide()
    {
        // Caret sits just after "fix the " (index 8), before "bug". The left side already ends in a
        // space so no space is added there; the right side starts with a letter so one space is added.
        Assert.Equal("fix the and bug", DictationText.InsertAt("fix the bug", 8, "and"));
    }

    [Fact]
    public void InsertAt_InMiddleMidWord_SpacesBothSides()
    {
        // Caret between two non-space characters: a separating space is added on both sides.
        Assert.Equal("foo bar baz", DictationText.InsertAt("foobaz", 3, "bar"));
    }

    [Fact]
    public void InsertAt_CaretOutOfRange_ClampsToEnd()
    {
        Assert.Equal("hello world", DictationText.InsertAt("hello", 999, "world"));
    }

    [Fact]
    public void InsertAt_CaretAtStart_PrependsWithSingleSpace()
    {
        Assert.Equal("hello world", DictationText.InsertAt("world", 0, "hello"));
    }
}
