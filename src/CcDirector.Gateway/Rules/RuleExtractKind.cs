namespace CcDirector.Gateway.Rules;

/// <summary>
/// The closed set of things <c>extract_first</c> knows how to pull out of a terminal screen (ruling 15,
/// and Architect ruling A3). It is deliberately an enum and not a pattern: a primitive that accepted an
/// arbitrary expression would be the interpreter coming back under another name. Widening this set is a
/// product change - written, reviewed and shipped by us.
/// </summary>
public enum RuleExtractKind
{
    /// <summary>A filesystem path.</summary>
    Path,

    /// <summary>A span of time written out, such as "5 minutes".</summary>
    Duration,

    /// <summary>A clock time, such as "09:44".</summary>
    Timestamp,
}
