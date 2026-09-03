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
    /// path may do. It is written BEFORE the keystroke and completed after - see
    /// <see cref="CompleteFiring"/>.</summary>
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

    /// <summary>
    /// Say what became of a firing that was written down BEFORE its keystroke went out. The record exists
    /// first and is reconciled afterwards, so there is no moment in which something has happened to a
    /// person's session and nothing durable says so.
    /// </summary>
    /// <param name="firingId">The firing written before the send.</param>
    /// <param name="typedText">What actually reached the session - empty unless something confirmed it.</param>
    /// <param name="outcome">What happened, in the words a person reads.</param>
    /// <param name="nowUtc">Now.</param>
    /// <exception cref="RuleRejectedException">There is no such firing, the outcome says nothing, or a
    /// dry-run rule's firing was told it typed something.</exception>
    SessionRuleFiring CompleteFiring(Guid firingId, string typedText, string outcome, DateTime nowUtc);
}
