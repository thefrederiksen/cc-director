using Mono.Cecil;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE GROUNDING EVIDENCE HAS ONE MINTER, AND THE STORE HAS ONE PRODUCTION CALLER (fix round E, ruling E1).
/// Read off the BUILT Gateway assembly, exactly as <see cref="RulesPromotionBoundaryGuardTests"/> reads
/// the promotion bound, because the two are the same mechanism on purpose: evidence only one path can
/// mint, demanded by the store and by the context's gate. A convention that "only the create route calls
/// the store" is what an inspection walked round; a structural assertion is what makes it a bound.
///
/// Every assertion here is a PRESENCE first: the expected minter and the expected caller must be in the
/// list, so an enumeration that read nothing cannot certify that nothing else was found.
/// </summary>
public sealed class RulesGroundingBoundaryGuardTests
{
    private const string TheEvidence = "CcDirector.Gateway.Rules.RuleGroundingEvidence";
    private const string TheMint = "CcDirector.Gateway.Rules.RuleGroundingEvidence::Minted";
    private const string TheAuthor = "CcDirector.Gateway.Rules.RuleAuthor";
    private const string TheStore = "CcDirector.Gateway.Rules.SessionRuleStore";
    private const string TheCreateCall = "CcDirector.Gateway.Rules.SessionRuleStore::Create";
    private const string TheCreateEndpoint = "CcDirector.Gateway.Api.SessionRuleEndpoints";

    private static List<string> BodiesMentioning(string member)
    {
        var found = new List<string>();
        foreach (var type in TheBuiltGatewayAssembly.AllTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference called) continue;
                    var name = (called.DeclaringType?.FullName ?? "") + "::" + called.Name;
                    if (!name.StartsWith(member, StringComparison.Ordinal)) continue;
                    found.Add(TheBuiltGatewayAssembly.Outermost(type).FullName);
                    break;
                }
            }
        }
        return found.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void The_only_production_code_that_can_mint_grounding_evidence_is_the_author()
    {
        var minters = BodiesMentioning(TheMint)
            .Where(t => !t.StartsWith(TheEvidence, StringComparison.Ordinal))
            .ToList();

        Assert.Contains(TheAuthor, minters);
        Assert.Equal(new[] { TheAuthor }, minters);
    }

    [Fact]
    public void Grounding_evidence_cannot_be_constructed_by_anything_but_itself()
    {
        var builders = BodiesMentioning(TheEvidence + "::.ctor")
            .Where(t => !t.StartsWith(TheEvidence, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(builders);
        // The instrument: the type's own factory does call its constructor, so the scanner sees bodies.
        Assert.Contains(TheEvidence, BodiesMentioning(TheEvidence + "::.ctor"));
    }

    [Fact]
    public void The_only_production_code_that_can_call_create_on_the_store_is_the_create_endpoint()
    {
        var callers = BodiesMentioning(TheCreateCall)
            .Where(t => !t.StartsWith(TheStore, StringComparison.Ordinal))
            .ToList();

        Assert.Contains(TheCreateEndpoint, callers);
        Assert.Equal(new[] { TheCreateEndpoint }, callers);
    }
}
