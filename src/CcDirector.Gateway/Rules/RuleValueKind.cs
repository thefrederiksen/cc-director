namespace CcDirector.Gateway.Rules;

/// <summary>
/// The closed set of value kinds a verified rule primitive can take as a parameter or hand back as an
/// answer (ruling 15). A kind is never written down twice: it is DERIVED from the primitive method's own
/// CLR types by <see cref="RulePrimitiveRegistry"/>, so a primitive's signature in the source is the only
/// place its shape is stated. A parameter type outside this set is refused when the registry is built -
/// loudly, at startup - rather than being quietly mapped onto something close.
/// </summary>
public enum RuleValueKind
{
    /// <summary>A single piece of text (CLR <see cref="string"/>).</summary>
    Text,

    /// <summary>An ordered list of literal terms (CLR <c>IReadOnlyList&lt;string&gt;</c>). Terms are
    /// compared literally - never as a pattern, an expression or a format string.</summary>
    TextList,

    /// <summary>A moment in time, always UTC (CLR <see cref="DateTime"/>).</summary>
    Timestamp,

    /// <summary>One member of the closed <see cref="RuleExtractKind"/> set.</summary>
    ExtractKind,

    /// <summary>A yes or no answer (CLR <see cref="bool"/>). Answers only - no primitive takes one.</summary>
    Boolean,

    /// <summary>A number of seconds (CLR <see cref="double"/>). Answers only.</summary>
    Seconds,

    /// <summary>A number of seconds, or nothing at all (CLR <c>double?</c>). Answers only.</summary>
    OptionalSeconds,
}
