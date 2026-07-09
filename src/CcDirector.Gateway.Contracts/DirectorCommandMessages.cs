namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Issue #1177 (Phase 1): a command the Gateway sends DOWN a Director's stream. <see cref="Verb"/> selects
/// the handler; <see cref="PayloadJson"/> is the serialized request DTO (or "" when the verb takes none).
/// <see cref="CommandId"/> is a correlation/idempotency id - SignalR's <c>InvokeAsync&lt;T&gt;</c> already
/// correlates each request to its reply, so CommandId is for logging today and future idempotency.
///
/// This is the down-channel twin of the UP-channel snapshot/delta/remove messages: it rides the SAME
/// outbound-dialed connection (the Director dials the Gateway; the Gateway never dials the Director), and
/// the Director executes it through the SAME in-process handlers the Control API endpoints use, so the
/// stream path and the REST path cannot drift.
/// </summary>
public sealed class DirectorCommand
{
    /// <summary>Correlation/idempotency id. Used for logging today; reserved for future idempotency.</summary>
    public string CommandId { get; set; } = "";

    /// <summary>Selects the handler on the Director (e.g. "prompt", "interrupt", "hold").</summary>
    public string Verb { get; set; } = "";

    /// <summary>The target session id, or "" for a verb that does not address one session.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The serialized request DTO for the verb, or "" when the verb takes no body.</summary>
    public string PayloadJson { get; set; } = "";
}

/// <summary>
/// The outcome category of a <see cref="DirectorCommand"/>. The stream path has no HTTP status codes, so
/// the result carries the outcome and the REST layer maps it back to the matching <c>Results.*</c>.
/// </summary>
public enum DirectorCommandStatus
{
    Ok = 0,
    BadRequest = 1,
    NotFound = 2,
    Conflict = 3,
    Error = 4,
}

/// <summary>
/// Issue #1177 (Phase 1): the reply to a <see cref="DirectorCommand"/>. <see cref="BodyJson"/> carries the
/// verb's serialized response DTO on success; <see cref="Error"/> carries a message on failure.
/// </summary>
public sealed class DirectorCommandResult
{
    /// <summary>Echoes the originating command's <see cref="DirectorCommand.CommandId"/> for correlation.</summary>
    public string CommandId { get; set; } = "";

    /// <summary>The outcome category.</summary>
    public DirectorCommandStatus Status { get; set; }

    /// <summary>The verb's serialized response DTO on success; null when the verb has no body or on failure.</summary>
    public string? BodyJson { get; set; }

    /// <summary>A human-readable error message on failure; null on success.</summary>
    public string? Error { get; set; }

    /// <summary>True when the command succeeded.</summary>
    public bool Ok => Status == DirectorCommandStatus.Ok;

    /// <summary>Build a success result, optionally carrying a serialized response body.</summary>
    public static DirectorCommandResult Success(string? bodyJson = null) =>
        new() { Status = DirectorCommandStatus.Ok, BodyJson = bodyJson };

    /// <summary>Build a failure result with the given status and message.</summary>
    public static DirectorCommandResult Fail(DirectorCommandStatus status, string error) =>
        new() { Status = status, Error = error };
}
