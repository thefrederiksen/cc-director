using System.Reflection;
using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// The architecture guard that <c>TranscriptEditEngine</c> has claimed since July.
///
/// Its class comment said "The TranscriptionIntegrity architecture test enforces this at build time".
/// No such test existed - the name appeared nowhere in the repository except in that sentence. The
/// invariant it names is the one that matters most in this subsystem, and for a month it was enforced
/// by a comment.
///
/// The invariant has two halves. THIS FILE ENFORCES ONE OF THEM.
///
///   1. ENFORCED HERE - a language model may LOCATE misheard terms; it can never receive the
///      transcript and hand back free text that becomes the user's words. That is checked structurally
///      below, on both the return type and the parameter.
///   2. NOT ENFORCED ANYWHERE - only TranscriptEditEngine changes transcript text. This is still a
///      real rule and still followed; nothing automated checks it.
///
/// Half two was attempted twice and abandoned, and the reason is worth keeping. A substring scan over
/// source text was evadable by a single space and failed the build on the word appearing inside a
/// comment. Parsing with Roslyn fixed the trivia but not the actual problem: without following values
/// through the code, a whitelist of variable names missed <c>text.Replace(...)</c>, <c>String.Concat</c>,
/// <c>this.rawTranscript</c>, null-conditional calls and interpolated rebuilds - all ordinary C# a
/// well-meant second cleanup path would use - while flagging a Substring taken for a log preview.
///
/// A check that certifies an unconditional claim while missing the ordinary spellings is worse than no
/// check, because the green run is read as proof. That is exactly the failure this file was written to
/// correct: for a month the engine's comment claimed a TranscriptionIntegrity test that did not exist,
/// and replacing a missing test with a passing-but-blind one is the same mistake with extra steps.
///
/// So half two is written down as an unenforced rule and tracked, rather than dressed up. Doing it
/// properly needs semantic analysis that follows transcript values, which is a piece of work in its
/// own right - see devthrottle_internal#1556.
/// </summary>
public sealed class TranscriptionIntegrityGuardTests
{
    /// <summary>
    /// Half one, made structural. The judge's only method hands back numbers - the ids of candidates
    /// OUR code isolated. A model on the other end of this interface physically cannot return a word,
    /// a sentence, or a corrected transcript, because there is nowhere in the signature to put one.
    /// </summary>
    [Fact]
    public void AJudgeCanOnlyReturnCandidateIds_NeverText()
    {
        var methods = typeof(ICandidateJudge).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var accept = Assert.Single(methods);

        Assert.Equal(typeof(Task<IReadOnlyList<int>?>), accept.ReturnType);

        foreach (var m in methods)
        {
            Assert.False(ReturnsTextSomehow(m.ReturnType),
                $"ICandidateJudge.{m.Name} can return text. A judge must only ever select from " +
                "candidates our own code produced - see the integrity invariant on TranscriptEditEngine.");
        }
    }

    /// <summary>
    /// The judge must not be able to reach the applied candidates through its PARAMETER either. It is
    /// handed an <see cref="IReadOnlyList{T}"/>, and a review proved that a bare array or List handed
    /// out under that type can be cast straight back and written through after validation. So the
    /// contract is pinned as read-only AND the runtime type the orchestrator passes is checked by
    /// <c>DictationJudgeTests.AJudgeThatRewritesItsOwnCandidateList_ChangesNothing</c>.
    /// </summary>
    [Fact]
    public void AJudgeIsHandedCandidatesItCannotWriteThrough()
    {
        var accept = typeof(ICandidateJudge).GetMethods(BindingFlags.Public | BindingFlags.Instance).Single();
        var candidates = accept.GetParameters().Single(p => p.Name == "candidates");

        Assert.Equal(typeof(IReadOnlyList<JudgeCandidate>), candidates.ParameterType);
    }

    private static bool ReturnsTextSomehow(Type t)
    {
        if (t == typeof(string)) return true;
        if (t.IsGenericType)
            return t.GetGenericArguments().Any(ReturnsTextSomehow);
        return false;
    }
}
