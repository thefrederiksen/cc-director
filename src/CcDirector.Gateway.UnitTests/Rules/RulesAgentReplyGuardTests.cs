using Mono.Cecil;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// NO CODE PATH COMPOSES A KEYSTROKE AT RUN TIME (phase 1), asserted against the built assembly.
///
/// The acceptance row says the text typed is the authored text, verbatim, and that no code path can
/// compose one. The evaluator test proves the first half on a firing: the stored text is typed byte for
/// byte while the reply carries a different text of its own. This proves the second half structurally:
/// the record the model's reply is read into carries EXACTLY a rule id, a decision, a citation and a
/// reason - there is no member on it a keystroke could travel in - and the evaluator reaches the send seam
/// with the rule's own stored text, not with anything read off the reply.
///
/// IT READS THE COMPILED METADATA, NOT THE SOURCE, for the same reason RulesTypeNothingGuardTests does: a
/// grep passes on an empty directory. And it is checked against a known positive first - the rule record,
/// which DOES carry the text - so a scanner that could not see a string member would fail here before it
/// certified anything.
/// </summary>
public sealed class RulesAgentReplyGuardTests
{
    private const string ReplyType = "CcDirector.Gateway.Rules.RuleAgentReply";
    private const string RuleType = "CcDirector.Gateway.Rules.SessionRule";
    private const string TextMember = "TextToType";

    private static AssemblyDefinition Gateway() =>
        AssemblyDefinition.ReadAssembly(typeof(CcDirector.Gateway.Rules.RuleEvaluator).Assembly.Location);

    private static TypeDefinition Type(AssemblyDefinition assembly, string fullName) =>
        assembly.MainModule.GetType(fullName)
        ?? throw new Xunit.Sdk.XunitException("the type " + fullName + " is not in the Gateway assembly; the guard is pointed at nothing.");

    private static string[] PublicPropertyNames(TypeDefinition type) =>
        type.Properties.Where(p => p.GetMethod is { IsPublic: true }).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>THE INSTRUMENT CHECK. The rule record does carry the text, and the scanner must see it.</summary>
    [Fact]
    public void The_scanner_sees_the_text_member_on_the_rule_which_does_carry_it()
    {
        using var assembly = Gateway();

        Assert.Contains(TextMember, PublicPropertyNames(Type(assembly, RuleType)));
    }

    /// <summary>The reply record is exactly four parts, none of them a text to type. A fifth member is a
    /// deliberate edit to this list, made in the open, never a field that quietly starts carrying a
    /// keystroke.</summary>
    [Fact]
    public void The_reply_the_model_gives_has_no_member_a_keystroke_could_travel_in()
    {
        using var assembly = Gateway();

        var members = PublicPropertyNames(Type(assembly, ReplyType));

        Assert.Equal(new[] { "Decision", "Quote", "Reason", "RuleId" }, members);
        Assert.DoesNotContain(members, m => m.Contains("Type", StringComparison.OrdinalIgnoreCase) || m.Contains("Text", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The evaluator reads the rule's stored text. A method body that never loads
    /// SessionRule.TextToType would have to be typing something else.</summary>
    [Fact]
    public void The_evaluator_reads_the_stored_text_off_the_rule()
    {
        using var assembly = Gateway();
        var evaluator = Type(assembly, "CcDirector.Gateway.Rules.RuleEvaluator");

        // The pass body lives in a compiler-generated state machine nested inside the evaluator, so every
        // nested type's methods are scanned too.
        var methods = evaluator.Methods.Concat(evaluator.NestedTypes.SelectMany(t => t.Methods)).Where(m => m.HasBody);
        var reads = methods
            .SelectMany(m => m.Body.Instructions)
            .Select(i => i.Operand as MethodReference)
            .Where(r => r is not null)
            .Count(r => r!.DeclaringType.FullName == RuleType && r.Name == "get_" + TextMember);

        Assert.True(reads > 0, "the evaluator never reads " + RuleType + "." + TextMember + ", so whatever it types is not the stored text.");
    }
}
