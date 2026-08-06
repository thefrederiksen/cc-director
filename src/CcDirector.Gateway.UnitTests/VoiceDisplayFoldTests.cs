using CcDirector.Core.HostedAi;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The one place the Voice screen is ruled (the client now only renders). These pin every folded state,
/// and above all the screenshot bug: a voice-mode session with no audio and nothing to narrate must NOT
/// offer a "Generate narration" button, because pressing it re-runs the same empty read and never makes
/// audio. CanGenerate is the whole defect, so it is asserted on every state.
/// </summary>
public sealed class VoiceDisplayFoldTests
{
    [Fact]
    public void NotVoiceMode_IsOff_NoActions()
    {
        var d = VoiceDisplayFold.Fold(voiceMode: false, agentWorking: false, hasAudio: false, generating: false, unavailable: null, nothingToNarrate: false);
        Assert.Equal("off", d.Kind);
        Assert.False(d.CanPlay);
        Assert.False(d.CanGenerate);
    }

    [Fact]
    public void HasAudio_IsReady_CanPlay_NoGenerate()
    {
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: true, generating: false, unavailable: null, nothingToNarrate: false);
        Assert.Equal("ready", d.Kind);
        Assert.Equal("green", d.Tone);
        Assert.True(d.CanPlay);
        Assert.False(d.CanGenerate);
    }

    [Fact]
    public void HasAudio_WinsOverGenerating_NeverPullsTheRugOnAListener()
    {
        // Mid-regeneration with a playable clip present: keep offering the clip (issue #1322), do not
        // flip to a "preparing" state that would drop a listener out of playback.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: true, generating: true, unavailable: null, nothingToNarrate: false);
        Assert.Equal("ready", d.Kind);
        Assert.True(d.CanPlay);
    }

    [Fact]
    public void Generating_IsPreparing_NoButton()
    {
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: true, unavailable: null, nothingToNarrate: false);
        Assert.Equal("preparing", d.Kind);
        Assert.Equal("yellow", d.Tone);
        Assert.False(d.CanGenerate);
    }

    [Fact]
    public void Retrying_IsYellowOnItsWay_NoButton_SharedCopy()
    {
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: HostedAiState.Retrying, nothingToNarrate: false);
        Assert.Equal("retrying", d.Kind);
        Assert.Equal("yellow", d.Tone);
        Assert.False(d.CanGenerate);
        // Reuses the single-source copy, never a hand-written string.
        Assert.Equal(HostedAiMessages.For(HostedAiState.Retrying).Text, d.Message);
    }

    [Fact]
    public void ServiceDown_IsRed_NoButton_SharedCopy()
    {
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: HostedAiState.ServiceDown, nothingToNarrate: false);
        Assert.Equal("serviceDown", d.Kind);
        Assert.Equal("red", d.Tone);
        Assert.False(d.CanGenerate);
        Assert.Equal(HostedAiMessages.For(HostedAiState.ServiceDown).Text, d.Message);
    }

    [Theory]
    [InlineData(HostedAiState.NeedsCredits)]
    [InlineData(HostedAiState.CapReached)]
    [InlineData(HostedAiState.NeedsKey)]
    [InlineData(HostedAiState.SubscriptionRequired)]
    [InlineData(HostedAiState.FairUseLimitReached)]
    [InlineData(HostedAiState.Unavailable)]
    public void Blocked_CarriesTheSharedCallToAction_NoGenerateButton(HostedAiState state)
    {
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: state, nothingToNarrate: false);
        Assert.Equal("blocked", d.Kind);
        Assert.NotNull(d.Reason);                 // the shared CTA (where one exists) rides here
        Assert.Equal(HostedAiMessages.For(state).Text, d.Message);
        Assert.False(d.CanGenerate);              // a generate button would hit the same wall
    }

    [Theory]
    [InlineData(HostedAiState.SubscriptionRequired, "Not included with this account")]
    [InlineData(HostedAiState.FairUseLimitReached, "Monthly fair-use limit reached")]
    [InlineData(HostedAiState.Unavailable, "Voice unavailable")]
    public void IncludedAiRefusals_HaveNoCostWordsInTheirLabelsOrCopy(HostedAiState state, string expectedLabel)
    {
        // Issue #1360: the two Included AI refusals and the neutral unknown state must never put credit
        // or money words on the voice screen.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: state, nothingToNarrate: false);
        Assert.Equal(expectedLabel, d.Label);
        Assert.DoesNotContain("credit", d.Label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credit", d.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingToNarrate_IsHonestState_WithNoDeadEndButton_THE_SCREENSHOT_BUG()
    {
        // The exact defect in the screenshot: voice on, no audio, and the session is waiting on a prompt
        // (no text reply). It must be its OWN honest state with NO Generate button - not a red "unavailable"
        // badge next to a button that can never succeed.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: null, nothingToNarrate: true);
        Assert.Equal("nothingToNarrate", d.Kind);
        Assert.False(d.CanGenerate);
        Assert.False(d.CanPlay);
        Assert.False(string.IsNullOrWhiteSpace(d.Label));    // it SAYS something
        Assert.False(string.IsNullOrWhiteSpace(d.Message));  // and explains why
    }

    [Fact]
    public void AnsweredFailure_WinsOverNothingToNarrate()
    {
        // If both are somehow set, a real hosted-AI failure reason is more informative than "nothing to
        // narrate" and takes precedence.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: HostedAiState.ServiceDown, nothingToNarrate: true);
        Assert.Equal("serviceDown", d.Kind);
    }

    [Fact]
    public void AgentWorking_IsWorkingState_NoPlay_NoButton_DominatesEverything()
    {
        // The agent is mid-turn: the finished-turn narration is stale. No play, no Generate - and it
        // dominates even a lingering reason or nothing-to-narrate marker, matching the pre-fold client
        // rule where agent-working suppressed every other voice affordance.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: true, hasAudio: false, generating: false, unavailable: HostedAiState.Retrying, nothingToNarrate: true);
        Assert.Equal("working", d.Kind);
        Assert.False(d.CanPlay);
        Assert.False(d.CanGenerate);
    }

    [Fact]
    public void NoAudioNoReasonNotEmpty_IsNotReady_AndOnlyHereMayGenerate()
    {
        // The one legitimate "you can make one" window: voice on, nothing yet, but there may be a text
        // reply to narrate. This is the ONLY state that offers Generate.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: null, nothingToNarrate: false);
        Assert.Equal("notReady", d.Kind);
        Assert.True(d.CanGenerate);
    }

    // --- TTS fallback: the generic backup-voice notice (mission Phase 2) ------------------------------

    [Fact]
    public void ServedViaFallback_IsStillReady_WithTheGenericNotice_NoProviderNamed()
    {
        // A backup-served clip is a SUCCESS-with-a-note: a normal green, playable "ready", plus the one
        // generic notice line. It is NOT an outage state and names no provider.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: true, generating: false, unavailable: null, nothingToNarrate: false, servedViaFallback: true);
        Assert.Equal("ready", d.Kind);
        Assert.Equal("green", d.Tone);
        Assert.True(d.CanPlay);
        Assert.False(d.CanGenerate);
        Assert.Equal(VoiceDisplayFold.BackupVoiceNotice, d.VoiceFallbackNotice);
        // Host non-disclosure has no carve-out: the notice must never name the backup provider.
        Assert.DoesNotContain("openai", d.VoiceFallbackNotice!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("backup voice", d.VoiceFallbackNotice!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotServedViaFallback_HasNoNotice()
    {
        // The normal case: a ready clip made by the primary provider carries no notice.
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: true, generating: false, unavailable: null, nothingToNarrate: false, servedViaFallback: false);
        Assert.Equal("ready", d.Kind);
        Assert.Null(d.VoiceFallbackNotice);
    }

    [Fact]
    public void ServedViaFallback_NeverRidesAnOutageState()
    {
        // Defensive: a fallback flag with no playable audio must NEVER turn a real outage into a
        // "ready + notice". The notice only ever attaches to the green ready verdict; an answered
        // ServiceDown stays ServiceDown with no notice (a fallback SUCCESS never becomes an outage).
        var d = VoiceDisplayFold.Fold(voiceMode: true, agentWorking: false, hasAudio: false, generating: false, unavailable: HostedAiState.ServiceDown, nothingToNarrate: false, servedViaFallback: true);
        Assert.Equal("serviceDown", d.Kind);
        Assert.Null(d.VoiceFallbackNotice);
    }
}
