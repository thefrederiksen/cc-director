using Mono.Cecil;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// PHASE 1 TYPES NOTHING, and this is what proves it. Phase 1 ships the store, the checks and the
/// validator; acting - typing into a session - is a later phase. So no type in the rules namespace may
/// reach the seam that sends a command to a session.
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

    /// <summary>The namespace phase 1 built.</summary>
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

    [Fact]
    public void Nothing_in_the_rules_namespace_can_type_into_a_session()
    {
        using var module = GatewayModule();
        var offenders = TypesReachingTheSeam(
            module,
            t => (t.Namespace ?? "").StartsWith(RulesNamespace, StringComparison.Ordinal));

        Assert.True(offenders.Count == 0,
            "phase 1 types nothing, but these reach " + TypingSeam + ": " + string.Join(", ", offenders));
    }
}
