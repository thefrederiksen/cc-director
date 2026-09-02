using CcDirector.Core.History;
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
/// The claims are read off the intermediate language with Mono.Cecil, not off the source text, so an alias,
/// a <c>using static</c> or a differently-named local cannot hide a call that is actually made:
///
///   1. NOTHING in the Director's command surface derives a transcript path for itself, AND that surface
///      still calls <see cref="SessionHistoryReader.ResolveTranscriptPath"/> - which prefers the
///      hook-reported pointer and only falls back to a lookup by the transcript's own identifier. Both
///      halves are needed: the absence alone would go on passing if every call site were simply deleted.
///   2. The GATEWAY resolves a transcript path by NEITHER route. It has no business touching one: it reads
///      conversations out of its own store, which the Director pushes to it. A hosted Gateway could not open
///      the file even if it tried - the disk is a different machine - so a call appearing here would be a
///      read that always fails, which is exactly the shape that silenced session 111.
///
/// The detector is proven against a known-positive rather than trusted: <see cref="TheDetectorFindsTheCall"/>
/// points it at the assembly that legitimately DOES compute the path and requires it to find one. Without
/// that, a typo in the member name would make every other test in this file pass by finding nothing.
///
/// WHAT THIS DOES NOT CATCH, said plainly because a reviewer read the first draft as claiming more than it
/// delivers. This scans for calls to two NAMED members. Someone who wrapped the path formula behind a new
/// helper in CcDirector.Core, or who rebuilt the path inline with <c>Path.Combine</c>, would defeat it - the
/// scan would find no call to either name and report a pass. It is a guard against the defect coming back
/// the way it was written, not a proof that no transcript path can ever be computed anywhere. The narrower
/// claim is still worth having: the eight call sites this mission changed are exactly the shape it detects.
/// </summary>
public sealed class OneTranscriptResolverArchitectureTests
{
    /// <summary>The path-by-formula call. It is legitimate in exactly one assembly (see the class summary).</summary>
    private const string DerivesATranscriptPath = "CcDirector.Core.Claude.ClaudeSessionReader::GetJsonlPath";

    /// <summary>THE one resolver. Pointer first, formula only as its own fallback.</summary>
    private const string TheOneResolver = "CcDirector.Core.History.SessionHistoryReader::ResolveTranscriptPath";

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

    /// <summary>
    /// The four types that read a transcript on the Director's command surface. Named individually, not
    /// counted: a bare "something calls the resolver" would stay green while seven of the eight call sites
    /// were rewritten to build the path some third way, which is precisely the regression being guarded
    /// against (found in review).
    /// </summary>
    private static readonly string[] TypesThatMustResolveThroughIt =
    {
        "CcDirector.ControlApi.Chat.ChatService",
        "CcDirector.ControlApi.ControlEndpoints",
        "CcDirector.ControlApi.SessionReadExecutor",
        "CcDirector.ControlApi.SessionWriteExecutor",
    };

    [Fact]
    public void EveryTypeOnTheCommandSurfaceThatReadsATranscript_goesThroughTheOneResolver()
    {
        // The other half of claim 1. The absence check above would pass just as happily if every one of
        // these call sites were deleted, or quietly rewritten - so this pins WHICH types must still be
        // reaching the transcript through the resolver, by name.
        var resolvingTypes = CallersOf(TheOneResolver, "CcDirector.ControlApi.dll")
            .Select(m => m[..m.IndexOf("::", StringComparison.Ordinal)])
            .ToHashSet(StringComparer.Ordinal);

        var missing = TypesThatMustResolveThroughIt.Where(t => !resolvingTypes.Contains(t)).ToList();

        Assert.True(missing.Count == 0,
            "These types read a transcript on the Director's command surface and no longer call " +
            "SessionHistoryReader.ResolveTranscriptPath. Either the read has gone (delete the name from " +
            "TypesThatMustResolveThroughIt and say why in the commit), or it is resolving the path some " +
            "other way - which is the defect this mission removed:" + Environment.NewLine +
            "  " + string.Join(Environment.NewLine + "  ", missing));
    }

    [Fact]
    public void TheGateway_neverResolvesATranscriptPath_byEitherRoute()
    {
        // BOTH names, deliberately. Checking only the formula would let the Gateway call the resolver itself
        // and still pass - and the Gateway has no business resolving a transcript by ANY route, because the
        // file is on the user's machine and this process is somewhere else entirely.
        var callers = CallersOf(DerivesATranscriptPath, "CcDirector.Gateway.dll")
            .Concat(CallersOf(TheOneResolver, "CcDirector.Gateway.dll"))
            .ToList();

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

    private static List<string> CallersOf(string member, string assemblyFile)
        => CompiledCalls.Of(member, assemblyFile, typeof(SessionHistoryReader));
}
