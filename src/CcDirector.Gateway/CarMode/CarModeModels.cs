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

    /// <summary>Whole hours since the session was created; 0 when it is under an hour old. Carried so the
    ///  brain can answer "which sessions have been open too long" with a real age, never a guess.</summary>
    public int AgeHours { get; init; }

    /// <summary>Whole minutes since the session last produced output (SessionDto.IdleSeconds); 0 when it is
    ///  active right now. Carried so the brain can tell an active session from an abandoned one.</summary>
    public int IdleMinutes { get; init; }
}

/// <summary>
/// Which surface a brain instance speaks to (Assistant on the cockpit build). The SAME loop, tools, stores,
/// and model serve both; only the system prompt's speech-style rules differ. Car is the hands-free phone
/// surface (one or two short spoken sentences); Desk is the cockpit Assistant screen (the owner is at his
/// computer, typing or talking, and the reply is shown as text and may also be read aloud).
/// </summary>
public enum CarModeSurface
{
    Car,
    Desk,
}

/// <summary>The account credit balance for the get_credits read tool, from GET /account/credits. SignedIn
/// false means the Gateway holds no account credential - there IS no balance, and the brain says so rather
/// than inventing a zero (the endpoint never fabricates a balance and neither does the tool).</summary>
public sealed record CarModeCredits(bool SignedIn, long? BalanceMicros, long? LastDebitMicros);

/// <summary>One machine running Directors, for the list_machines read tool: the machine name the owner says
/// out loud, how recently the Gateway heard from it, and how many fleet sessions it is running now.</summary>
public sealed record CarModeMachineInfo
{
    public required string MachineName { get; init; }
    public required string Version { get; init; }
    /// <summary>Whole minutes since the Gateway last reached a Director on this machine; null when the
    ///  registry carries no last-seen time for it.</summary>
    public int? LastSeenMinutesAgo { get; init; }
    /// <summary>How many roster sessions are on this machine right now.</summary>
    public int SessionCount { get; init; }
}

/// <summary>One scheduled job for the list_schedules read tool, from GET /cron/jobs: what it is called,
/// whether it is on, when it runs (a cron expression or a one-off time), where, and what it does.</summary>
public sealed record CarModeScheduleInfo
{
    public required string Name { get; init; }
    public bool Enabled { get; init; }
    /// <summary>The human schedule: the cron expression for a recurring job, or "once at ..." for a one-off.</summary>
    public required string Schedule { get; init; }
    public required string Machine { get; init; }
    /// <summary>A one-line summary of what the fire does (the seed prompt or the work list it drains).</summary>
    public required string ActionSummary { get; init; }
    public DateTime? NextRunUtc { get; init; }
    public DateTime? LastFiredUtc { get; init; }
    public string? LastStatus { get; init; }
}

/// <summary>The hosted AI spend total over a trailing window, for the get_spend read tool, from
/// GET /gateway/governance/hosted-ai-spend/summary.</summary>
public sealed record CarModeSpendSummary(long TotalMicros, int DebitCount, DateTime SinceUtc, DateTime UntilUtc);

/// <summary>
/// A fleet tool that is KNOWINGLY unavailable on this deployment (issue #2129: per-tenant credits and
/// spend are not served on the hosted Gateway yet). Distinct from a genuine failure on purpose: the brain
/// converts this into a tool-error result the model RELAYS in plain words ("credits are not available
/// here yet"), instead of failing the whole turn - the owner hears the truth, not an error page.
/// </summary>
public sealed class CarModeToolUnavailableException : Exception
{
    public CarModeToolUnavailableException(string message) : base(message) { }
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

/// <summary>The spoken narration for one session, for the "what does it need" / "read me the wingman" read
/// tool (Voice-screen-actions phase). Backed by the SAME POST /wingman/explain the Voice screen's onSwitchOn
/// path calls: <see cref="Spoken"/> is the real, current narration the assistant reads aloud (never a canned
/// "it is waiting for you"), and <see cref="NothingYet"/> is true for a fresh/text-only session with nothing
/// to summarize yet (in which case Spoken carries the truthful "nothing to read yet" line).</summary>
public sealed record CarModeExplain(string Spoken, bool NothingYet);

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
    ///  it with its own client stamps into one local diagnostics record.</summary>
    public CarModeTurnTiming? Timing { get; init; }
}

/// <summary>Request body of POST /carmode/turn: the owner's transcribed command for this turn.</summary>
public sealed class CarModeTurnRequest
{
    public string Text { get; set; } = "";
}
