using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// WHO a fanout claims to be from (Remove-the-network-port mission, phase 2b - inspection finding 6).
///
/// The sender id is not decoration: it selects the team scope the broadcast is judged against, and it is
/// the per-sender rate-limit bucket. A caller that can choose it can borrow another session's team and
/// escape its own rate limit. This is the same class as the message FRAMING - a claim about identity that
/// the recipient and the governor both believe - and the framing was proved end to end while this was
/// shipped with no test at all, which is why it is a pure function now rather than an inline decision
/// inside an endpoint only a host-bound integration test could reach.
/// </summary>
public sealed class FanoutSenderPinTests
{
    private const string Mine = "11111111-1111-1111-1111-111111111111";
    private const string Theirs = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void A_session_key_naming_another_session_is_overridden_to_its_own()
    {
        // The spoof: my key, their id in the body. Before the fix this was taken at face value.
        var sender = FanoutSenderPin.Resolve(Mine, Theirs);

        Assert.Equal(Mine, sender.SessionId);
        Assert.True(sender.Overridden, "an overridden claim must be visible so it can be logged");
    }

    [Fact]
    public void A_session_key_that_names_nobody_is_still_pinned_to_itself()
    {
        // The rate-limit half of the finding, and the one an "only override a mismatch" fix would miss.
        // An ABSENT sender is its own bucket, so a caller could simply omit the field to escape the
        // bucket its own id counts into. Omitting is not honesty, it is the same evasion.
        var sender = FanoutSenderPin.Resolve(Mine, null);

        Assert.Equal(Mine, sender.SessionId);
        Assert.False(sender.Overridden, "nothing was claimed, so nothing was overridden - but it is still pinned");
    }

    [Fact]
    public void A_session_key_naming_itself_is_left_alone_and_not_flagged()
    {
        // The honest caller must not be logged as an override, or the log stops meaning anything.
        var sender = FanoutSenderPin.Resolve(Mine, Mine);

        Assert.Equal(Mine, sender.SessionId);
        Assert.False(sender.Overridden);
    }

    [Fact]
    public void Case_and_padding_do_not_make_an_honest_claim_look_like_a_spoof()
    {
        // Session ids travel as text through several hands. A claim that differs only in case or
        // whitespace is the SAME session, and flagging it would fill the log with false overrides.
        var sender = FanoutSenderPin.Resolve(Mine, "  " + Mine.ToUpperInvariant() + "  ");

        Assert.Equal(Mine, sender.SessionId);
        Assert.False(sender.Overridden);
    }

    [Fact]
    public void A_device_key_keeps_the_sender_it_asked_for()
    {
        // The desktop and the phone act for the ACCOUNT, not as a session, so there is no session
        // identity to pin them to. Pinning them would break the surfaces that legitimately send on a
        // chosen session's behalf - the fix must not overreach into them.
        var sender = FanoutSenderPin.Resolve(null, Theirs);

        Assert.Equal(Theirs, sender.SessionId);
        Assert.False(sender.Overridden);
    }

    [Fact]
    public void An_empty_authenticated_id_is_treated_as_no_session_not_as_a_pin_to_nothing()
    {
        // Defensive: a blank identity must not silently blank out every sender, which would collapse
        // the whole fleet into one rate-limit bucket and one scope.
        var sender = FanoutSenderPin.Resolve("   ", Theirs);

        Assert.Equal(Theirs, sender.SessionId);
        Assert.False(sender.Overridden);
    }
}
