namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The compact, speakable view of one fleet session the Car Mode brain reasons over (Car Mode mission,
/// New build A read tools). Deliberately small: the human NAME and repository the assistant must speak,
/// the state and "needs you" facts, the short one-line summary of what it is doing, and the id the act
/// tools (Phase 3) address - never the raw SessionDto. Sessions are referred to by name and what they
/// are doing, never by number, in every spoken line (decision 5), but the number is carried so the
/// brain can resolve a spoken "session one-oh-four" if the owner uses one.
/// </summary>
public sealed record CarModeSessionInfo
{
    public required string SessionId { get; init; }
    public required string Name { get; init; }
    public int? Number { get; init; }
    public required string Repo { get; init; }
    public required string MachineName { get; init; }
    public string? MissionName { get; init; }
    /// <summary>Human-readable state label, e.g. "Needs you" / "Working" / "Ready" (SessionDto.StateLabel).</summary>
    public required string State { get; init; }
    /// <summary>True when this session is waiting on the owner (TriageBucket == "needsYou").</summary>
    public bool NeedsYou { get; init; }
    /// <summary>How long it has been waiting on the owner, in whole minutes; 0 when not waiting.</summary>
    public int WaitingMinutes { get; init; }
    /// <summary>The &lt;=8-word one-line summary of what it is doing (SessionDto.RailLine), or the short
    ///  status reason when there is no rail line. Empty when neither is available.</summary>
    public required string Summary { get; init; }
}

/// <summary>What one session is doing, for the "read me that one" / "what is X doing" read tool. For v1
/// this is the roster's own summary fields (name + repo + short line), which already answer the mission's
/// Phase 2 proof; a richer transcript read can be layered on later without changing the tool contract.</summary>
public sealed record CarModeActivity
{
    public required string SessionId { get; init; }
    public required string Name { get; init; }
    public required string Repo { get; init; }
    public required string State { get; init; }
    public required string Summary { get; init; }
    public bool NeedsYou { get; init; }
}

/// <summary>One action the brain took during a turn (Phase 3+), surfaced to the page for on-screen
/// confirmation alongside the spoken reply. The act already happened server-side.</summary>
public sealed record CarModeActionRecord(string Tool, string Summary);

/// <summary>The brain's result for one turn: what to say out loud, what it did, and whether it is holding
/// a destructive action for a spoken confirmation on the next turn (Phase 3).</summary>
public sealed record CarModeTurnResponse
{
    public required string Spoken { get; init; }
    public IReadOnlyList<CarModeActionRecord> Actions { get; init; } = Array.Empty<CarModeActionRecord>();
    public bool PendingConfirmation { get; init; }

    /// <summary>The per-stage server timing for this turn (Car Mode performance round): every hosted-model
    ///  round trip and every fleet/roster read, plus the whole-turn wall-clock. Null only on a path that did
    ///  not measure (it always measures in production). The endpoint returns it inline so the browser merges
    ///  it with its own client stamps into one telemetry record.</summary>
    public CarModeTurnTiming? Timing { get; init; }
}

/// <summary>Request body of POST /carmode/turn: the owner's transcribed command for this turn.</summary>
public sealed class CarModeTurnRequest
{
    public string Text { get; set; } = "";
}
