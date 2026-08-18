using System.Text.RegularExpressions;
using CcDirector.Core.Dictation.Models;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Deterministic, in-process candidate generator for dictation cleanup.
///
/// Given a raw transcript and the dictation dictionary, it PROPOSES find/replace edits that map
/// misheard spans onto canonical vocabulary terms, using string similarity over word windows. This is
/// the replacement for the hosted language model that previously located mishearings: issue #190
/// always allowed a model to LOCATE edits (never to rewrite text), and this class does exactly that
/// job deterministically, in microseconds, with no network call.
///
/// It never edits text itself - it only returns proposals. Every proposal is validated and applied by
/// <see cref="TranscriptEditEngine"/> (canonical-term check, verbatim-occurrence check,
/// plausible-mishearing gate, boundary-aware replacement), so the transcription-integrity invariant is
/// unchanged: the only thing that ever rewrites a user's words is a validated dictionary swap.
///
/// LANGUAGE-AGNOSTIC BY DESIGN. It ships no word list and makes no assumption about the language of the
/// dictation, so it works for any language the user dictates in. Precision instead comes from three
/// language-neutral signals:
///   - a conservative similarity threshold (only confident mishearings are proposed);
///   - the base <see cref="Jaro"/> metric WITHOUT the Winkler prefix bonus - a shared prefix is how a
///     legitimate word collides with a similar-looking term ("avalanche" vs "Avalonia"), so rewarding
///     it would cause exactly the false positives we must avoid;
///   - the user's own curated exact/alias map (handled before this stage) and the engine's own
///     plausibility gate as the backstop.
/// Anything the fuzzy layer is not confident about is left untouched; the user adds an alias for it.
///
/// Pure and static: no I/O, no model, no network - fully unit-testable.
/// </summary>
public static class FuzzyDictionaryMatcher
{
    /// <summary>Canonical terms expand from at most this many spoken tokens ("Acme Flow" -> "acmeflow").</summary>
    internal const int MaxWindow = 2;

    /// <summary>Below this many alphanumeric characters, similarity is too collision-prone to trust.</summary>
    internal const int MinSpanChars = 4;

    /// <summary>
    /// Minimum similarity for a single-word span to be proposed as a mishearing. Conservative on
    /// purpose: it must clear a real word that merely resembles a term. The engine's own
    /// <see cref="TranscriptEditEngine.MinSimilarity"/> gate (0.55) is the final backstop.
    /// </summary>
    internal const double ProposeThreshold = 0.78;

    /// <summary>
    /// Minimum similarity for a MULTI-word window to collapse onto a term. Near-exact: a real
    /// multi-word spoken form of a term ("Acme Flow" -> "acmeflow") matches almost perfectly, whereas
    /// "&lt;ordinary word&gt; &lt;misheard term&gt;" ("run Akmeflow") matches on one half only and must NOT win
    /// the span and starve the correct single-word match.
    /// </summary>
    internal const double MultiWordThreshold = 0.90;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Propose dictionary corrections for <paramref name="rawTranscript"/>. Returns an empty list when
    /// nothing is a confident mishearing. The proposals are candidates only - pass them through
    /// <see cref="TranscriptEditEngine.Validate"/> / <see cref="TranscriptEditEngine.Apply"/> before
    /// anything reaches the user.
    /// </summary>
    public static IReadOnlyList<TranscriptEdit> Propose(string rawTranscript, DictationDictionary dictionary)
    {
        // Deduped by exact found text, because Apply rewrites every occurrence of a find anyway.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var edits = new List<TranscriptEdit>();
        foreach (var c in ProposeCandidates(rawTranscript, dictionary))
            if (seen.Add(c.Find))
                edits.Add(new TranscriptEdit(c.Find, c.Replace));
        return edits;
    }

    /// <summary>
    /// The same scan, but keeping WHERE each span was found and every separate occurrence of it.
    ///
    /// This is what the judge is given. <see cref="Propose"/> collapses repeats because its consumer
    /// rewrites the whole utterance per find; a judge rules on one occurrence in one sentence, and a
    /// second "sure" three words later is a different use of the word that it never saw. Keeping the
    /// occurrences apart is what lets one ruling change one span - see
    /// <see cref="TranscriptEditEngine.ApplyAt"/>.
    ///
    /// Ids are assigned in document order and are the judge's entire vocabulary.
    /// </summary>
    public static IReadOnlyList<JudgeCandidate> ProposeCandidates(
        string rawTranscript, DictationDictionary dictionary)
    {
        var found = Scan(rawTranscript, dictionary);
        var ordered = found.OrderBy(f => f.Start).ToList();
        var candidates = new List<JudgeCandidate>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
            candidates.Add(new JudgeCandidate(i, ordered[i].Find, ordered[i].Replace, ordered[i].Start));
        return candidates;
    }

    private readonly record struct Found(string Find, string Replace, int Start);

