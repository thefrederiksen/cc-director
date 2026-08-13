using CcDirector.Core.HostedAi;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2576: the terminal "gave up" verdict, and the wait it carries.
///
/// Before this, every no-audio verdict was either a calm "still coming" or a specific fault, so a
/// narration that simply never arrived matched the calm one FOREVER. A session sat that way for
/// forty-eight minutes. <c>SessionOrdering.IsVoicePreparing</c>'s own note named a terminal gave-up state
/// as the correct answer to that wedge and it was never built; this is it.
/// </summary>
public sealed class VoiceGaveUpFoldTests
{
    private static readonly DateTime Now = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);

    private static VoiceDisplay Fold(DateTime? waitingSince, bool generating = false, bool hasAudio = false,
        HostedAiState? unavailable = null, bool nothingToNarrate = false)
        => VoiceDisplayFold.Fold(
            voiceMode: true, agentWorking: false, hasAudio: hasAudio, generating: generating,
            unavailable: unavailable, nothingToNarrate: nothingToNarrate,
            waitingSince: waitingSince, utcNow: Now);

    [Fact]
    public void PastTheThreshold_TheVerdictIsGaveUp_AndSaysHowLong()
    {
        var v = Fold(Now - TimeSpan.FromMinutes(48));

        Assert.Equal("gaveUp", v.Kind);
        Assert.Equal("Voice did not arrive after 48m", v.Label);
        Assert.Equal("48m", v.WaitedLabel);
        Assert.False(v.CanPlay);
        // No Generate button: it would re-run the same thing that has already failed for 48 minutes, and a
        // button that cannot succeed invites the reader to keep pressing and blame themselves.
        Assert.False(v.CanGenerate);
    }

    [Fact]
    public void JustUnderTheThreshold_IsStillTheCalmNotReady_NotGaveUp()
    {
        // The boundary matters in this direction: declaring defeat early would put a red "did not arrive"
        // on the ordinary case, which is how a real signal gets trained out of a reader.
        var v = Fold(Now - VoiceDisplayFold.GaveUpAfter + TimeSpan.FromSeconds(1));
        Assert.NotEqual("gaveUp", v.Kind);
    }

    [Fact]
    public void ExactlyAtTheThreshold_HasGivenUp()
    {
        Assert.Equal("gaveUp", Fold(Now - VoiceDisplayFold.GaveUpAfter).Kind);
    }

    [Fact]
    public void PlayableAudio_BeatsGaveUp_HoweverLongTheWaitWas()
    {
        // Audio arriving is the episode ENDING. A clip the reader can play must never be hidden behind a
        // verdict about how long it took to make.
        var v = Fold(Now - TimeSpan.FromHours(2), hasAudio: true);
        Assert.Equal("ready", v.Kind);
        Assert.True(v.CanPlay);
    }

    [Fact]
    public void ALiveAttempt_BeatsGaveUp()
    {
        // Something IS happening right now, so saying it did not arrive would be false at the moment it is
        // read - and the attempt in flight may well be the one that lands.
        var v = Fold(Now - TimeSpan.FromHours(2), generating: true);
        Assert.Equal("preparing", v.Kind);
    }

    [Fact]
    public void GaveUp_OutranksARetryingState_BecauseTheRetryHasHadLongEnough()
    {
        // "Voice on its way" past the threshold is the exact sentence this issue exists to stop. A retry
        // that has been going for 48 minutes is not news of progress.
        var v = Fold(Now - TimeSpan.FromMinutes(48), unavailable: HostedAiState.Retrying);
        Assert.Equal("gaveUp", v.Kind);
    }

    [Fact]
    public void NotWaitingAtAll_IsUnchanged()
    {
        // A null clock must not manufacture a verdict - this is the ordinary path for every session that
        // has its audio, and for every caller that does not supply the clock at all.
        var v = Fold(waitingSince: null, nothingToNarrate: true);
        Assert.Equal("nothingToNarrate", v.Kind);
        Assert.Null(v.WaitedLabel);
    }

    [Theory]
    [InlineData(0, 30, null)]          // under a minute: silent, so an ordinary turn shows no number
    [InlineData(0, 61, "1m")]
    [InlineData(0, 44 * 60, "44m")]
    [InlineData(1, 4 * 60, "1h 4m")]
    [InlineData(50, 0, "2d 2h")]
    public void TheWaitLadder_MatchesTheOneTheRosterAlreadyUses(int hours, int seconds, string? expected)
    {
        var since = Now - TimeSpan.FromHours(hours) - TimeSpan.FromSeconds(seconds);
        Assert.Equal(expected, VoiceDisplayFold.WaitedLabelFor(since, Now));
    }

    [Fact]
    public void APreparingSessionCarriesItsWait_SoTheRailCanShowIt()
    {
        // The calm state carries the number too: the rail reads "Preparing voice (2m)", so a wait that is
        // growing is visible BEFORE it reaches the point of giving up.
        var v = Fold(Now - TimeSpan.FromMinutes(2), generating: true);
        Assert.Equal("preparing", v.Kind);
        Assert.Equal("2m", v.WaitedLabel);
    }
}
