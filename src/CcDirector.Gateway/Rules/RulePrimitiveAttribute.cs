namespace CcDirector.Gateway.Rules;

/// <summary>
/// Marks a public static method as a VERIFIED RULE PRIMITIVE - one of the small set of checks the Gateway
/// ships, wrote and reviewed, that a rule is allowed to run (owner ruling 15: no user-written code and no
/// model-written code ever runs; the model chooses one of ours by name and supplies its arguments).
///
/// The attribute carries no name and no signature. Both are DERIVED from the method itself by
/// <see cref="RulePrimitiveRegistry"/> - the wire name from the method name, the parameters and the
/// answer from the CLR types - so there is no second list to drift (Architect ruling A2). Attributing a
/// method is therefore the whole act of shipping a primitive, and un-attributing it is the whole act of
/// withdrawing one.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RulePrimitiveAttribute : Attribute
{
    /// <param name="summary">A plain-English sentence saying what the check answers, in the words a rule
    /// uses to DESCRIBE itself to the account. Required - a primitive nobody can describe cannot be shown
    /// in a rule, and the account only ever reads descriptions, never code.</param>
    /// <exception cref="ArgumentException">The summary is null, empty or whitespace.</exception>
    public RulePrimitiveAttribute(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            throw new ArgumentException("summary is required", nameof(summary));
        Summary = summary.Trim();
    }

    /// <summary>The plain-English sentence saying what this check answers.</summary>
    public string Summary { get; }
}
