using Mono.Cecil;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE BUILT GATEWAY ASSEMBLY, READ ONCE FOR THE WHOLE TEST PROCESS.
///
/// Three guards in this feature reason about the compiled metadata - what can type, what can promote, what
/// issues bulk SQL - and each of them held its own copy of this. That is three full reads of the assembly
/// per run, and it is not a micro-optimisation to fix: the type-nothing guard's own comment records that
/// reading the module per TEST pushed this project past the local gate's two-minute ceiling, and a suite
/// that gets stopped is neither a pass nor a failure. A third of that cost was still being paid.
///
/// Held for the life of the process, because a disposed module takes the type handles read from it.
/// </summary>
internal static class TheBuiltGatewayAssembly
{
    private static readonly Lazy<ModuleDefinition> TheModule = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CcDirector.Gateway.dll");
        Assert.True(File.Exists(path), "the Gateway assembly is not beside the tests at " + path);
        return ModuleDefinition.ReadModule(path);
    }, isThreadSafe: true);

    internal static ModuleDefinition Module => TheModule.Value;

    /// <summary>Every type in it, nested ones included - a compiler-generated closure is still a type that
    /// could hold the thing being looked for.</summary>
    internal static IEnumerable<TypeDefinition> AllTypes()
    {
        foreach (var type in Module.Types)
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

    /// <summary>
    /// The type a nested one belongs to, walking all the way out. AN ASYNC METHOD'S BODY DOES NOT LIVE IN
    /// THE METHOD: the compiler moves it into a generated nested state machine which carries an EMPTY
    /// namespace and a name of its own. A guard that did not walk out to the declaring type would find
    /// nothing for every async method in the namespace it is guarding - and would report that as clean.
    /// </summary>
    internal static TypeDefinition Outermost(TypeDefinition type)
    {
        var current = type;
        while (current.DeclaringType is not null) current = current.DeclaringType;
        return current;
    }
}
