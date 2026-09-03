using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Rules;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// GROUNDING EVIDENCE FOR A TEST, MINTED THE ONLY WAY IT CAN BE (fix round E, ruling E1): through a real
/// <see cref="RuleAuthor"/> reading a screen on which every word appears. There is deliberately no
/// shortcut here that reaches the evidence type's factory directly - a helper that could mint evidence
/// without a screen read would be a second door in the tests, and a test that passed through it would
/// prove nothing about the store's invariant.
/// </summary>
internal static class Grounded
{
    /// <summary>Evidence for exactly these words, from a screen that shows all of them.</summary>
    public static RuleGroundingEvidence For(IEnumerable<string> words, string sessionId = "sid-grounded")
    {
        var list = words.ToList();
        var screen = "> carry on\n\n" + string.Join("\n", list) + "\n\n>";
        var author = new RuleAuthor(
            (_, _, _) => Task.FromResult<string?>(null),
            (_, sid, _) => Task.FromResult(RuleScreenResult.Read(
                new RuleScreenReading(sid, new RuleSessionOrigin("ClaudeCode", "TEST"), screen))));

        var grounding = author.GroundAsync(TenantId.Local, sessionId, list, scope: null, allAgents: true, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (grounding.Evidence is null)
            throw new InvalidOperationException("the test screen did not ground the words: " + grounding.Refusal);
        return grounding.Evidence;
    }

    /// <summary>Evidence for exactly these words.</summary>
    public static RuleGroundingEvidence For(params string[] words) => For((IEnumerable<string>)words);
}
