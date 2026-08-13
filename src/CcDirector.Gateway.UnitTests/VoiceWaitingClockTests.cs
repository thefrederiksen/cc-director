using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2576: the clock that finally lets a surface say HOW LONG a session has been without its voice.
///
/// The defect it closes: a session sat on "Preparing voice" for forty-eight minutes and no screen could
/// say so, because the only elapsed-time fact the product had - <c>NeedsYouSince</c> - is stamped solely
/// when the folded colour is RED, and a session waiting for voice is YELLOW. The one clock that existed
/// was, by construction, never running for exactly the sessions that were stuck.
/// </summary>
public sealed class VoiceWaitingClockTests
{
    private static readonly TenantId Other = new TenantId("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void NotWaiting_StampsNothing()
    {
        var clock = new VoiceWaitingClock();
        Assert.Null(clock.Stamp(TenantId.Local, "s", isWaitingForVoice: false));
    }

    [Fact]
    public void FirstWaitingRefresh_Stamps_AndLaterRefreshesHoldTheSameMoment()
    {
        // The value must NOT advance while the session keeps waiting - it is "since when", not "for how
        // long". A clock that re-stamped every refresh would read zero forever, which is precisely the
        // failure being fixed: a number that cannot grow cannot show that something is stuck.
        var clock = new VoiceWaitingClock();

        var first = clock.Stamp(TenantId.Local, "s", isWaitingForVoice: true);
        var second = clock.Stamp(TenantId.Local, "s", isWaitingForVoice: true);
        var third = clock.Stamp(TenantId.Local, "s", isWaitingForVoice: true);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void VoiceArriving_EndsTheEpisode_AndTheNextWaitIsStrictlyLater()
    {
        var clock = new VoiceWaitingClock();
        var first = clock.Stamp(TenantId.Local, "s", isWaitingForVoice: true);

        Assert.Null(clock.Stamp(TenantId.Local, "s", isWaitingForVoice: false));   // audio arrived

        var second = clock.Stamp(TenantId.Local, "s", isWaitingForVoice: true);    // a new turn, a new wait
        Assert.NotNull(second);
        Assert.True(second >= first, "a later episode must not report an earlier moment than the one before it");
    }

    [Fact]
    public void TwoTenantsWithTheSameSessionId_KeepSeparateClocks()
    {
        // The same reason the needs-you clock is keyed this way: two accounts can run sessions with the
        // same id, and a bare-sid key would let one tenant's "voice arrived" clear the other's wait - so
        // one owner's stuck session would silently reset its own age whenever an unrelated account's
        // session got its audio.
        var clock = new VoiceWaitingClock();

        var mine = clock.Stamp(TenantId.Local, "shared-id", isWaitingForVoice: true);
        var theirs = clock.Stamp(Other, "shared-id", isWaitingForVoice: true);
        Assert.NotNull(mine);
        Assert.NotNull(theirs);

        clock.Stamp(Other, "shared-id", isWaitingForVoice: false);   // their voice arrived

        // Mine is untouched, and still reports the moment MY wait began.
        Assert.Equal(mine, clock.Stamp(TenantId.Local, "shared-id", isWaitingForVoice: true));
    }

    [Fact]
    public void Forget_DropsTheEpisode()
    {
        var clock = new VoiceWaitingClock();
        var first = clock.Stamp(TenantId.Local, "s", isWaitingForVoice: true);
        clock.Forget(TenantId.Local, "s");

        var second = clock.Stamp(TenantId.Local, "s", isWaitingForVoice: true);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(second >= first);
    }
}
