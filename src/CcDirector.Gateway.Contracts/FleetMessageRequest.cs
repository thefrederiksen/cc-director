namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /fleet/send on the Director (issue #705). A session asks its own Director to
/// deliver a message to another session anywhere in the fleet. The Director delivers locally
/// when the target lives on this machine, otherwise relays through the Gateway - the fleet
/// token never reaches the calling agent.
/// </summary>
public sealed class FleetSendRequest
{
    /// <summary>Target session GUID anywhere in the fleet.</summary>
    public string ToSessionId { get; set; } = "";

    /// <summary>The message text.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// The calling session's own GUID (its CC_SESSION_ID). Used to stamp the sender header so the
    /// recipient knows who to reply to. The display name is resolved by the Director from its own
    /// session record, never trusted from the caller. Optional - an unknown sender is framed
    /// generically.
    /// </summary>
    public string? FromSessionId { get; set; }
}

/// <summary>
/// Body of POST /fleet/broadcast on the Director (issue #705). Sends one message to every other
/// session in the fleet.
/// </summary>
public sealed class FleetBroadcastRequest
{
    /// <summary>The message text.</summary>
    public string Text { get; set; } = "";

    /// <summary>The calling session's own GUID (its CC_SESSION_ID); excluded from the recipients
    /// and used to stamp the sender header.</summary>
    public string? FromSessionId { get; set; }

    /// <summary>
    /// Issue #1229: when false (the default), the broadcast reaches only the sender's own team - the
    /// sessions sharing its group, or (for a solo session) the sessions in the same repository on the
    /// same machine. When true, the caller is explicitly asking to reach the WHOLE fleet, which the
    /// Gateway Hub allows only with a valid <see cref="GrantId"/> and a <see cref="Reason"/>.
    /// </summary>
    public bool Everyone { get; set; }

    /// <summary>Issue #1229: why a fleet-wide broadcast is warranted. Required (with a grant) when
    /// <see cref="Everyone"/> is true; logged by the Hub and surfaced to the human.</summary>
    public string? Reason { get; set; }

    /// <summary>Issue #1229: a human-issued broadcast grant id authorizing a fleet-wide broadcast.
    /// Null for the ordinary team-scoped broadcast.</summary>
    public string? GrantId { get; set; }
}

/// <summary>Response from POST /fleet/send and POST /fleet/broadcast.</summary>
public sealed class FleetSendResponse
{
    /// <summary>True when the message was accepted for delivery.</summary>
    public bool Accepted { get; set; }

    /// <summary>How many sessions the message was delivered to.</summary>
    public int DeliveredCount { get; set; }

    /// <summary>Non-blocking note for an accepted send - e.g. a team-scoped broadcast (issue #1229)
    /// that matched no other team member. Null when there is nothing to note.</summary>
    public string? Warning { get; set; }

    /// <summary>Error message when Accepted is false.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Body of POST /fleet/ask on the Director (issue #717). A session asks a question to one target
/// session anywhere in the fleet and waits for the target's answer (its turn output). The Director
/// relays to the Gateway, which holds the response open until the target returns to Idle or the
/// timeout elapses; standalone, the Director captures a local target's reply itself.
/// </summary>
public sealed class FleetAskRequest
{
    /// <summary>Target session GUID anywhere in the fleet.</summary>
    public string ToSessionId { get; set; } = "";

    /// <summary>The question text.</summary>
    public string Question { get; set; } = "";

    /// <summary>The calling session's own GUID (its CC_SESSION_ID); stamps the sender header on the
    /// delivered question. The display name is resolved by the Director, never trusted from the body.</summary>
    public string? FromSessionId { get; set; }

    /// <summary>How long to wait for the target's answer, in milliseconds. Default 120000 (2 min).</summary>
    public int TimeoutMs { get; set; } = 120_000;
}

/// <summary>Response from POST /fleet/ask.</summary>
public sealed class FleetAskResponse
{
    /// <summary>True when the target produced an answer within the timeout.</summary>
    public bool Answered { get; set; }

    /// <summary>The target's answer (its turn output), when Answered is true.</summary>
    public string Answer { get; set; } = "";

    /// <summary>Outcome of the wait: idle (answered) | timeout | failed | not_found.</summary>
    public string Status { get; set; } = "";

    /// <summary>Error or timeout message when Answered is false.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Body of POST /fleet/rename on the Director (issue #1490). A session asks its own Director to rename a
/// session anywhere in the fleet: renamed locally when the target lives on this machine, otherwise relayed
/// through the Gateway (PATCH /sessions/{sid}), so the fleet token never reaches the calling agent. The
/// tunnel-only floor restores this off the PATCH /sessions/{sid} route the cut removed from the Director.
/// </summary>
public sealed class FleetRenameRequest
{
    /// <summary>Target session GUID anywhere in the fleet.</summary>
    public string ToSessionId { get; set; } = "";

    /// <summary>New custom display name, or empty/null to clear it back to the default (repo folder name).</summary>
    public string? Name { get; set; }
}

/// <summary>Response from POST /fleet/rename.</summary>
public sealed class FleetRenameResponse
{
    /// <summary>True when the rename was applied.</summary>
    public bool Renamed { get; set; }

    /// <summary>The renamed session's GUID.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The session's resolved display name after the rename.</summary>
    public string Name { get; set; } = "";

    /// <summary>Error message when Renamed is false.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Body of POST /fleet/prompt on the Director: send raw text into a session anywhere in the fleet. Unlike
/// <see cref="FleetSendRequest"/> this does NOT frame the text with a sender - it is exactly what a human
/// typing into the session would produce. Restores the old POST /sessions/{sid}/prompt to the loopback
/// surface the tunnel-only cut removed it from.
/// </summary>
public sealed class FleetPromptRequest
{
    /// <summary>Target session GUID anywhere in the fleet.</summary>
    public string ToSessionId { get; set; } = "";

    /// <summary>The text to send. Required.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// Whether to press Enter after the text. Defaults to true - a prompt normally submits. Named to match
    /// <see cref="PromptRequest.AppendEnter"/>, which this is passed straight through to.
    /// </summary>
    public bool AppendEnter { get; set; } = true;
}

/// <summary>
/// Body of a Director fleet verb whose only input is the target session (for example POST
/// /fleet/interrupt).
/// </summary>
public sealed class FleetTargetRequest
{
    /// <summary>Target session GUID anywhere in the fleet.</summary>
    public string ToSessionId { get; set; } = "";
}

/// <summary>
/// Body of POST /fleet/hold on the Director: park a session, or release it. Restores the old POST
/// /sessions/{sid}/hold to the loopback surface.
/// </summary>
public sealed class FleetHoldRequest
{
    /// <summary>Target session GUID anywhere in the fleet.</summary>
    public string ToSessionId { get; set; } = "";

    /// <summary>True to hold (the default), false to release.</summary>
    public bool OnHold { get; set; } = true;

    /// <summary>Optional timed snooze in minutes; null holds until something lifts it.</summary>
    public int? SnoozeMinutes { get; set; }
}

/// <summary>
/// Body of POST /fleet/role on the Director. Declares a session's EXPLICIT role after birth - the set-role
/// verb the tunnel-only cut removed with POST /sessions/{sid}/role, leaving a session stuck with whatever
/// role it was born with. Architect cannot be derived from the spawn graph, so without this a running
/// session can never become one.
/// </summary>
public sealed class FleetRoleRequest
{
    /// <summary>Target session GUID (defaults, at the CLI, to the caller itself).</summary>
    public string ToSessionId { get; set; } = "";

    /// <summary>
    /// One of <see cref="SessionRoles.All"/> (case-insensitive). An empty or null value CLEARS the explicit
    /// role, reverting the session to auto-derivation. An unknown value is REJECTED as a bad request so a
    /// mistyped role never silently drops.
    /// </summary>
    public string? Role { get; set; }
}

/// <summary>Response from POST /fleet/role.</summary>
public sealed class FleetRoleResponse
{
    /// <summary>True when the explicit role was applied.</summary>
    public bool Applied { get; set; }

    /// <summary>The target session's GUID.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The explicit role after the call, or null when it was cleared.</summary>
    /// <remarks>
    /// The EFFECTIVE role is deliberately not returned here. Worker and Manager are derived from the
    /// fleet-wide spawn graph, which only the Gateway holds - a Director cannot compute it alone, so the
    /// field would always be null. Read the effective role from the roster (GET /fleet/sessions), which
    /// relays to the Gateway.
    /// </remarks>
    public string? ExplicitRole { get; set; }

    /// <summary>Error message when Applied is false.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Body of POST /fleet/done on the Director (issue #1490). Flags a session anywhere in the fleet for
/// asynchronous teardown by its owning Director's deletion reaper: flagged locally when the target lives on
/// this machine, otherwise relayed through the Gateway (POST /sessions/{sid}/request-deletion). Restores the
/// CLI self-reap (`cc-devthrottle session done`) off the route the tunnel-only cut removed from the Director.
/// </summary>
public sealed class FleetDoneRequest
{
    /// <summary>Target session GUID anywhere in the fleet (usually the caller flagging itself).</summary>
    public string ToSessionId { get; set; } = "";

    /// <summary>Short human reason for the teardown, surfaced while the session winds down. Optional.</summary>
    public string? Reason { get; set; }
}

/// <summary>Response from POST /fleet/done.</summary>
public sealed class FleetDoneResponse
{
    /// <summary>True when the session was flagged for deletion.</summary>
    public bool Accepted { get; set; }

    /// <summary>Error message when Accepted is false.</summary>
    public string? Error { get; set; }
}
