using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the pure message-framing helper used by fleet session-to-session
/// messaging (issue #705). Pure and machine-independent.
///
/// The helper's production caller is the GATEWAY (its message and broadcast routes frame with it) -
/// the Director's /fleet/* relay routes that once shared it were deleted with the Director's
/// listener (Remove-the-network-port mission, phase 5), and their endpoint-validation tests went
/// with them: the request validation a caller meets now is the Gateway's own, tested on the
/// Gateway's routes. This framing helper stands on its own.
/// </summary>
public sealed class FleetMessagingFramingTests
{
    [Fact]
    public void ShortId_truncates_to_eight_characters()
    {
        Assert.Equal("4c810000", FleetMessaging.ShortId("4c810000-1111-2222"));
        Assert.Equal("abc", FleetMessaging.ShortId("abc"));
        Assert.Equal("", FleetMessaging.ShortId(null));
    }

    [Fact]
    public void BuildFramedMessage_WithName_includes_name_machine_id_and_reply_line()
    {
        var framed = FleetMessaging.BuildFramedMessage(
            "4c810000-1111-2222-3333-444444444444", "feature-work", "machine-A", "run the tests");

        Assert.StartsWith("Message ", framed);
        Assert.Contains("[message from feature-work (machine-A), id 4c810000]", framed);
        Assert.Contains("run the tests", framed);
        Assert.Contains("(to reply: cc-devthrottle message send 4c810000", framed);
    }

    [Fact]
    public void BuildFramedMessage_WithIdButNoName_uses_generic_session_header_with_reply()
    {
        var framed = FleetMessaging.BuildFramedMessage(
            "9b2f0000-aaaa-bbbb-cccc-dddddddddddd", null, "machine-B", "hello");

        Assert.Contains("[message from session 9b2f0000 (machine-B)]", framed);
        Assert.Contains("(to reply: cc-devthrottle message send 9b2f0000", framed);
    }

    [Fact]
    public void BuildFramedMessage_WithNoSender_is_anonymous_and_has_no_reply_line()
    {
        var framed = FleetMessaging.BuildFramedMessage(null, null, "machine-C", "broadcast text");

        Assert.Contains("[message from another session]", framed);
        Assert.Contains("broadcast text", framed);
        Assert.DoesNotContain("to reply:", framed);
    }

    [Fact]
    public void BuildFramedMessage_IsSingleLine_soItDeliversInlineToEveryAgent()
    {
        // A multi-line frame is routed through the @-temp-file delivery path that some agents (e.g. Pi)
        // do not expand, so they would see the file reference instead of the message. The frame - even
        // for a multi-line body - must collapse to a single line so it is typed inline.
        var framed = FleetMessaging.BuildFramedMessage(
            "4c810000-1111-2222-3333-444444444444", "asker", "machine-A",
            "Reply with\nexactly\nGREEN-42");

        Assert.DoesNotContain("\n", framed);
        Assert.Contains("Reply with exactly GREEN-42", framed); // body newlines collapsed to spaces
    }
}

