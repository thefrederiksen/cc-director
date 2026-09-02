namespace CcDirector.Gateway.Rules;

/// <summary>
/// What a rule may decide, as stored on a firing. A closed set, because the record is read by people and by
/// the free checks: the cooldown and the daily cap count <see cref="Act"/> and nothing else.
/// </summary>
public static class RuleDecisions
{
    /// <summary>The instruction applied and the agent composed something to type. In dry run nothing is
    /// typed, but the decision is still an act and still counts against the ceiling.</summary>
    public const string Act = "act";

    /// <summary>The agent read the screen against the instruction and decided the instruction does not
    /// reach it. A first-class outcome, always written down.</summary>
    public const string Decline = "decline";

    /// <summary>The act was given up before the keystroke - the screen moved on, or a check the agent
    /// staked its decision on did not hold.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>The reply named something it was not offered, or was not an answer at all. Never read as
    /// permission, and never swallowed.</summary>
    public const string Refused = "refused";
}

/// <summary>The facts about one session a rule's scope is matched against, read from the pushed roster
/// snapshot rather than by dialing the session.</summary>
public sealed record RuleSessionFacts(
    string SessionId,
    string Agent,
    string RepositoryPath,
    string Machine,
    string Mission,
    string ActivityState);

/// <summary>One rule that was considered and passed over, with the reason it was passed over.</summary>
public sealed record RuleSkipped(Guid RuleId, string Reason);

/// <summary>
/// The answer of the free checks: the rules worth paying a model call for, the ones that were passed over
/// with their reasons, and - when the pass stopped before any rule was considered at all - why it stopped.
/// </summary>
public sealed record RuleCandidates(
    IReadOnlyList<SessionRule> Chosen,
    IReadOnlyList<RuleSkipped> Skipped,
    string? StoppedBecause);

/// <summary>
/// THE FREE CHECKS. Cheap pure code that runs on every idle transition and decides whether any rule could
/// possibly apply, so the common case - a session that finished a turn normally - costs one screen read and
/// nothing else. No model is reached from here.
///
/// Everything it turns down it turns down OUT LOUD. A filter that answered with an empty list would read
/// exactly the same whether it had considered every rule and rejected each one or thrown on the first, so
/// each rule leaves either as a candidate or as a <see cref="RuleSkipped"/> carrying its reason.
/// </summary>
public static class RuleCandidateFilter
{
    /// <summary>The activity state a session reports while it is running a turn.</summary>
    public const string WorkingState = "Working";

    /// <summary>Stated when a session is still working, which is before any rule is looked at.</summary>
    public const string SessionIsWorking =
        "the session is working, so no rule is evaluated - a rule only ever looks at an idle session";

    /// <summary>Stated when the screen is the same one already evaluated for this session.</summary>
    public const string ScreenUnchanged =
        "the screen has not changed since this session was last looked at";

    /// <summary>Stated when there is nothing on the screen to read.</summary>
    public const string ScreenIsEmpty =
        "there is nothing on the screen to read, and an empty screen is not evidence";

    /// <summary>Choose the rules worth a model call for this screen.</summary>
    public static RuleCandidates Choose(
        IReadOnlyList<SessionRule> rules,
        RuleSessionFacts facts,
        string screenText,
        string? previousScreenText,
        Func<Guid, IReadOnlyList<SessionRuleFiring>> firingsFor,
        DateTime nowUtc) => throw new NotImplementedException();
}
