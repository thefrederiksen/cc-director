using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gap 6: <see cref="SessionOrdering.StateLabel"/> NEVER returns a blank label.
///
/// This is the invariant that let Car Mode's fallback chain die. LoopbackCarModeFleet used to read
/// <c>StateLabel ?? (EffectiveColor ?? StatusColor)</c> and SPEAK the result - a chain that ended at the
/// Director's cooked colour, illegal both as fallback programming and as a client rendering a Director
/// decision. The right fix was not better words for the blank case but closing the hole, so that the chain
/// has nothing to catch and can simply go.
///
/// The blank was never reachable in production - DictationPhase.For returns two non-empty constants or
/// null - but the invariant was enforced two assemblies away from the method that depends on it, by a
/// producer with no idea anything hung on it. These tests move it here, where it is checked.
/// </summary>
public sealed class StateLabelIsNeverBlankTests
{
    /// <summary>
    /// THE HOLE, CLOSED: a blank dictation phase is treated as no dictation, not as a dictation with no
    /// name. StateLabel used to return s.DictationStatus verbatim, so this shape produced the empty string
    /// - the one input that could have made Car Mode's fallback fire and speak a raw colour.
    ///
    /// Watched failing on revert: restore `if (s.DictationStatus is { } p) return p;` and this returns ""
    /// while the dot is orange - a row labelled with nothing at all.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankDictationPhase_IsTreatedAsNoDictation_NotAsALabelWithNoWords(string blank)
    {
        var s = new SessionDto
        {
            SessionId = "s",
            ActivityState = "WaitingForInput",
            StatusColor = "red",
            DictationStatus = blank,
        };

        // The label falls through to the session's real state instead of reading as nothing...
        Assert.Equal("Needs you", SessionOrdering.StateLabel(s));
        // ...and the dot agrees with it: no dictation, so no orange. A blank reaching only one of the two
        // arms would paint an orange dot beside a label that had fallen through - a row contradicting
        // itself, which is the defect class this whole mission exists to end.
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
    }

    /// <summary>A REAL dictation phase still labels and paints exactly as before - the control. If this
    /// breaks, the hardening has eaten the feature rather than the hole.</summary>
    [Theory]
    [InlineData("Uploading from phone")]
    [InlineData("Transcribing")]
    public void ARealDictationPhase_StillLabelsAndPaintsOrange(string phase)
    {
        var s = new SessionDto
        {
            SessionId = "s",
            ActivityState = "WaitingForInput",
            StatusColor = "red",
            DictationStatus = phase,
        };

        Assert.Equal(phase, SessionOrdering.StateLabel(s));
        Assert.Equal("orange", SessionOrdering.EffectiveColor(s));
    }

    /// <summary>
    /// The invariant itself, across every shape the fold distinguishes: whatever a session is doing,
    /// StateLabel says SOMETHING. This is what Car Mode now relies on, so it is asserted here rather than
    /// assumed there.
    /// </summary>
    [Theory]
    [InlineData("Working")]
    [InlineData("WaitingForInput")]
    [InlineData("WaitingForPerm")]
    [InlineData("Idle")]
    [InlineData("Starting")]
    [InlineData("Exited")]
    [InlineData("")]
    [InlineData("SomethingNobodyHasWrittenYet")]
    public void StateLabel_IsNeverBlank_ForAnyActivityState(string activityState)
    {
        foreach (var onHold in new[] { false, true })
        foreach (var dictation in new string?[] { null, "", "   ", "Transcribing" })
        foreach (var briefing in new[] { "None", "Briefing", "Briefed", "Failed" })
        {
            var s = new SessionDto
            {
                SessionId = "s",
                ActivityState = activityState,
                StatusColor = "red",
                OnHold = onHold,
                DictationStatus = dictation,
                BriefingState = briefing,
            };

            var label = SessionOrdering.StateLabel(s);

            Assert.False(string.IsNullOrWhiteSpace(label),
                $"StateLabel returned a blank label for activityState='{activityState}', onHold={onHold}, " +
                $"dictationStatus='{dictation ?? "(null)"}', briefingState='{briefing}'. Car Mode speaks " +
                "this label and no longer has a fallback - a blank here is a session with nothing to say.");
        }
    }

    /// <summary>An unknown activity state still gets a real word ("Idle"), not the empty string. The
    /// catch-all arm is the one most likely to be reached by a state nobody has written yet.</summary>
    [Fact]
    public void AnUnknownActivityState_FallsThroughToARealWord()
    {
        var s = new SessionDto { SessionId = "s", ActivityState = "NotAStateWeKnow", StatusColor = "blue" };

        Assert.Equal("Idle", SessionOrdering.StateLabel(s));
    }
}
