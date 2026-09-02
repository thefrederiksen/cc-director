using Mono.Cecil;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// EXACTLY ONE THING IN THIS FEATURE CAN TYPE, AND THIS IS WHAT PROVES IT. Phase 1 typed nothing at all;
/// phase 2 types, which is the whole point of it - so the guard is not deleted, it is TIGHTENED. The set of
/// types in the rules namespace that reach the seam that sends a command to a session must be exactly one,
/// and it must be the production environment wiring. The evaluator - which is where the dry-run decision is
/// made - must not be able to type at all, which is what makes "dry run types nothing" a property of the
/// structure rather than a branch somebody has to keep remembering.
///
/// IT IS A REFERENCE ASSERTION IN THE BUILT ASSEMBLY, DELIBERATELY NOT A SOURCE-TEXT SCAN, and the
/// distinction is the point. A grep for "prompt" over the rules directory would pass just as happily if
/// the rules directory were EMPTY, or if the call sat one helper away in another file. Reading the
/// compiled metadata cannot be dodged by moving text, and - the part that matters most - the scanner is
/// PROVEN TO WORK on a known-positive first: it is pointed at the endpoint code that really does type,
/// and required to find the seam there. If that instrument check ever comes back clean, the scanner is
/// broken and every "no reference found" below it means nothing.
/// </summary>
public sealed class RulesTypeNothingGuardTests
{
    /// <summary>The seam that sends a command to a session - the only way anything is typed.</summary>
    private const string TypingSeam = "CcDirector.Gateway.Api.DirectorCommandRouter";

    /// <summary>The namespace this feature is built in.</summary>
    private const string RulesNamespace = "CcDirector.Gateway.Rules";

    /// <summary>A type that really does type into sessions, so the scanner can be proven against it.</summary>
    private const string KnownTypist = "CcDirector.Gateway.Api.GatewayEndpoints";

    private static ModuleDefinition GatewayModule()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CcDirector.Gateway.dll");
        Assert.True(File.Exists(path), "the Gateway assembly is not beside the tests at " + path);
        return ModuleDefinition.ReadModule(path);
    }

    /// <summary>Every type whose methods reach the typing seam, among the types this filter selects.</summary>
    private static List<string> TypesReachingTheSeam(ModuleDefinition module, Func<TypeDefinition, bool> include)
    {
        var found = new List<string>();
        foreach (var type in AllTypes(module))
        {
            if (!include(type)) continue;
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    var operandType = instruction.Operand switch
                    {
                        MethodReference m => m.DeclaringType?.FullName,
                        FieldReference f => f.DeclaringType?.FullName,
                        TypeReference t => t.FullName,
                        _ => null,
                    };
                    if (operandType is not null && operandType.StartsWith(TypingSeam, StringComparison.Ordinal))
                    {
                        found.Add(type.FullName + "." + method.Name);
                        break;
                    }
                }
            }
        }
        return found;
    }

    /// <summary>Every type in the module, nested ones included - a compiler-generated closure is still a
    /// type that could hold the call.</summary>
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

    [Fact]
    public void The_scanner_finds_the_typing_seam_where_it_really_is()
    {
        // THE INSTRUMENT. If this comes back empty the scanner is broken, and the guard below it would
        // certify a run that never looked at anything.
        using var module = GatewayModule();
        var typists = TypesReachingTheSeam(module, t => t.FullName.StartsWith(KnownTypist, StringComparison.Ordinal));

        Assert.True(typists.Count > 0,
            "the scanner found no reference to " + TypingSeam + " inside " + KnownTypist +
            ", which really does send commands to sessions - so the scanner is broken, not the code.");
    }

    [Fact]
    public void The_rules_namespace_exists_and_has_types_in_it()
    {
        // The second instrument: a guard over an empty namespace passes and proves nothing.
        using var module = GatewayModule();
        var ruleTypes = AllTypes(module)
            .Where(t => (t.Namespace ?? "").StartsWith(RulesNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(ruleTypes.Count > 0, "no types were found in " + RulesNamespace);
    }

    /// <summary>The ONE type in this feature that is allowed to type. Named, so adding a second one is a
    /// failing test rather than a thing nobody notices.</summary>
    private const string TheOnlyTypist = "CcDirector.Gateway.Rules.GatewayRuleEnvironment";

    /// <summary>Where the dry-run decision is made. It must be structurally incapable of typing.</summary>
    private const string TheEvaluator = "CcDirector.Gateway.Rules.RuleEvaluator";

    [Fact]
    public void Exactly_one_type_in_the_rules_namespace_can_type_and_it_is_the_named_one()
    {
        using var module = GatewayModule();
        var typists = TypesReachingTheSeam(
                module, t => (t.Namespace ?? "").StartsWith(RulesNamespace, StringComparison.Ordinal))
            .Select(m => m[..m.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // A PRESENCE first: the one thing that is supposed to be able to type must actually be there. An
        // empty list here would otherwise pass the "nothing else can type" half below while proving that
        // the feature cannot type at all.
        Assert.Contains(TheOnlyTypist, typists);
        Assert.Equal(new[] { TheOnlyTypist }, typists);
    }

    [Fact]
    public void The_evaluator_cannot_type_so_dry_run_is_a_property_of_the_structure()
    {
        using var module = GatewayModule();
        var offenders = TypesReachingTheSeam(
            module, t => t.FullName.StartsWith(TheEvaluator, StringComparison.Ordinal));

        Assert.True(offenders.Count == 0,
            "the evaluator decides whether a rule is in dry run, so it must reach the send only through its " +
            "environment seam - but these reach " + TypingSeam + " directly: " + string.Join(", ", offenders));
    }
}