    private static List<Found> Scan(string rawTranscript, DictationDictionary dictionary)
    {
        var none = new List<Found>();
        if (string.IsNullOrWhiteSpace(rawTranscript))
            return none;

        var targets = BuildTargets(dictionary);
        if (targets.Count == 0)
            return none;

        var canonicalNorms = new HashSet<string>(targets.Select(t => t.Norm), StringComparer.Ordinal);

        var tokens = Regex.Matches(rawTranscript, @"[\p{L}\p{Nd}][\p{L}\p{Nd}'\-]*",
            RegexOptions.CultureInvariant, RegexTimeout);
        if (tokens.Count == 0)
            return none;

        var used = new bool[tokens.Count];
        var proposals = new List<Found>();

        // Prefer longer windows first so a two-word spoken form ("Acme Flow") wins over a one-word match.
        for (int win = MaxWindow; win >= 1; win--)
        {
            for (int i = 0; i + win <= tokens.Count; i++)
            {
                if (AnyUsed(used, i, win))
                    continue;

                // Precision guard for multi-word windows: never glue a term to a neighbouring word that
                // is already a correct canonical term ("the cc-director" -> drop "the"). Together with
                // the near-exact multi-word threshold this also blocks gluing a term to a filler word
                // ("Akmeflow and"), with no language-specific stop list.
                if (win > 1 && MultiWordWindowTouchesCanonical(tokens, i, win, canonicalNorms))
                    continue;

                var spanText = rawTranscript[tokens[i].Index..(tokens[i + win - 1].Index + tokens[i + win - 1].Length)];
                var spanNorm = Normalize(spanText);
                if (spanNorm.Length < MinSpanChars)
                    continue;

                var (bestTerm, bestScore) = BestMatch(spanNorm, targets);
                if (bestTerm is null)
                    continue;

                // Already exactly correct (spelling and casing) - nothing to do.
                if (string.Equals(spanText, bestTerm, StringComparison.Ordinal))
                    continue;

                // A multi-word window must match near-exactly; a single word only needs the base bar.
                var threshold = win > 1 ? MultiWordThreshold : ProposeThreshold;
                if (bestScore < threshold)
                    continue;

                proposals.Add(new Found(spanText, bestTerm, tokens[i].Index));
                MarkUsed(used, i, win);
            }
        }

        return proposals;
    }

    private readonly record struct Target(string Canonical, string Norm, int TokenCount);

    private static List<Target> BuildTargets(DictationDictionary dictionary)
    {
        // The canonical set is the vocabulary plus the mistranscription-map keys - exactly the set
        // TranscriptEditEngine treats as valid replacements, so we never propose a non-canonical term.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<Target>();
        void Add(string term)
        {
            if (string.IsNullOrWhiteSpace(term) || !seen.Add(term))
                return;
            var norm = Normalize(term);
            if (norm.Length == 0)
                return;
            list.Add(new Target(term, norm, CountTokens(term)));
        }
        foreach (var v in dictionary.Vocabulary) Add(v);
        foreach (var k in dictionary.CommonMistranscriptions.Keys) Add(k);
        return list;
    }

    private static (string? Term, double Score) BestMatch(string spanNorm, List<Target> targets)
    {
        string? best = null;
        double bestScore = 0.0;
        foreach (var t in targets)
        {
            // Length-ratio guard: skip nonsense comparisons cheaply before scoring.
            var minLen = Math.Min(spanNorm.Length, t.Norm.Length);
            var maxLen = Math.Max(spanNorm.Length, t.Norm.Length);
            if (maxLen == 0 || (double)minLen / maxLen < 0.6)
                continue;
            var score = Score(spanNorm, t.Norm);
            if (score > bestScore)
            {
                bestScore = score;
                best = t.Canonical;
            }
        }
        return (best, bestScore);
    }

    /// <summary>Similarity used to propose a correction: the base Jaro metric, deliberately WITHOUT the
    /// Winkler prefix bonus. A shared leading substring is the signature of a real word colliding with a
    /// similar-looking term, so rewarding it would manufacture false positives; the tail-sensitive Jaro
    /// score separates a true mishearing ("Terascale" ~ "Tailscale") from an innocent word that merely
    /// starts the same ("avalanche" vs "Avalonia").</summary>
    private static double Score(string a, string b) => Jaro(a, b);

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static int CountTokens(string s)
        => Regex.Matches(s, @"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant, RegexTimeout).Count;

    private static bool AnyUsed(bool[] used, int start, int len)
    {
        for (int k = start; k < start + len; k++)
            if (used[k]) return true;
        return false;
    }

    private static void MarkUsed(bool[] used, int start, int len)
    {
        for (int k = start; k < start + len; k++)
            used[k] = true;
    }

    private static bool MultiWordWindowTouchesCanonical(
        MatchCollection tokens, int start, int len, HashSet<string> canonicalNorms)
    {
        for (int k = start; k < start + len; k++)
            if (canonicalNorms.Contains(Normalize(tokens[k].Value)))
                return true;
        return false;
    }

    /// <summary>Jaro similarity in [0,1] (NO Winkler prefix bonus). Standard algorithm; pure and
    /// allocation-light. Works on any Unicode string, so it is language-agnostic.</summary>
    internal static double Jaro(string s1, string s2)
    {
        if (s1.Length == 0 && s2.Length == 0) return 1.0;
        if (s1.Length == 0 || s2.Length == 0) return 0.0;

        var matchDistance = Math.Max(s1.Length, s2.Length) / 2 - 1;
        if (matchDistance < 0) matchDistance = 0;

        var s1Matches = new bool[s1.Length];
        var s2Matches = new bool[s2.Length];
        int matches = 0;

        for (int i = 0; i < s1.Length; i++)
        {
            int start = Math.Max(0, i - matchDistance);
            int end = Math.Min(i + matchDistance + 1, s2.Length);
            for (int j = start; j < end; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j]) continue;
                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0.0;

        double transpositions = 0;
        int point = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i]) continue;
            while (!s2Matches[point]) point++;
            if (s1[i] != s2[point]) transpositions++;
            point++;
        }
        transpositions /= 2;

        double m = matches;
        return ((m / s1.Length) + (m / s2.Length) + ((m - transpositions) / m)) / 3.0;
    }
}
