using System.Reflection;
using CcDirector.Core.Dictation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
/// The invariant, in two halves:
///   1. A language model may LOCATE misheard terms. It must never receive the transcript and return
///      free text that becomes the user's words.
///   2. Only TranscriptEditEngine changes transcript text.
///
/// The first version of this guard matched substrings in source text and was evadable by a space:
/// <c>raw.Replace ("a","b")</c> passed, and so did <c>string.Concat</c> and <c>Substring</c> rebuilds -
/// while an unrelated <c>.Replace(</c> anywhere in the folder failed the build. Known-bad code passing
/// and unrelated code failing is the worst combination a fitness function can have, because it teaches
/// the next person to delete it.
///
/// So it reads SYNTAX now, not characters. Roslyn parses each file and the check asks what operation
/// is being invoked, which makes trivia - spaces, line breaks, comments, string literals containing
/// the word - irrelevant by construction.
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

    /// <summary>
    /// Half two: nothing in the dictation subsystem rewrites transcript text except the edit engine.
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

            foreach (var hit in FindRewrites(File.ReadAllText(file)))
                offenders.Add($"{rel}: {hit}");
        }

        // The instrument must not pass by scanning nothing: an empty sweep is a broken run, not a
        // clean one.
        Assert.True(scanned >= 5,
            $"only {scanned} dictation source files were scanned - the guard is looking in the wrong " +
            "place and would report clean whatever the code did");

        Assert.True(offenders.Count == 0,
            "Only TranscriptEditEngine may rewrite transcript text. Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The known-bad controls. Every one of these rebuilds a string from another one, which is what
    /// rewriting a transcript actually looks like - and the substring version of this guard let four
    /// of the five through. A guard that cannot fail on these is not evidence of anything.
    /// </summary>
    [Theory]
    [InlineData("class C { string M(string raw) => raw.Replace(\"a\", \"b\"); }")]
    [InlineData("class C { string M(string raw) => raw.Replace (\"a\", \"b\"); }")]
    [InlineData("class C { string M(string raw) => raw\n    .Replace(\"a\", \"b\"); }")]
    [InlineData("class C { string M(string raw) => Regex.Replace(raw, \"a\", \"b\"); }")]
    [InlineData("class C { string M(string raw, int s, int e) => raw.Substring(0, s) + \"x\" + raw.Substring(e); }")]
    [InlineData("class C { string M(string raw, int s, int e) => string.Concat(raw.AsSpan(0, s), \"x\", raw.AsSpan(e)); }")]
    [InlineData("class C { string M(string raw, int s) => raw[..s] + \"x\" + raw[s..]; }")]
    [InlineData("class C { string M(string raw) { var b = new StringBuilder(raw); b.Remove(0, 2); return b.ToString(); } }")]
    [InlineData("class C { string M(string raw) { var b = new StringBuilder(raw); b[0] = 'x'; return b.ToString(); } }")]
    [InlineData("class C { string M(string raw) { var b = new StringBuilder(raw); b.Insert(0, \"x\"); return b.ToString(); } }")]
    public void TheGuardFailsOnEveryKnownRewrite(string source)
        => Assert.NotEmpty(FindRewrites(source));

    /// <summary>
    /// And stays quiet on code that only reads, measures or matches text - including the trivia that
    /// broke the previous version: the forbidden words inside comments and string literals.
    /// </summary>
    [Theory]
    [InlineData("class C { double M(string a, string b) => Jaro(a, b); }")]
    [InlineData("class C { void M(string raw) { var t = Regex.Matches(raw, P); } }")]
    [InlineData("class C { bool M(string raw, string f) => raw.Contains(f, StringComparison.Ordinal); }")]
    [InlineData("class C { int M(string raw) => raw.IndexOf('a'); }")]
    [InlineData("class C { // we never Replace anything here, we only measure it\n void M() { } }")]
    [InlineData("class C { string M() => \"call .Replace( to rewrite\"; }")]
    [InlineData("class C { /* Regex.Replace( in a block comment */ void M() { } }")]
    [InlineData("class C { string M(string s) { var sb = new System.Text.StringBuilder(s.Length); sb.Append('a'); return sb.ToString(); } }")]
    public void TheGuardStaysQuietOnCodeThatOnlyReadsText(string source)
        => Assert.Empty(FindRewrites(source));

    /// <summary>
    /// Every invocation or expression in <paramref name="source"/> that BUILDS a string out of another
    /// one. Syntax-level, so spacing, line breaks, comments and string literals cannot hide or fake a
    /// hit.
    ///
    /// It proves the operation, not the data flow: it cannot tell a transcript from any other string.
    /// That is deliberate. In this folder every string of consequence is the user's words, so "no
    /// rebuild happens here at all" is a stronger and far more checkable rule than "no rebuild of
    /// specifically the transcript" - and it is the rule the engine already satisfies.
    /// </summary>
    /// <summary>Identifiers that hold the user's words. A rewrite that matters operates on one of
    /// these; <c>sb.Append(...)</c> building a prompt or <c>patterns[key] = ...</c> writing a dictionary
    /// does not, and an earlier version of this guard failed the build on both.</summary>
    /// <remarks>"text" is deliberately NOT here. It matched the <c>Text</c> in
    /// <c>System.Text.StringBuilder</c> and failed the build on a normalisation helper that only
    /// lowercases characters for scoring. A name that collides with a namespace is not a name that can
    /// identify the user's words. The cost is that a rewrite through a variable called exactly
    /// <c>text</c> would evade this - which is why the engine, where that name is used, is the one file
    /// exempt from the scan anyway.</remarks>
    private static readonly string[] TranscriptNames =
        { "raw", "rawtranscript", "transcript", "cleaned", "utterance" };

    private static bool IsTranscript(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return false;
        var head = expr.Split('.', '[', '(')[0].Trim();
        return TranscriptNames.Contains(head, StringComparer.OrdinalIgnoreCase);
    }

    private static bool MentionsTranscript(SyntaxNode node)
        => node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(i => TranscriptNames.Contains(i.Identifier.ValueText, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Every place <paramref name="source"/> builds a new string out of the user's words. Syntax-level,
    /// so spacing, line breaks, comments and string literals cannot hide or fake a hit.
    ///
    /// It follows the transcript by NAME rather than by full data flow. That is a real limit and worth
    /// stating: a rewrite of a transcript first copied into a deliberately unrelated variable name would
    /// slip past. The alternative - a full semantic model - buys precision this guard does not need,
    /// because the thing it is defending against is a well-meant second cleanup path, not a hostile one.
    /// Following the name is what stops it failing the build on prompt building and dictionary writes,
    /// which is what got the previous version into trouble in the other direction.
    /// </summary>
    /// <summary>Is <paramref name="name"/> a local initialised from the user's words - a StringBuilder
    /// or span wrapped round the transcript? Mutating one of those rewrites the transcript just as
    /// surely as calling Replace on it directly.</summary>
    private static bool IsBufferFromTranscript(SyntaxNode root, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Any(v => v.Identifier.ValueText == name
                      && v.Initializer is not null && MentionsTranscript(v.Initializer));
    }

    private static IReadOnlyList<string> FindRewrites(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var hits = new List<string>();

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax member) continue;
            var name = member.Name.Identifier.ValueText;
            var receiver = member.Expression.ToString();

            var rewritesReceiver = name is "Replace" or "Substring" or "Insert" or "Remove"
                                   && (IsTranscript(receiver) || IsBufferFromTranscript(root, receiver));
            var mutatesBuffer = name is "Append" or "AppendLine"
                                && IsBufferFromTranscript(root, receiver);
            var concatsTranscript = name is "Concat" && receiver is "string"
                                    && call.ArgumentList.Arguments.Any(a => MentionsTranscript(a));
            var regexRewrite = name is "Replace" && receiver.EndsWith("Regex", StringComparison.Ordinal)
                               && call.ArgumentList.Arguments.Count > 0
                               && MentionsTranscript(call.ArgumentList.Arguments[0]);

            if (rewritesReceiver) hits.Add($"calls {receiver}.{name}(...) on the transcript");
            else if (mutatesBuffer) hits.Add($"mutates {receiver}, a text buffer built from the transcript");
            else if (concatsTranscript) hits.Add("rebuilds the transcript with string.Concat");
            else if (regexRewrite) hits.Add("rewrites the transcript with Regex.Replace");
        }

        // A range slice of the transcript, concatenated with something else - what ApplyAt itself does.
        if (root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(b => b.IsKind(SyntaxKind.AddExpression))
            .Any(b => b.DescendantNodesAndSelf().OfType<ElementAccessExpressionSyntax>()
                .Any(e => IsTranscript(e.Expression.ToString())
                          && e.ArgumentList.Arguments.Any(a => a.Expression is RangeExpressionSyntax))))
        {
            hits.Add("rebuilds the transcript by slicing a range and concatenating");
        }

        // Writing through an indexer into a text buffer built from the transcript.
        foreach (var assign in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assign.Left is not ElementAccessExpressionSyntax target) continue;
            var buffer = target.Expression.ToString();
            if (IsBufferFromTranscript(root, buffer) || IsTranscript(buffer))
                hits.Add($"writes through {buffer}[...], a text buffer built from the transcript");
        }

        return hits;
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
