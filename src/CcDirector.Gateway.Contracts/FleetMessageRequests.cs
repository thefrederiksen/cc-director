namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of <c>POST /sessions/{sid}/message</c> - one agent sending one message to one session
/// (Remove-the-network-port mission, phase 2).
///
/// THERE IS NO SENDER FIELD, AND ITS ABSENCE IS THE POINT. The sender is the session whose key
/// authenticated the request, read from the authenticated identity and never from this body. The
/// Director's loopback predecessor took a <c>fromSessionId</c> from the body, which was safe only
/// because the only thing that could reach that port was a process on the same machine. This route
/// is reachable by anything holding a session key, so a caller-supplied sender would let one agent
/// send a message wearing another agent's name - and the recipient would have no way to tell.
/// </summary>
public sealed class FleetMessageRequest
{
    /// <summary>The message body. The sender header and (for a one-way message) the reply hint are
    /// added by the Gateway.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// True for <c>message ask</c>: hold the response open until the recipient finishes and return what
    /// it printed. It also DROPS the reply hint from the frame - the asker is already waiting and reads
    /// the answer from the recipient's own output, so telling the recipient to send a separate reply
    /// makes it answer into a channel nobody is listening on.
    /// </summary>
    public bool WaitForIdle { get; set; }

    /// <summary>How long to wait when <see cref="WaitForIdle"/> is set. Default 120000 (2 minutes).</summary>
    public int TimeoutMs { get; set; } = 120_000;
}

/// <summary>
/// Body of <c>POST /fleet/broadcast</c> - one message to the sender's own TEAM (the fleet's
/// "message send all").
///
/// Like <see cref="FleetMessageRequest"/> it carries no sender: the team is resolved from the
/// authenticated session's own roster row, which is also the only way the scope decision and the
/// recipient list can be guaranteed to be about the same session.
///
/// It is a DIFFERENT TYPE from the Director's <see cref="FleetBroadcastRequest"/> rather than a reuse of
/// it, and the difference is the whole point: that one carries a caller-supplied FromSessionId, which is
/// exactly the field this route must not have. Reusing it would leave a sender field on the wire that the
/// Gateway silently ignores - a caller setting it would believe it had taken effect.
/// </summary>
public sealed class FleetTeamBroadcastRequest
{
    /// <summary>The message body. Framed with the sender's header by the Gateway.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// Reach the whole ACCOUNT rather than the sender's team. Refused unless <see cref="Reason"/> and a
    /// valid human-issued <see cref="GrantId"/> accompany it - an agent cannot mint its own grant, so
    /// this cannot become the default way to talk to the fleet.
    /// </summary>
    public bool Everyone { get; set; }

    /// <summary>Why this message needs to reach beyond the sender's team. Required with <see cref="Everyone"/>.</summary>
    public string? Reason { get; set; }

    /// <summary>The human-issued broadcast grant authorizing <see cref="Everyone"/>.</summary>
    public string? GrantId { get; set; }
}
