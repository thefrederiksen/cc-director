namespace CcDirector.Gateway.Rules;

/// <summary>
/// THE EVIDENCE THAT A PERSON ASKED FOR A RULE TO GO LIVE. Dry run is the owner's most important bound -
/// it is what puts a human between a standing instruction and its first real use - and bound 6 forbids a
/// rule promoting itself. Before this type existed, <c>Promote</c> took a rule id and a timestamp and
/// nothing else, so any code that could read rules could also move one to live. The independent inspection
/// of landing A found that, and it was right: nothing about the caller in that test made it a person.
///
/// WHAT THIS ACTUALLY ENFORCES, STATED EXACTLY, because a security bound that is described more broadly
/// than it holds is worse than none:
///
///  - A grant cannot be constructed. The only way to obtain one is
///    <see cref="FromAuthenticatedRequest"/>, which REFUSES unless it is given an authenticated caller
///    identity that the Gateway's own request pipeline resolved, and an acknowledgement written by whoever
///    is asking. Code with no inbound request has neither, so no code path inside this feature can mint one.
///  - A grant names ONE rule. A grant obtained for one rule cannot promote another, so a grant cannot be
///    captured and reused against a different instruction.
///  - The evaluation path cannot reach this type at all, and that is asserted against the built assembly by
///    <c>RulesPromotionBoundaryGuardTests</c> rather than left as a convention.
///
/// WHAT IT DOES NOT ENFORCE. It is not a proof that a human being was at a keyboard - nothing in a process
/// can be. It is a proof that the act was carried by an authenticated request, is attributable to the
/// caller the pipeline resolved, and cannot be performed by the code that evaluates rules. An attacker
/// already holding a device key is authentication's problem, not this bound's.
/// </summary>
public sealed class RulePromotionGrant
{
    private RulePromotionGrant(Guid ruleId, string actor, string acknowledgement, DateTime askedUtc)
    {
        RuleId = ruleId;
        Actor = actor;
        Acknowledgement = acknowledgement;
        AskedUtc = askedUtc;
    }

    /// <summary>The ONE rule this grant is evidence for.</summary>
    public Guid RuleId { get; }

    /// <summary>Who asked, as the request pipeline resolved them. Goes onto the rule, so a live rule can
    /// always say who made it live.</summary>
    public string Actor { get; }

    /// <summary>What they said when they asked. Kept verbatim.</summary>
    public string Acknowledgement { get; }

    /// <summary>When they asked (UTC).</summary>
    public DateTime AskedUtc { get; }

    /// <summary>
    /// The ONLY way to obtain a grant: from an inbound request the Gateway authenticated.
    /// </summary>
    /// <param name="ruleId">The rule the caller asked to promote.</param>
    /// <param name="callerIdentity">Who the request pipeline resolved this caller to be. Blank means the
    /// caller is not attributable, which is exactly the case a grant must refuse.</param>
    /// <param name="acknowledgement">What the person said when they asked. Required, so promoting is a
    /// deliberate sentence rather than an empty POST that could be replayed by anything.</param>
    /// <param name="askedUtc">When they asked.</param>
    /// <exception cref="RuleRejectedException">The caller is not attributable, or said nothing.</exception>
    public static RulePromotionGrant FromAuthenticatedRequest(
        Guid ruleId, string? callerIdentity, string? acknowledgement, DateTime askedUtc)
    {
        var actor = (callerIdentity ?? "").Trim();
        if (actor.Length == 0)
            throw new RuleRejectedException(
                "a rule is moved out of dry run by a person, and this request has no caller the Gateway " +
                "could name. Nothing that runs on its own can promote a rule.");

        var said = (acknowledgement ?? "").Trim();
        if (said.Length == 0)
            throw new RuleRejectedException(
                "moving a rule out of dry run is the one act that lets it type into your sessions, so it " +
                "asks you to say what you are agreeing to. An empty request promotes nothing.");

        return new RulePromotionGrant(ruleId, actor, said, askedUtc.ToUniversalTime());
    }
}
