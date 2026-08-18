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
            var hit = DetectRewrite(text);
            if (hit is not null)
                offenders.Add($"{rel}: {hit}");
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
    /// The guard above is only worth having if it can FAIL, and the previous version of this test could
    /// not tell you that: it asserted a literal string contained a literal substring, which proves
    /// nothing about the detector the scan actually uses. Now the same <see cref="DetectRewrite"/> the
    /// scan calls is run over source fixtures, so a green sweep means "no rewrite mechanism present"
    /// rather than "the pattern happened not to match".
    /// </summary>
    [Theory]
    [InlineData("var cleaned = raw.Replace(\"a\", \"b\");")]
    [InlineData("return Regex.Replace(text, pattern, replacement);")]
    [InlineData("var cleaned = raw[..start] + term + raw[(start + len)..];")]
    [InlineData("text = text[..c.Start] + c.Replace + text[(c.Start + c.Find.Length)..];")]
    [InlineData("var sb = new StringBuilder(raw); sb.Remove(0, 2); return sb.ToString();")]
    public void TheDetectorFiresOnEveryRewriteMechanismWeKnowOf(string source)
        => Assert.NotNull(DetectRewrite(source));

    /// <summary>And stays quiet on code that only READS or scores text, or the guard becomes a nuisance
    /// that the next person deletes instead of honouring.</summary>
    [Theory]
    [InlineData("var score = Jaro(spanNorm, target.Norm);")]
    [InlineData("var tokens = Regex.Matches(raw, TokenPattern);")]
    [InlineData("if (raw.Contains(edit.Find, StringComparison.Ordinal)) { }")]
    [InlineData("var norm = char.ToLowerInvariant(c);")]
    [InlineData("// we never Replace anything here, we only measure it")]
    public void TheDetectorStaysQuietOnCodeThatOnlyReadsText(string source)
        => Assert.Null(DetectRewrite(source));

    /// <summary>
    /// The rewrite mechanisms we know how to spot. Heuristic by nature - it reads source text, not
    /// semantics - so it is deliberately aimed at the shapes that actually produce a modified
    /// transcript: a replace call, a slice-and-concatenate rebuild (what ApplyAt itself does), and
    /// StringBuilder mutation. A new mechanism that evades all three is a gap to close here, and the
    /// fixtures above are where it gets pinned.
    /// </summary>
    private static string? DetectRewrite(string source)
    {
        var code = string.Join(
            Environment.NewLine,
            source.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        if (code.Contains("Regex.Replace(", StringComparison.Ordinal)) return "calls Regex.Replace";
        if (code.Contains(".Replace(", StringComparison.Ordinal)) return "calls string Replace";
        if (code.Contains("[..", StringComparison.Ordinal) && code.Contains("] +", StringComparison.Ordinal))
            return "rebuilds a string by slicing and concatenating";
        if (code.Contains("new StringBuilder(", StringComparison.Ordinal)
            && (code.Contains(".Remove(", StringComparison.Ordinal)
                || code.Contains(".Insert(", StringComparison.Ordinal)))
            return "mutates text through a StringBuilder";
        return null;
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
