using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Rules;

/// <summary>One firing, as the evaluator hands it to whatever writes it down.</summary>
public sealed record RuleFiringDraft(
    Guid RuleId,
    string SessionId,
    string ScreenText,
    string Understanding,
    string Decision,
    string Reason,
    IReadOnlyList<RulePrimitiveRun> Runs,
    string TypedText,
    string Outcome);

/// <summary>What one evaluation pass did. A closed set, so a caller and a test both name the same thing.</summary>
public static class RulePassOutcomes
{
    /// <summary>This account has no rules at all.</summary>
    public const string NoRules = "no-rules";

    /// <summary>The session's facts could not be read - it is no longer on the roster.</summary>
    public const string SessionNotFound = "session-not-found";

    /// <summary>The screen could not be read. Unreadable is not evidence.</summary>
    public const string ScreenUnreadable = "screen-unreadable";

    /// <summary>The pass stopped before any rule was considered (working, or the screen did not change).</summary>
    public const string StoppedBeforeAnyRule = "stopped-before-any-rule";

    /// <summary>Every rule was considered and passed over, each with its reason.</summary>
    public const string NoCandidates = "no-candidates";

    /// <summary>The reply named something it was not offered, or was not an answer. Recorded as a refusal.</summary>
    public const string Refused = "refused";

    /// <summary>The agent read the screen against the instruction and declined. Recorded.</summary>
    public const string Declined = "declined";

    /// <summary>The act was given up before the keystroke. Recorded.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>The rule is in dry run: it decided to act, and typed nothing.</summary>
    public const string DryRun = "dry-run";

    /// <summary>The rule is live and the text was typed into the session.</summary>
    public const string Acted = "acted";

    /// <summary>The rule is live, the text was sent, and the send did not land.</summary>
    public const string SendFailed = "send-failed";
}

/// <summary>What one evaluation pass did, and the firings it wrote.</summary>
public sealed record RulePass(string What, string Detail, IReadOnlyList<RuleFiringDraft> Recorded);

/// <summary>
/// Everything the evaluator needs from the Gateway around it, as one seam - the same shape, and for the
/// same reason, as the supervisor's <c>ISupervisorEnvironment</c>: production wires the real store, the
/// real tunnel read, the real model and the real prompt route, and a test wires a fake with an instrumented
/// send seam, which is what makes "dry run types nothing" a counted fact rather than a missing log line.
/// </summary>
public interface IRuleEnvironment
{
    /// <summary>Every rule this account has.</summary>
    IReadOnlyList<SessionRule> Rules(TenantId tenant);

    /// <summary>Every firing of one rule, so the cooldown and the daily cap can be counted.</summary>
    IReadOnlyList<SessionRuleFiring> FiringsFor(TenantId tenant, Guid ruleId);

    /// <summary>The session's facts from the pushed roster snapshot, or null when it is no longer there.</summary>
    RuleSessionFacts? ReadSessionFacts(TenantId tenant, string sessionId);

    /// <summary>The session's LIVE screen rows, or null when it cannot be read.</summary>
    Task<IReadOnlyList<string>?> ReadScreenRowsAsync(
        TenantId tenant, string directorId, string sessionId, CancellationToken ct);

    /// <summary>Ask the agent the one question for this screen, and return its raw reply.</summary>
    Task<string?> AskAgentAsync(TenantId tenant, string prompt, CancellationToken ct);

    /// <summary>THE SEND SEAM: type text into the session. The only thing in this feature that can.</summary>
    Task<bool> TypeIntoSessionAsync(
        TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct);

    /// <summary>Write one firing down.</summary>
    void RecordFiring(TenantId tenant, RuleFiringDraft draft);

    /// <summary>The clock, as a seam.</summary>
    DateTime NowUtc { get; }
}

/// <summary>
/// THE EVALUATOR - the thin vertical slice from a session going idle to something being typed into it.
/// </summary>
public sealed class RuleEvaluator
{
    /// <param name="environment">Everything it reads and writes.</param>
    /// <param name="registry">The verified checks a reply may name. Defaults to what the product ships.</param>
    public RuleEvaluator(IRuleEnvironment environment, RulePrimitiveRegistry? registry = null)
        => _ = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>Evaluate one idle transition.</summary>
    public Task<RulePass> EvaluateAsync(
        TenantId tenant, string directorId, string sessionId, CancellationToken ct)
        => throw new NotImplementedException();
}
