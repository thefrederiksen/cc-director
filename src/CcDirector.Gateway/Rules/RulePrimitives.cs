namespace CcDirector.Gateway.Rules;

/// <summary>
/// THE VERIFIED PRIMITIVES - the entire set of checks a rule is allowed to run (owner ruling 15,
/// Architect ruling A3). These are ordinary reviewed static functions in the product, shipped like any
/// other feature and tested like any other feature. A rule never holds a program, an expression, a lambda
/// or a snippet: it holds the NAME of one of these plus argument values, validated against the signature
/// before it is ever stored. There is no interpreter, so there is no sandbox to get right.
///
/// Every method here is pure: same arguments, same answer, no clock, no filesystem write, no network. The
/// only filesystem READ is <see cref="IsPathInside"/> resolving links, which is what makes it truthful.
///
/// Widening this set is a PRODUCT CHANGE - a new reviewed method, shipped in a release. Never route around
/// a gap by adding a primitive whose argument is effectively a program (a pattern, an expression, a format
/// string); that is the interpreter coming back under another name.
/// </summary>
public static class RulePrimitives
{
    [RulePrimitive("Checks a path is inside a directory, resolving '..' and links first.")]
    public static bool IsPathInside(string target, string root) => throw new NotImplementedException();

    [RulePrimitive("Reads how long the screen says to wait before trying again.")]
    public static double? RetryDelayFrom(string screenText, DateTime now) => throw new NotImplementedException();

    [RulePrimitive("Measures how long it has been since something first went wrong.")]
    public static double ElapsedSince(DateTime firstFailure, DateTime now) => throw new NotImplementedException();

    [RulePrimitive("Checks whether the text contains any of a list of words.")]
    public static bool MatchesAny(string text, IReadOnlyList<string> terms) => throw new NotImplementedException();

    [RulePrimitive("Pulls the first path, duration or clock time out of the screen.")]
    public static string ExtractFirst(string screenText, RuleExtractKind kind) => throw new NotImplementedException();
}
