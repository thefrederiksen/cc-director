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
/// by a comment. A review found it while checking something else, which is luck, not process.
///
/// The invariant, in two halves:
///   1. A language model may LOCATE misheard terms. It must never receive the transcript and return
///      free text that becomes the user's words.
///   2. Only TranscriptEditEngine changes transcript text.
///
/// Both are checked structurally rather than by reading the code, so a future call site that breaks
/// them fails the build rather than an inspection.
/// </summary>
public sealed class TranscriptionIntegrityGuardTests
{
    /// <summary>
    /// Half one, made structural. The judge's only method hands back numbers - the ids of candidates
    /// OUR code isolated. A model on the other end of this interface physically cannot return a word,
    /// a sentence, or a corrected transcript, because there is nowhere in the signature to put one.
    ///
    /// This is why the interface returns ids instead of edits, and the test exists so that stays true:
    /// changing it to return strings would be a one-line change that quietly reopens the entire class
    /// of corruption the July removal was about.
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
    /// Half two: nothing in the dictation subsystem rewrites transcript text except the edit engine.
    ///
    /// Scanned for the operations that actually produce a modified transcript - a regex replace or a
    /// string replace. The engine is the single allowed home for both. A new "helpful" cleanup step
    /// added anywhere else in Dictation trips this.
    /// </summary>
    [Fact]
    public void OnlyTheEditEngineRewritesTranscriptText()
    {
        var root = GetRepoRoot();
        var dictationDir = Path.Combine(root, "src", "CcDirector.Core", "Dictation");
        Assert.True(Directory.Exists(dictationDir), $"expected the dictation source at {dictationDir}");

        var scanned = 0;
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(dictationDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
            scanned++;

            if (Path.GetFileName(file) == "TranscriptEditEngine.cs") continue;

            var text = File.ReadAllText(file);
            if (text.Contains("Regex.Replace(", StringComparison.Ordinal))
                offenders.Add($"{rel} calls Regex.Replace");
            if (text.Contains(".Replace(", StringComparison.Ordinal))
                offenders.Add($"{rel} calls string Replace");
        }

        // The instrument must not pass by scanning nothing: an empty sweep is a broken run, not a
        // clean one. TranscriptEditEngine itself is excluded above, so a real scan sees several files.
        Assert.True(scanned >= 5,
            $"only {scanned} dictation source files were scanned - the guard is looking in the wrong " +
            "place and would report clean whatever the code did");

        Assert.True(offenders.Count == 0,
            "Only TranscriptEditEngine may rewrite transcript text. Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The guard above is only worth having if it can fail. This proves the detector fires on the
    /// exact shape it is looking for, so a green run means "nothing rewrites text" rather than "the
    /// scan never matched anything".
    /// </summary>
    [Fact]
    public void TheGuardDetectsARewriteWhenThereIsOne()
    {
        const string offending = "var cleaned = raw.Replace(\"a\", \"b\");";
        const string innocent = "var score = Jaro(spanNorm, target);";

        Assert.Contains(".Replace(", offending, StringComparison.Ordinal);
        Assert.DoesNotContain(".Replace(", innocent, StringComparison.Ordinal);
    }

    private static bool ReturnsTextSomehow(Type t)
    {
        if (t == typeof(string)) return true;
        if (t.IsGenericType)
            return t.GetGenericArguments().Any(ReturnsTextSomehow);
        return false;
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
