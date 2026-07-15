using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// DEFECT 19 - the wedged orange. The regression tests for the rule that bounds the dictation colour
/// (<see cref="DictationPhase.For"/>), fixed 14 July 2026 by the mission "Session State Truth".
///
/// THE DEFECT, OBSERVED - not theorised. Log correlation on SOREN_NORTH, 14 July 2026: upload
/// f13cb4b6d9d0 registered at 06:11 on 12 July 2026 and stood undelivered until 07:40 - ONE HOUR AND
/// THIRTY MINUTES - painting its session orange the entire time, reading "Uploading from phone" about an
/// upload whose audio had already arrived intact (456KB, assembled on every attempt). Its transcription
/// returned a retryable 502 roughly fifteen times, and each of those returned WITHOUT writing a terminal
/// state, so the durable PENDING record stood, so the colour stood. It survived four Gateway restarts (the
/// record is on disk), and at 07:40 it finally transcribed and delivered 362 characters.
///
/// Both halves of that matter, and the fix honours both:
///   - the durable record was RIGHT to keep the words. They landed. It is untouched, and still never expires.
///   - the colour was LYING for ninety minutes. It is now bounded by actual progress.
///
/// THE RULE: "is there an undelivered dictation?" (durable, unbounded, must never expire) is NOT the same
/// question as "should this session be painted orange right now?" (presentation, must always be bounded).
/// One flag was answering both. That was the whole defect.
/// </summary>
public sealed class DictationOrangeBoundTests
{
    // ===== the rule =================================================================================

    [Fact]
    public void UndeliveredButNotProgressing_DoesNotPaint_ThisIsTheDefect19Fix()
    {
        // The phone registered a dictation and went quiet - it died, lost signal, or its transcription is
        // failing over and over. The durable record still stands (correctly: the words are not lost), but
        // nothing is happening, so the session must fall back to its TRUE colour.
        //
        // THIS IS THE ASSERTION THAT FAILS AGAINST THE OLD RULE. Before the fix this returned
        // "Uploading from phone" - forever, for as long as the record stood, which for a dictation that
        // reaches no terminal state is unbounded. That is upload f13cb4b6d9d0's ninety minutes.
        Assert.Null(DictationPhase.For(activelyTranscribing: false, undelivered: true, progressing: false));
    }

    [Fact]
    public void UndeliveredAndProgressing_PaintsUploading()
    {
        // The honest case the label exists for: the phone is actively sending. A genuinely slow upload
        // refreshes its progress mark on every stored chunk, so it keeps this label and is never cut short.
        Assert.Equal("Uploading from phone",
            DictationPhase.For(activelyTranscribing: false, undelivered: true, progressing: true));
    }

    [Fact]
    public void ActivelyTranscribing_PaintsTranscribing_TheMostSpecificTrueStatement()
    {
        Assert.Equal("Transcribing",
            DictationPhase.For(activelyTranscribing: true, undelivered: true, progressing: true));
    }

    [Fact]
    public void ActivelyTranscribing_WinsEvenWhenTheUploadHasGoneQuiet()
    {
        // A long server-side transcribe makes no upload progress by definition. The run is bounded by its
        // own finally, so it may paint regardless - and it is the most specific true statement available.
        Assert.Equal("Transcribing",
            DictationPhase.For(activelyTranscribing: true, undelivered: false, progressing: false));
    }

    [Fact]
    public void NoDictation_PaintsNothing()
    {
        Assert.Null(DictationPhase.For(activelyTranscribing: false, undelivered: false, progressing: false));
    }

    [Fact]
    public void Progressing_WithoutAnUndeliveredRecord_PaintsNothing()
    {
        // A stale progress mark with no durable record behind it is not a dictation. Both facts are required.
        Assert.Null(DictationPhase.For(activelyTranscribing: false, undelivered: false, progressing: true));
    }

    // ===== the rule, joined to the fold that renders it =============================================

    [Fact]
    public void AQuietUndeliveredDictation_LetsTheSessionShowItsRealColour_InsteadOfWedgingOrange()
    {
        // End to end through the shared fold: the session needs the user. Before the fix a quiet
        // undelivered record painted it orange indefinitely, hiding the fact that it wanted attention.
        var s = new SessionDto
        {
            SessionId = "s1",
            ActivityState = "WaitingForInput",
            BriefingState = "None",
            DictationStatus = DictationPhase.For(activelyTranscribing: false, undelivered: true, progressing: false),
        };

        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void AWorkingSessionWithAWedgedDictationIsBlue_BecauseWorkingIsBlue()
    {
        // THE LAW, and the reason the normal dictation path needs no special case: a delivered dictation
        // submits text, which makes the agent work, and working is blue no matter what any stale flag says.
        var s = new SessionDto
        {
            SessionId = "s1",
            ActivityState = "Working",
            BriefingState = "None",
            DictationStatus = "Uploading from phone",
        };

        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Working", SessionOrdering.StateLabel(s));
    }
}
