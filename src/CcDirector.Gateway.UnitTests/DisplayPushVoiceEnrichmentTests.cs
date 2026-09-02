using System.Collections.Generic;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The display-state PUSH seam (FleetDisplayStateObserver) must fold from the SAME inputs the roster folds
/// from. The pushed snapshot carries only Director-owned facts; the two voice-readiness booleans
/// (VoiceGenerating / VoiceAudioReady) are Gateway-only, so <see cref="GatewayHost.EnrichVoiceThenFoldForPush"/>
/// stamps them onto each session from the live voice lookups BEFORE folding - exactly as the roster handler
/// does.
///
/// The bug these defend against: without the enrichment the push seam sees VoiceAudioReady=false for every
/// session and, since #1841 made IsVoicePreparing key on <c>!VoiceAudioReady</c>, held every voice-mode
/// waiting session yellow "Preparing voice" forever - never red once the voice was ready. The roster path
/// enriched these, so the browsers folded red while the push-only desktop rail stuck yellow. Remove either
/// enrichment line in EnrichVoiceThenFoldForPush and the matching test below goes red.
/// </summary>
public sealed class DisplayPushVoiceEnrichmentTests
{
    // A voice-mode session as it arrives in the PUSH snapshot: raw-red (turn ended, waiting) and with the
    // Gateway-only voice booleans still at their default false - the Director never sets them.
    private static SessionDto PushedVoiceSession(bool snapshotAudioReady = false) => new()
    {
        SessionId = "v",
        StatusColor = "red",
        ActivityState = "WaitingForInput",
        VoiceMode = true,
        VoiceGenerating = false,
        VoiceAudioReady = snapshotAudioReady,
    };

    [Fact]
    public void PushSeam_WhenGatewayVoiceReady_FoldsRed_NotStuckYellow()
    {
        // The Gateway knows the voice is ready (HasVoice == true). The push seam must enrich that fact and
        // fold RED, the same answer the roster serves. This is the exact stuck-yellow regression.
        var s = PushedVoiceSession(snapshotAudioReady: false);

        GatewayHost.EnrichVoiceThenFoldForPush(
            new List<SessionDto> { s },
            voiceGeneratingFor: _ => false,
            voiceAudioReadyFor: _ => true,
            tenant: TenantId.Local,
            needsYouStampFor: null,
            snoozeRegistry: null);

        Assert.True(s.VoiceAudioReady);           // enrichment overwrote the snapshot's stale false
        Assert.Equal("red", s.EffectiveColor);    // -> red, not the frozen yellow
    }

    [Fact]
    public void PushSeam_WhenGatewayVoiceNotReady_FoldsYellow()
    {
        // The Gateway knows the voice is NOT ready. Even if the snapshot carried a stale audioReady=true,
        // the enrichment must overwrite it from the live lookup so the seam folds yellow "preparing voice".
        var s = PushedVoiceSession(snapshotAudioReady: true);

        GatewayHost.EnrichVoiceThenFoldForPush(
            new List<SessionDto> { s },
            voiceGeneratingFor: _ => false,
            voiceAudioReadyFor: _ => false,
            tenant: TenantId.Local,
            needsYouStampFor: null,
            snoozeRegistry: null);

        Assert.False(s.VoiceAudioReady);          // enrichment overwrote the snapshot's stale true
        Assert.Equal("yellow", s.EffectiveColor);
    }

    [Fact]
    public void PushSeam_WhileGenerating_FoldsYellow()
    {
        // Actively generating the spoken summary -> yellow, regardless of a stale cached-audio flag.
        var s = PushedVoiceSession(snapshotAudioReady: false);

        GatewayHost.EnrichVoiceThenFoldForPush(
            new List<SessionDto> { s },
            voiceGeneratingFor: _ => true,
            voiceAudioReadyFor: _ => true,
            tenant: TenantId.Local,
            needsYouStampFor: null,
            snoozeRegistry: null);

        Assert.Equal("yellow", s.EffectiveColor);
    }

    // ---------- The push seam carries the REASON, not just the readiness (issue #2576) ----------

