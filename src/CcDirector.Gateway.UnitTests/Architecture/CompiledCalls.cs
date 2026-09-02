using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CcDirector.Gateway.Tests.Architecture;

/// <summary>
/// Reads WHICH METHODS CALL WHAT off the compiled intermediate language, so an architecture claim is checked
/// against the code that will actually run rather than against the source text. An alias, a
/// <c>using static</c>, or a differently-named local cannot hide a call that is genuinely made.
///
/// It finds calls to a NAMED member, which is its limit and worth stating: a call reached through a new
/// wrapper somewhere else, a behaviour rebuilt inline, or an invocation made by reflection is invisible to
/// it. Every test that uses this must say what its scan therefore does not cover.
///
/// Results are reported in the names a person wrote, not the names the compiler generated - see
/// <see cref="AsWritten"/> for why that is not cosmetic.
/// </summary>
internal static class CompiledCalls
{
    /// <summary>
    /// Every method in <paramref name="assemblyFile"/> whose body calls <paramref name="member"/>, written as
    /// "Declaring.Type::Method" using the source-level names. The assembly is looked up beside
    /// <paramref name="anchor"/>'s own file, which is the test output directory.
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
                    // Every instruction that names a method: the two calls, a constructor, and both ways of
                    // taking a function pointer. Ldvirtftn was missing until a review pointed out that a
                    // delegate made from a virtual method would slip past (found in review).
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj
                        or Code.Ldftn or Code.Ldvirtftn)) continue;
                    if (instruction.Operand is not MethodReference called) continue;
                    if ($"{called.DeclaringType.FullName}::{called.Name}" == member)
                        found.Add(AsWritten(type, method));
                }
            }

        return found.Distinct().ToList();
    }

    /// <summary>
    /// The type and method A PERSON WROTE, recovered from whatever the compiler emitted.
    ///
    /// This is load-bearing, not tidiness. The body of an <c>async</c> method lives in a generated state
    /// machine nested inside its type ("Owner/&lt;DoWork&gt;d__12::MoveNext"), and the body of a lambda lives
    /// in a generated closure class ("Owner/&lt;&gt;c__DisplayClass9_0::&lt;DoWork&gt;b__0"). Report those raw
    /// and a guard comparing against names from the source misses the call entirely - a guard that passes
    /// because it looked in the wrong place, which is the worst kind. Both shapes were found doing exactly
    /// that: a transcript read inside an async method, and the voice sweep inside a lambda.
    ///
    /// The generated name always carries the original in angle brackets, on the nested TYPE for a state
    /// machine and on the METHOD for a lambda, so both are recoverable. Anything unrecognised is returned
    /// unchanged rather than guessed at.
    /// </summary>
    private static string AsWritten(TypeDefinition type, MethodDefinition method)
    {
        var top = type;
        while (top.DeclaringType is not null) top = top.DeclaringType;

        var written = Original(type.Name) ?? Original(method.Name) ?? method.Name;
        return $"{top.FullName}::{written}";
    }

    /// <summary>The name between the angle brackets of a compiler-generated name, or null if there is none.</summary>
    private static string? Original(string generated)
    {
        var m = Regex.Match(generated, @"^<([^>]+)>");
        return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
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
