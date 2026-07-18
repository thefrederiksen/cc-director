namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The hero metric of the weekly Outcome Ledger (issue #1771): verified yield. Deliberately NOT a bare
/// percent - a headline metric is never shown without its denominator, so this carries the numerator and
/// denominator ("6 of 8 delivered") and the excused count, and a client renders the ratio from them.
///
/// Numerator = runs accepted (an evidenced, accepted outcome). Denominator = runs that reached a terminal
/// outcome in the window and were NOT waived. Waived runs are EXCLUDED from the denominator (a run we
/// explicitly decided need not count - e.g. exploratory) and disclosed separately, so a waiver neither helps
/// nor hurts the yield.
/// </summary>
public sealed class VerifiedYieldDto
{
    /// <summary>Numerator: runs with an accepted outcome in the window.</summary>
    public int AcceptedRuns { get; set; }

    /// <summary>Denominator: runs that reached a terminal outcome in the window, excluding waived.</summary>
    public int EffortRuns { get; set; }

    /// <summary>Runs waived in the window - excluded from the denominator, disclosed so the number is honest.</summary>
    public int WaivedRuns { get; set; }

    /// <summary>Runs rejected in the window - these stay in the denominator as "did not deliver".</summary>
    public int RejectedRuns { get; set; }
}

/// <summary>
/// One run's line in the Outcome Ledger, carrying its cost (token effort) and its attention-burden beside it.
/// Aggregated per RUN - never per person (issue #1771 principle). Cost is expressed in tokens with a coverage
/// flag, not dollars: per-session dollars wait on the #1608 rate card, and subscription traffic has no
/// marginal dollar. The account-level hosted-AI dollars are a separate report line, never pinned to a run.
/// </summary>
public sealed class OutcomeLedgerRowDto
{
    public Guid RunId { get; set; }
    public string RunName { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public string? RepoPath { get; set; }
    public string Status { get; set; } = "";
    public string AcceptanceStatus { get; set; } = "";
    public DateTime? CompletedUtc { get; set; }

    /// <summary>How many sessions joined this run (the effort join, from the run's participants).</summary>
    public int ParticipantSessions { get; set; }

    /// <summary>Cumulative output tokens across the run's participant sessions (0 when none captured).</summary>
    public long OutputTokens { get; set; }

    /// <summary>Cumulative input tokens across the run's participant sessions.</summary>
    public long InputTokens { get; set; }

    /// <summary>False when any participant session's additive token spend was NOT captured (a context-gauge-only
    /// driver): the token cost is then a floor, not a total, and the report discloses it rather than read it as
    /// complete.</summary>
    public bool TokenCoverageComplete { get; set; }

    /// <summary>Count of intervention audit events for this run's sessions in the window (the agent needed a
    /// human) - an attention-burden signal.</summary>
    public int InterventionCount { get; set; }

    /// <summary>Total seconds this run's sessions spent waiting on a human in the window (from the event
    /// ledger) - the attention-burden duration a manager actually feels.</summary>
    public long WaitingOnHumanSeconds { get; set; }
}

/// <summary>
/// The weekly Outcome Ledger (issue #1771, spine item 4) - the first report that pays rent. It answers both
/// "where did my week go" and "where did value leak" by putting accepted runs, aging work-in-progress, and
/// high-effort/no-outcome runs side by side, each with its cost and attention-burden, under the verified-yield
/// headline. Aggregated by run/repo/workflow, NEVER by person, and it discloses its own coverage so a low
/// number is never mistaken for a good one.
/// </summary>
public sealed class OutcomeLedgerReportDto
{
    public DateTime SinceUtc { get; set; }
    public DateTime UntilUtc { get; set; }

    /// <summary>The hero metric: verified yield, as numerator + denominator (never a bare percent).</summary>
    public VerifiedYieldDto VerifiedYield { get; set; } = new();

    /// <summary>Delivered: runs accepted in the window.</summary>
    public List<OutcomeLedgerRowDto> Delivered { get; set; } = new();

    /// <summary>Aging work-in-progress: runs that succeeded but are still not accepted - the acceptance backlog.</summary>
    public List<OutcomeLedgerRowDto> AgingWip { get; set; } = new();

    /// <summary>High-effort / no-outcome: runs that consumed effort in the window and ended without an accepted
    /// outcome (failed, abandoned, or rejected) - the value leak.</summary>
    public List<OutcomeLedgerRowDto> HighEffortNoOutcome { get; set; } = new();

    /// <summary>The account-level hosted-AI service dollars for the window - a real, separate line, never
    /// blended into per-run cost.</summary>
    public AccountHostedAiSpendSummaryDto HostedAiServices { get; set; } = new();

    /// <summary>Spend coverage disclosure over the window's sessions - how much of the effort is captured in
    /// tokens vs not captured at all, so the cost figures are read honestly.</summary>
    public SpendCoverageDto SpendCoverage { get; set; } = new();
}
