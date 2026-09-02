using CcDirector.Core.History;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace CcDirector.Gateway.Tests.Architecture;

/// <summary>
/// THE ONE RESOLVER, enforced on the compiled code rather than asserted in a comment.
///
/// The defect this exists for: on 1 September 2026 session 111 produced no spoken narration for hours, and
/// the cause was not the voice model, the text, or the disk. The Director resolved the agent's transcript by
/// BUILDING A PATH from the session's repository folder. The agent had moved into a worktree, the transcript
/// moved with it, and every read from then on opened an empty spot and answered "no transcript" - forever,
/// because the formula would never produce a different answer. Chat was unaffected in the same session,
/// because Chat followed the pointer the agent's own hook reports instead of computing one.
///
/// Two claims are checked here, and both are read off the intermediate language with Mono.Cecil, so they
/// cannot be walked around by an alias, a <c>using static</c>, a wrapper, or a differently-named local:
///
///   1. NOTHING in the Director's command surface derives a transcript path for itself. Every call site goes
///      through <see cref="SessionHistoryReader.ResolveTranscriptPath"/>, which prefers the hook-reported
///      pointer and only falls back to a lookup by the transcript's own identifier.
///   2. The GATEWAY never resolves a transcript path at all. It has no business touching one: it reads
///      conversations out of its own store, which the Director pushes to it. A hosted Gateway could not open
///      the file even if it tried - the disk is a different machine - so a call appearing here would be a
///      read that always fails, which is exactly the shape that silenced session 111.
///
/// The detector is proven against a known-positive rather than trusted: <see cref="TheDetectorFindsTheCall"/>
/// points it at the assembly that legitimately DOES compute the path and requires it to find one. Without
/// that, a typo in the member name would make every other test in this file pass by finding nothing.
/// </summary>
public sealed class OneTranscriptResolverArchitectureTests
{
    /// <summary>The path-by-formula call. It is legitimate in exactly one assembly (see the class summary).</summary>
    private const string DerivesATranscriptPath = "CcDirector.Core.Claude.ClaudeSessionReader::GetJsonlPath";

    [Fact]
    public void TheDirectorsCommandSurface_neverDerivesATranscriptPathForItself()
    {
        var callers = CallersOf(DerivesATranscriptPath, "CcDirector.ControlApi.dll");

        Assert.True(callers.Count == 0,
            "The Director's command surface must resolve a transcript through SessionHistoryReader." +
            "ResolveTranscriptPath, which follows the pointer the agent's own hook reports. Building the path " +
            "from the session's repository folder is what left session 111 silent for hours: the agent moved " +
            "into a worktree, the transcript moved with it, and the formula went on naming the empty spot for " +
            "the rest of the session's life. These methods compute a path instead:" + Environment.NewLine +
            "  " + string.Join(Environment.NewLine + "  ", callers));
    }

    [Fact]
    public void TheGateway_neverResolvesATranscriptPathAtAll()
    {
        var callers = CallersOf(DerivesATranscriptPath, "CcDirector.Gateway.dll");

        Assert.True(callers.Count == 0,
            "The Gateway reads conversations from its own store, which the Director pushes to it. It must " +
            "never resolve a transcript path - hosted, the file is on somebody else's machine, so such a read " +
            "can only ever fail, and a read that always fails is what the turn-push mission removed. These " +
            "methods resolve one:" + Environment.NewLine +
            "  " + string.Join(Environment.NewLine + "  ", callers));
    }

    [Fact]
    public void TheDetectorFindsTheCall()
    {
        // A checker whose pass condition is an ABSENCE certifies nothing until it has been shown finding a
        // presence. CcDirector.Core is where deriving the path is CORRECT - it is the fallback inside
        // ResolveTranscriptPath itself - so the detector must report calls there. If this ever goes red, the
        // two absence claims above are meaningless rather than reassuring: the member has been renamed and
        // the scan is looking for something that no longer exists.
        var callers = CallersOf(DerivesATranscriptPath, "CcDirector.Core.dll");

        Assert.True(callers.Count > 0,
            $"The scan found no call to {DerivesATranscriptPath} in CcDirector.Core.dll, where the resolver's " +
            "own fallback makes one. The member has been renamed or moved, so the two absence checks in this " +
            "file are passing by finding nothing rather than by nothing being there. Update the constant.");
    }

    /// <summary>
    /// Every method in <paramref name="assemblyFile"/> whose intermediate language calls
    /// <paramref name="member"/>, as "Declaring.Type::Method". Read off the compiled metadata, so a wrapper,
    /// an alias or a rename in the source cannot hide a call.
    /// </summary>
    private static List<string> CallersOf(string member, string assemblyFile)
    {
        var baseDir = Path.GetDirectoryName(typeof(SessionHistoryReader).Assembly.Location)!;
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
