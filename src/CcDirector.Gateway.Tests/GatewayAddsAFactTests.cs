using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gap 5: THE GATEWAY ADDS ITS OWN FACT AND NEVER OVERWRITES A FIELD THE DIRECTOR OWNS.
///
/// The Gateway used to get its voice-mode yellow by writing <c>s.BriefingState = "Briefing"</c> onto the
/// row during enrichment (GatewayEndpoints), gated on the Director's value being null/"None"/"Briefed".
/// The pixel was right and the row was ruined: afterwards, BriefingState="Briefing" + VoiceGenerating=true
/// could not say WHO said it. If the Director genuinely was briefing, the desktop folds yellow too and the
/// screens AGREE; if the Gateway had overwritten a "None", the desktop folds red and they genuinely
/// DISAGREE. Opposite verdicts, identical row - so the agreement check could only report "indeterminate"
/// and refuse to grade it, which fixes the instrument rather than the product.
///
/// The fold now reads the Gateway's own VoiceGenerating (which was always being stamped anyway) through
/// <see cref="SessionOrdering.IsGatewayVoiceBriefing"/>, carrying the stamp's exact condition. Nothing is
/// destroyed and no pixel moves.
///
/// These assert BOTH halves - the colour is unchanged AND the Director's fact survives. Asserting only the
/// colour would pass just as well against the overwrite, which is the whole point of the change.
/// </summary>
public sealed class GatewayAddsAFactTests
{
    /// <summary>A voice-mode session parked at a turn end - raw red - with the Gateway generating its
    /// spoken summary. <paramref name="directorBriefingState"/> is the DIRECTOR's fact, untouched.</summary>
    private static SessionDto VoiceGenerating(string directorBriefingState) => new()
    {
        SessionId = "v",
        StatusColor = "red",
        ActivityState = "WaitingForInput",
        VoiceMode = true,
        VoiceGenerating = true,
        BriefingState = directorBriefingState,
    };

    // ===== THE DEFECT: the fact the Gateway used to destroy must survive =====

    /// <summary>
    /// The headline. The Gateway's window still paints yellow, and the Director's "None" is still readable
    /// afterwards - so the row says who said what.
    ///
    /// Watched failing on revert: restore the stamp in GatewayEndpoints and BriefingState reads "Briefing"
    /// here, having been overwritten - the Director's answer gone, the row unanswerable.
    /// </summary>
    [Fact]
    public void TheGatewayVoiceWindow_FoldsYellow_AndLeavesTheDirectorsFactIntact()
    {
        var s = VoiceGenerating(directorBriefingState: "None");

        Assert.Equal("yellow", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Wingman reading", SessionOrdering.StateLabel(s));

        // The fact the overwrite used to destroy. The fold READ it; it did not write it.
        Assert.Equal("None", s.BriefingState);
    }

    /// <summary>
    /// The gradeability point, stated as a test: two rows that the overwrite made identical are now
    /// distinguishable. Both fold yellow on the Gateway - but one is a Director that IS briefing (the
    /// desktop, which has no Gateway facts, folds yellow too: agreement) and the other is a Director that
    /// is NOT (the desktop folds red: a real disagreement). The check can now tell them apart, because the
    /// Director's answer is still on the row.
    /// </summary>
    [Fact]
    public void TwoRowsTheOverwriteMadeIdentical_AreNowDistinguishable()
    {
        var directorIsBriefing = VoiceGenerating(directorBriefingState: "Briefing");
        var onlyTheGatewayIsBriefing = VoiceGenerating(directorBriefingState: "None");

        // Same Gateway pixel...
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(directorIsBriefing));
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(onlyTheGatewayIsBriefing));

        // ...and the rows still say who said it. Under the overwrite both read "Briefing" here.
        Assert.Equal("Briefing", directorIsBriefing.BriefingState);
        Assert.Equal("None", onlyTheGatewayIsBriefing.BriefingState);

        // Which is exactly what makes them gradeable: the Director's own fold differs between them.
        Assert.True(SessionOrdering.IsBriefing(directorIsBriefing));
        Assert.False(SessionOrdering.IsBriefing(onlyTheGatewayIsBriefing));
    }

    // ===== The stamp's condition, preserved exactly - moved from a write to a read =====

    /// <summary>The Director saying "Failed" kept the Gateway's window shut when the guard was a write
    /// condition, and still does now it is a read condition. Same rule, no destroyed field.</summary>
    [Fact]
    public void ADirectorsFailedBriefing_KeepsTheGatewayWindowShut()
    {
        var s = VoiceGenerating(directorBriefingState: "Failed");

        Assert.False(SessionOrdering.IsGatewayVoiceBriefing(s));
        Assert.Equal("Failed", s.BriefingState);
    }

    /// <summary>The stamp required raw red; so does the read. A working session is BLUE - nothing
    /// outranks working - however busy the Gateway's voice pipeline is.</summary>
    [Fact]
    public void AWorkingSession_IsBlue_HoweverBusyTheVoicePipeline()
    {
        var s = VoiceGenerating(directorBriefingState: "None");
        s.ActivityState = "Working";

        Assert.False(SessionOrdering.IsGatewayVoiceBriefing(s));
        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
    }

    /// <summary>No generation, no window. The control: if this ever goes yellow the fix has invented a
    /// state rather than moved one.</summary>
    [Fact]
    public void NotGenerating_IsNotTheGatewayWindow()
    {
        var s = VoiceGenerating(directorBriefingState: "None");
        s.VoiceGenerating = false;

        Assert.False(SessionOrdering.IsGatewayVoiceBriefing(s));
        Assert.Equal("red", SessionOrdering.EffectiveColor(s));
    }

    /// <summary>The Director's OWN briefing is untouched by all of this - it folds yellow on its own
    /// authority, with no Gateway voice involved, exactly as it always did.</summary>
    [Fact]
    public void TheDirectorsOwnBriefing_StillFoldsYellow_WithNoGatewayVoice()
    {
        var s = new SessionDto
        {
            SessionId = "d",
            StatusColor = "red",
            ActivityState = "WaitingForInput",
            BriefingState = "Briefing",
            VoiceGenerating = false,
        };

        Assert.Equal("yellow", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Wingman reading", SessionOrdering.StateLabel(s));
    }
}