    /// <summary>
    /// The two booleans say there is no audio; they cannot say WHY. Without the reason on the row,
    /// <c>SessionOrdering.StateLabel</c> has nothing to render and falls back to "Preparing voice" - so the
    /// desktop would claim a narration was in flight for a session that will never produce one, which is the
    /// defect on the phone wearing a different surface. The push seam must therefore stamp the same
    /// <c>VoiceUnavailable</c> and <c>VoiceDisplay</c> facts the roster stamps.
    ///
    /// Asserted on the LABEL, not on the stamped field: the point is that the reason reaches the words the
    /// desktop renders. Asserting the field alone would pass even if the label never read it.
    /// </summary>
    [Fact]
    public void PushSeam_WhenNothingToNarrate_LabelSaysSo_NotPreparingVoice()
    {
        var s = PushedVoiceSession(snapshotAudioReady: false);

        GatewayHost.EnrichVoiceThenFoldForPush(
            new List<SessionDto> { s },
            voiceGeneratingFor: _ => false,
            voiceAudioReadyFor: _ => false,
            tenant: TenantId.Local,
            needsYouStampFor: null,
            snoozeRegistry: null,
            voiceUnavailableFor: _ => null,
            nothingToNarrateFor: _ => true);

        Assert.Equal("yellow", s.EffectiveColor);                    // the hold is unchanged
        Assert.Equal("nothingToNarrate", s.VoiceDisplay?.Kind);      // the reason rode the push
        Assert.Equal("Nothing to read aloud", s.StateLabel);         // and reached the words
    }

    /// <summary>
    /// The retry schedule's count rides the push too (VoiceRetryPolicy). Without this delegate the desktop's
    /// Voice tab would fold with zero attempts and keep the Generate button off after the Gateway had stopped
    /// trying - the roster and the push disagreeing about the one thing the reader can press.
    /// </summary>
    [Fact]
    public void PushSeam_WhenTheAutomaticRetriesAreSpent_TheVerdictOffersGenerate()
    {
        var s = PushedVoiceSession(snapshotAudioReady: false);

        GatewayHost.EnrichVoiceThenFoldForPush(
            new List<SessionDto> { s },
            voiceGeneratingFor: _ => false,
            voiceAudioReadyFor: _ => false,
            tenant: TenantId.Local,
            needsYouStampFor: null,
            snoozeRegistry: null,
            voiceUnavailableFor: _ => null,
            nothingToNarrateFor: _ => false,
            voiceAutomaticAttemptsFor: _ => Wingman.VoiceRetryPolicy.MaxAutomaticAttempts);

        Assert.Equal("gaveUp", s.VoiceDisplay?.Kind);
        Assert.True(s.VoiceDisplay?.CanGenerate);
        Assert.Contains("stopped trying on its own", s.VoiceDisplay?.Message);
    }

    [Fact]
    public void PushSeam_WhenVoiceServiceDown_LabelSaysSo_NotPreparingVoice()
    {
        var s = PushedVoiceSession(snapshotAudioReady: false);

        GatewayHost.EnrichVoiceThenFoldForPush(
            new List<SessionDto> { s },
            voiceGeneratingFor: _ => false,
            voiceAudioReadyFor: _ => false,
            tenant: TenantId.Local,
            needsYouStampFor: null,
            snoozeRegistry: null,
            voiceUnavailableFor: _ => Core.HostedAi.HostedAiState.ServiceDown,
            nothingToNarrateFor: _ => false);

        Assert.NotNull(s.VoiceUnavailable);                          // the roster's own fact, on the push row
        Assert.Equal("yellow", s.EffectiveColor);
        Assert.Equal("Voice service down", s.StateLabel);
    }

    /// <summary>The negative control: with no reason to give, the push seam still folds the genuine
    /// "being made right now" case to the existing words. The new stamps add a reason; they do not
    /// change what a session with nothing wrong says.</summary>
    [Fact]
    public void PushSeam_WhileGenerating_WithNoReason_StillSaysPreparingVoice()
    {
        var s = PushedVoiceSession(snapshotAudioReady: false);

        GatewayHost.EnrichVoiceThenFoldForPush(
            new List<SessionDto> { s },
            voiceGeneratingFor: _ => true,
            voiceAudioReadyFor: _ => false,
            tenant: TenantId.Local,
            needsYouStampFor: null,
            snoozeRegistry: null,
            voiceUnavailableFor: _ => null,
            nothingToNarrateFor: _ => false);

        Assert.Equal("Preparing voice", s.StateLabel);
    }
}
