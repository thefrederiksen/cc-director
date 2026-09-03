using CcDirector.Gateway.Rules;
using Mono.Cecil;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE EVALUATION PATH CANNOT PROMOTE A RULE, AND THIS PROVES IT AGAINST THE BUILT ASSEMBLY. Bound 6 says
/// the instruction is the authority and a rule never promotes itself. A comment saying so decays; a type
/// bound does not.
///
/// It is a TYPE assertion and not a call assertion, and that distinction is the whole point. Asking "does
/// anything in the rules namespace CALL Promote" passes happily on code that simply has not called it yet -
/// the production wiring held a concrete <c>SessionRuleStore</c> for the whole of phase 2 and could have
/// promoted at any time by adding one line. So the assertion is that nothing in the feature HOLDS the type
/// that can promote: the evaluation path is handed <c>IRuleReading</c>, which has no promotion on it, and
/// the concrete store appears only in its own file and in the composition root that wires the routes.
/// </summary>
public sealed class RulesPromotionBoundaryGuardTests
{
    private const string RulesNamespace = "CcDirector.Gateway.Rules";
    private const string TheStore = "CcDirector.Gateway.Rules.SessionRuleStore";
    private const string TheGrant = "CcDirector.Gateway.Rules.RulePromotionGrant";

    /// <summary>The one way to obtain the evidence that a person asked.</summary>
    private const string TheMint = "CcDirector.Gateway.Rules.RulePromotionGrant::FromAuthenticatedRequest";

    /// <summary>The one method that moves a rule out of dry run.</summary>
    private const string ThePromoteCall = "CcDirector.Gateway.Rules.SessionRuleStore::Promote";

    /// <summary>The ONLY production type allowed to reach either. It is the route a person's request
    /// arrives on, and it is named here so a second one is a failing test rather than a quiet edit.</summary>
    private const string ThePromoteEndpoint = "CcDirector.Gateway.Api.SessionRuleEndpoints";

    /// <summary>The assembly, read ONCE for the whole test process and held. Reading it per test is what
    /// pushed this project past the local gate's two-minute ceiling, and a suite that gets stopped is
    /// neither a pass nor a failure.</summary>
    private static ModuleDefinition GatewayModule() => TheBuiltGatewayAssembly.Module;

    private static TypeDefinition Outermost(TypeDefinition type) => TheBuiltGatewayAssembly.Outermost(type);

