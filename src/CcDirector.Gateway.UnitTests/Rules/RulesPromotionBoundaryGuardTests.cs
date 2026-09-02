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

    /// <summary>The assembly, read ONCE for the whole test process and held. Reading it per test is what
    /// pushed this project past the local gate's two-minute ceiling, and a suite that gets stopped is
    /// neither a pass nor a failure.</summary>
    private static readonly Lazy<ModuleDefinition> Module = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CcDirector.Gateway.dll");
        Assert.True(File.Exists(path), "the Gateway assembly is not beside the tests at " + path);
        return ModuleDefinition.ReadModule(path);
    }, isThreadSafe: true);

    private static ModuleDefinition GatewayModule() => Module.Value;

    private static TypeDefinition Outermost(TypeDefinition type)
    {
        var current = type;
        while (current.DeclaringType is not null) current = current.DeclaringType;
        return current;
    }

    private static string NamespaceOf(TypeDefinition type) => Outermost(type).Namespace ?? "";

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            yield return type;
            foreach (var nested in Nested(type)) yield return nested;
        }

        static IEnumerable<TypeDefinition> Nested(TypeDefinition type)
        {
            foreach (var nested in type.NestedTypes)
            {
                yield return nested;
                foreach (var deeper in Nested(nested)) yield return deeper;
            }
        }
    }

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
