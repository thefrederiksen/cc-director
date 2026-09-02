namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One firing of one rule, as stored (<c>session_rule_firings</c>). THE RECORD IS THE PRODUCT (owner
/// ruling 14): rules that act while the account is asleep are only worth having if the morning says
/// exactly what happened. So a firing keeps the whole chain - what was on the screen, what the agent
/// understood, what it decided and why, which verified checks ran with what arguments and what they
/// answered, what was typed, and what happened next.
///
/// A dry-run firing is a real firing: it records what WOULD have been done and types nothing, which is
/// what makes dry run reviewable rather than invisible.
/// </summary>
public sealed class SessionRuleFiringEntity : GatewayMintedKeyEntity
{
    /// <summary>The rule that fired.</summary>
    public Guid RuleId { get; set; }

    /// <summary>The session it fired on.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>When it fired (UTC).</summary>
    public DateTime OccurredUtc { get; set; }

    /// <summary>What was on the terminal screen - the only input a rule reads (owner ruling 11).</summary>
    public string ScreenText { get; set; } = "";

    /// <summary>What the agent understood the screen to be, in its own words.</summary>
    public string Understanding { get; set; } = "";

    /// <summary>What it decided - including a decision to DECLINE, which is a first-class outcome.</summary>
    public string Decision { get; set; } = "";

    /// <summary>Why it decided that. A decline with no reason is not a record of anything.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Which verified checks ran, with what arguments, and what each answered.</summary>
    public List<RulePrimitiveRunEntity> PrimitiveRuns { get; set; } = new();

    /// <summary>What was typed into the session. EMPTY for a dry-run firing, always: a rule in dry run
    /// types nothing, and the store refuses to write a firing that claims otherwise.</summary>
    public string TypedText { get; set; } = "";

    /// <summary>What happened next, once the rule had acted or declined.</summary>
    public string Outcome { get; set; } = "";
}

/// <summary>One verified check that ran during a firing: which one, with what arguments, and what it
/// answered. Stored as part of the firing so an action nobody can reconstruct never exists.</summary>
public sealed class RulePrimitiveRunEntity
{
    /// <summary>The check's wire name, e.g. "is_path_inside".</summary>
    public string Name { get; set; } = "";

    /// <summary>The arguments as they were supplied, rendered for reading.</summary>
    public string Arguments { get; set; } = "";

    /// <summary>What the check answered, rendered for reading.</summary>
    public string Answer { get; set; } = "";
}