    private static string NamespaceOf(TypeDefinition type) => Outermost(type).Namespace ?? "";

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module) =>
        TheBuiltGatewayAssembly.AllTypes();

    /// <summary>Every place a type is named in another type's SHAPE - a field, a property, a method's
    /// parameters or its return - answered as "TypeFullName (where)".</summary>
    private static List<string> ShapesMentioning(
        ModuleDefinition module, string wanted, Func<TypeDefinition, bool> include)
    {
        var found = new List<string>();
        foreach (var type in AllTypes(module))
        {
            if (!include(type)) continue;
            var outer = Outermost(type).FullName;

            foreach (var field in type.Fields)
                if (Names(field.FieldType, wanted)) found.Add(outer + " (field " + field.Name + ")");

            foreach (var property in type.Properties)
                if (Names(property.PropertyType, wanted)) found.Add(outer + " (property " + property.Name + ")");

            foreach (var method in type.Methods)
            {
                if (Names(method.ReturnType, wanted)) found.Add(outer + " (returns from " + method.Name + ")");
                foreach (var parameter in method.Parameters)
                    if (Names(parameter.ParameterType, wanted))
                        found.Add(outer + " (parameter of " + method.Name + ")");
            }
        }
        return found.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether a type reference is the wanted type, or a generic built over it - a
    /// <c>Func&lt;SessionRuleStore&gt;</c> holds one exactly as a field of it does.</summary>
    private static bool Names(TypeReference reference, string wanted)
    {
        if (reference is null) return false;
        if (reference.FullName.StartsWith(wanted, StringComparison.Ordinal)) return true;
        if (reference is GenericInstanceType generic)
            return generic.GenericArguments.Any(a => Names(a, wanted));
        return false;
    }

    /// <summary>Every type whose OWN method bodies reference <paramref name="member"/>. A SHAPE scan - the
    /// one above - answers who HOLDS a type; this answers who CALLS a member, which is the question a
    /// capability has to survive: a caller can reach a static factory without holding anything.</summary>
    private static List<string> BodiesMentioning(ModuleDefinition module, string member)
    {
        var found = new List<string>();
        foreach (var type in AllTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference called) continue;
                    var name = (called.DeclaringType?.FullName ?? "") + "::" + called.Name;
                    if (!name.StartsWith(member, StringComparison.Ordinal)) continue;
                    found.Add(Outermost(type).FullName);
                    break;
                }
            }
        }
        return found.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// THE STRUCTURAL NEGATIVE, and it is the assertion the inspection asked for by name. Showing that a
    /// direct call to the mint works proves that a person CAN promote; it says nothing about whether
    /// anything else can. This says what else can: nothing, except the one route a person's request
    /// arrives on.
    ///
    /// The grant's own type is excluded because the factory lives on it, and the promote endpoint is the
    /// answer rather than an exception - if a second name ever appears in this list, some other piece of
    /// Gateway code has acquired the ability to move a rule out of dry run, and that is exactly the thing
    /// dry run exists to prevent.
    /// </summary>
    [Fact]
    public void The_only_production_code_that_can_obtain_a_promotion_grant_is_the_promote_endpoint()
    {
        var module = GatewayModule();
        var minters = BodiesMentioning(module, TheMint)
            .Where(t => !t.StartsWith(TheGrant, StringComparison.Ordinal))
            .ToList();

        // THE INSTRUMENT first. If the endpoint is not in this list the scanner is reading nothing, and
        // "no other minter was found" would certify a sweep that never looked.
        Assert.Contains(ThePromoteEndpoint, minters);
        Assert.Equal(new[] { ThePromoteEndpoint }, minters);
    }

    [Fact]
    public void The_only_production_code_that_can_call_promote_is_the_promote_endpoint()
    {
        var module = GatewayModule();
        var callers = BodiesMentioning(module, ThePromoteCall)
            .Where(t => !t.StartsWith(TheStore, StringComparison.Ordinal))
            .ToList();

        Assert.Contains(ThePromoteEndpoint, callers);
        Assert.Equal(new[] { ThePromoteEndpoint }, callers);
    }

    [Fact]
    public void A_promotion_grant_cannot_be_constructed_by_anything_but_itself()
    {
        // The factory is the only door, so the constructor must not be a second one. A public constructor
        // would make every assertion above decorative: a caller that could not reach the factory would
        // simply build the evidence itself.
        var module = GatewayModule();
        var builders = BodiesMentioning(module, TheGrant + "::.ctor")
            .Where(t => !t.StartsWith(TheGrant, StringComparison.Ordinal))
            .ToList();

        Assert.True(builders.Count == 0,
            "these construct a promotion grant directly rather than obtaining one from an authenticated " +
            "request: " + string.Join(", ", builders));
    }

    /// <summary>
    /// THE EVIDENCE MUST BE SOMETHING AN AUTOMATED CALLER CANNOT WRITE DOWN.
    ///
    /// The first version of this bound took a caller identity and an acknowledgement as STRINGS and proved
    /// only that neither was blank. Any Gateway code could therefore invent both and promote a rule, and
    /// the comment saying nothing automated could promote was simply untrue. So the mint is no longer
    /// public, and it takes THE REQUEST ITSELF rather than a description of one: the identity is read from
    /// what the pipeline authenticated, and code with no inbound request has nothing to hand it.
    /// </summary>
    [Fact]
    public void The_mint_takes_the_request_itself_and_is_not_part_of_the_public_surface()
    {
        var mints = typeof(RulePromotionGrant)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Static)
            .Where(m => m.Name == "FromAuthenticatedRequest")
            .ToList();

        var mint = Assert.Single(mints);
        Assert.False(mint.IsPublic,
            "the mint is public, so any caller anywhere can reach it. A capability that is available to " +
            "everything is not a capability.");
        Assert.Contains(mint.GetParameters(),
            p => p.ParameterType == typeof(Microsoft.AspNetCore.Http.HttpContext));
        Assert.DoesNotContain(mint.GetParameters(),
            p => p.ParameterType == typeof(string) && (p.Name ?? "").Contains("Identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_scanner_finds_the_store_where_it_really_is_held()
    {
        // THE INSTRUMENT. The composition root really does hold the concrete store - it is what wires the
        // routes. If this comes back empty the scanner is reading nothing, and the assertion below it would
        // certify a sweep that never looked.
        var module = GatewayModule();
        var holders = ShapesMentioning(module, TheStore,
            t => !NamespaceOf(t).StartsWith(RulesNamespace, StringComparison.Ordinal));

        Assert.True(holders.Count > 0,
            "the scanner found nothing outside the rules namespace holding " + TheStore +
            ", but the composition root does hold it - so the scanner is broken, not the code.");
    }

    [Fact]
    public void Nothing_in_the_rules_namespace_holds_the_store_that_can_promote_except_the_store_itself()
    {
        var module = GatewayModule();
        var holders = ShapesMentioning(module, TheStore,
            t => NamespaceOf(t).StartsWith(RulesNamespace, StringComparison.Ordinal)
                 && !Outermost(t).FullName.StartsWith(TheStore, StringComparison.Ordinal));

        Assert.True(holders.Count == 0,
            "the evaluation path is handed IRuleReading, which cannot promote a rule. These hold the " +
            "concrete " + TheStore + " instead, and could promote by adding one line: " +
            string.Join(", ", holders));
    }

    [Fact]
    public void Nothing_in_the_rules_namespace_can_obtain_a_promotion_grant_except_the_store_and_the_grant()
    {
        var module = GatewayModule();
        var holders = ShapesMentioning(module, TheGrant,
            t => NamespaceOf(t).StartsWith(RulesNamespace, StringComparison.Ordinal)
                 && !Outermost(t).FullName.StartsWith(TheStore, StringComparison.Ordinal)
                 && !Outermost(t).FullName.StartsWith(TheGrant, StringComparison.Ordinal));

        Assert.True(holders.Count == 0,
            "a promotion grant is minted from an authenticated request and handed straight to the store. " +
            "These in the feature carry one, which is a route by which an automated caller could hold the " +
            "evidence that a person asked: " + string.Join(", ", holders));
    }
}
