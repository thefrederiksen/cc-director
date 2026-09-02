using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CcDirector.Gateway.Tests.Architecture;

/// <summary>
/// Reads WHICH METHODS CALL WHAT off the compiled intermediate language, so an architecture claim is checked
/// against the code that will actually run rather than against the source text. An alias, a
/// <c>using static</c>, or a differently-named local cannot hide a call that is genuinely made.
///
/// It finds calls to a NAMED member, which is its limit and worth stating: a call reached through a new
/// wrapper somewhere else, or a behaviour rebuilt inline, is invisible to it. Every test that uses this must
/// say what its scan therefore does not cover.
/// </summary>
internal static class CompiledCalls
{
    /// <summary>
    /// Every method in <paramref name="assemblyFile"/> whose body calls <paramref name="member"/>, written as
    /// "Declaring.Type::Method". The assembly is looked up beside <paramref name="anchor"/>'s own file, which
    /// is the test output directory.
    /// </summary>
    public static List<string> Of(string member, string assemblyFile, Type anchor)
    {
        var baseDir = Path.GetDirectoryName(anchor.Assembly.Location)!;
        var path = Path.Combine(baseDir, assemblyFile);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"'{assemblyFile}' is not in the test output directory ('{baseDir}'), so this scan would " +
                "silently cover nothing while reporting a pass. Restore the project reference that brings it here.",
                path);

        var found = new List<string>();
        using var module = ModuleDefinition.ReadModule(path);
        foreach (var type in AllTypes(module))
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Ldftn)) continue;
                    if (instruction.Operand is not MethodReference called) continue;
                    if ($"{called.DeclaringType.FullName}::{called.Name}" == member)
                        found.Add($"{type.FullName}::{method.Name}");
                }
            }

        return found.Distinct().ToList();
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var top in module.Types)
            foreach (var nested in Flatten(top))
                yield return nested;
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
            foreach (var inner in Flatten(nested))
                yield return inner;
    }
}
