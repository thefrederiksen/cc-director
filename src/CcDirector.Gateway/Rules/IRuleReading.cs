namespace CcDirector.Gateway.Rules;

/// <summary>
/// WHAT THE EVALUATION PATH IS ALLOWED TO SEE OF THE STORE: read the rules, count a rule's firings, write
/// one down. There is deliberately no <c>Create</c>, no <c>Delete</c> and - the one that matters - no
/// <c>Promote</c> on it.
///
/// This interface exists because of a finding, and the finding is worth keeping written down. Through
/// phase 2 the production wiring held the concrete <c>SessionRuleStore</c>, whose <c>Promote</c> took a
/// rule id and a timestamp and nothing else. Nothing called it, so every test stayed green and the code
/// read as safe. But bound 6 - a rule never promotes itself - was one line away from being false at any
/// moment, and a bound that depends on nobody adding a line is not a bound.
///
/// So the evaluation path is handed this instead, and <c>RulesPromotionBoundaryGuardTests</c> asserts
/// against the built assembly that nothing in the feature holds the concrete store. Adding a promotion
/// method here is therefore a visible, reviewable act rather than a quiet one.
/// </summary>
public interface IRuleReading
{
    /// <summary>Every rule the account has, newest first.</summary>
    IReadOnlyList<SessionRule> All();

    /// <summary>Every firing of one rule, newest first - what the cooldown and the daily cap are counted
    /// from.</summary>
    IReadOnlyList<SessionRuleFiring> FiringsFor(Guid ruleId);

    /// <summary>Write one firing down. The record is the product, so this is the one write the evaluation
    /// path may do.</summary>
    /// <exception cref="RuleRejectedException">There is no such rule, or the record is not a record of
    /// anything - the reason says which.</exception>
    SessionRuleFiring RecordFiring(
        Guid ruleId,
        string sessionId,
        string screenText,
        string understanding,
        string decision,
        string reason,
        IEnumerable<RulePrimitiveRun> primitiveRuns,
        string typedText,
        string outcome,
        string grounding,
        DateTime nowUtc);
}
