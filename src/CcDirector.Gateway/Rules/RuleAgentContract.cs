namespace CcDirector.Gateway.Rules;

/// <summary>
/// One reply from the agent, after every part of it has been checked against what was actually offered.
/// </summary>
public sealed record RuleAgentReply(
    Guid RuleId,
    string Understanding,
    string Decision,
    string Reason,
    IReadOnlyList<RulePrimitiveCall> Checks,
    string TextToType);

/// <summary>A reply, or a stated refusal. Never both, and never neither.</summary>
public sealed record RuleAgentReading(RuleAgentReply? Reply, string? Refusal);

/// <summary>
/// THE ONE AGENT CALL (Architect ruling A5): one question per screen covering every candidate rule, and a
/// reply whose every part is validated against what was offered.
/// </summary>
public static class RuleAgentContract
{
    /// <summary>How many lines of the screen tail the question carries.</summary>
    public const int ScreenTailLines = 40;

    /// <summary>Build the one question for this screen.</summary>
    public static string BuildPrompt(
        IReadOnlyList<SessionRule> candidates,
        IReadOnlyList<string> screenRows,
        RulePrimitiveRegistry registry) => throw new NotImplementedException();

    /// <summary>Read a reply, refusing anything that names something it was not offered.</summary>
    public static RuleAgentReading Read(
        string? raw,
        IReadOnlyList<SessionRule> offered,
        RulePrimitiveRegistry registry) => throw new NotImplementedException();
}
