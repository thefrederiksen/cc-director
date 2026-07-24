using System.Text;
using System.Text.RegularExpressions;
using CcDirector.Core.Dictation.Models;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Deterministic, in-process miner that turns a tenant's stored dictation transcripts into a ranked list of
/// dictionary-term SUGGESTIONS (devthrottle issue #2075). It runs, on the server, the same analysis that was
/// done by hand to seed the issue: look across what the speech model actually heard, find the distinctive
/// terms the customer keeps saying that the model spells several near-identical ways, and surface each one -
/// with its wrong spellings and how often - as a term to add.
///
/// THE SIGNAL. For a term the customer has NOT yet put in their dictionary, the cleanup pass leaves it
/// untouched, so the mistranscriptions sit in the raw transcripts verbatim. The tell is INCONSISTENCY: the
/// model spells an ordinary word the same way every time, but a proper noun or a piece of jargon
/// ("mindzie", "ConPty", "Frederiksen") comes out as a cluster of near-spellings around the correct one.
/// The miner clusters phonetically-near spellings, takes the most-frequent spelling in a cluster as the
/// canonical term, and treats the rest as the wrong spellings - the evidence.
///
/// PURE AND STATIC, like <see cref="FuzzyDictionaryMatcher"/>: no I/O, no model, no network, no clock. The
/// transcripts, the current dictionary and the dismissed set are all passed in; the ranked suggestions are
/// returned. That makes the whole mining policy unit-testable end to end, and keeps the Gateway service that
/// calls it a thin read-compute-cache shell.
///
/// LANGUAGE-AGNOSTIC, by the same three signals <see cref="FuzzyDictionaryMatcher"/> relies on: a
/// conservative similarity threshold, the base <see cref="FuzzyDictionaryMatcher.Jaro"/> metric (no Winkler
/// prefix bonus, so a shared prefix does not manufacture matches), and a minimum span length. It ships no
/// word list, so it works whatever language the customer dictates in - the discriminator is that the model
/// is INCONSISTENT about a term, which ordinary vocabulary is not, and that is language-neutral.
///
/// EXCLUSIONS. A term already in the tenant's Vocabulary or Common-mistranscriptions (as a canonical term OR
/// as a known wrong spelling) is not suggested - it is already handled. A term the tenant has DISMISSED is
/// not suggested until they restore it. Both are matched on the same normalized form the clustering uses, so
/// casing and punctuation never re-open a handled or dismissed term.
///
/// SINGLE-WORD TERMS ONLY, for now. The miner suggests one-token terms ("mindzie", "ConPty", "Frederiksen" -
/// three of the four real examples that seeded issue #2075). A two-word term ("Center Consulting") is a known
/// follow-up: gluing a term to a neighbouring word needs a way to reject function-word neighbours, and doing
/// that without a per-language stop list is real work that a noisy first cut would get wrong. So the miner
/// stays deliberately conservative rather than shipping spurious multi-word rows.
/// </summary>
public static class MistranscriptionMiner
{
    /// <summary>Tunable mining policy. The defaults are deliberately STRICT (issue #2075 open question 1):
    /// loosening later is invisible, tightening later looks like the feature got worse, so start strict.</summary>
    public sealed record Options(
        /// <summary>A term must be heard WRONG at least this many times before it is suggested.</summary>
        int MinWrongCount = 3,
        /// <summary>A term must be heard wrong at least this fraction of the times it was said.</summary>
        double MinWrongRatio = 0.25,
        /// <summary>Below this many normalized characters a term is too collision-prone to trust.</summary>
        int MinTermChars = 4,
        /// <summary>Minimum Jaro similarity for two spellings to be treated as the same term. Single-linkage,
        /// so a chain of near-neighbours (mindzie ~ Mindzee ~ Mindsee) gathers into one term even when the
        /// ends are further apart than one hop.</summary>
        double ClusterThreshold = 0.82,
        /// <summary>Cap on suggestions returned, highest-evidence first.</summary>
        int MaxSuggestions = 50,
        /// <summary>Cap on the distinct wrong spellings shown/written per suggestion, highest-count first.</summary>
        int MaxVariantsPerTerm = 8,
        /// <summary>Performance bound: only the most-frequent this-many distinct spellings are clustered. Rare
        /// one-off spellings beyond this cannot form a suggestion on their own anyway.</summary>
        int MaxDistinctSpellings = 4000)
    {
        public static Options Default { get; } = new();
    }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    // The same token shape FuzzyDictionaryMatcher uses: a letter/digit start, then letters/digits/apostrophe/
    // hyphen. So "Con-TY" is ONE token (the hyphen is intra-word), matching how the model emits it.
    private static readonly Regex TokenRegex = new(
        @"[\p{L}\p{Nd}][\p{L}\p{Nd}'\-]*", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

    /// <summary>
    /// Mine <paramref name="rawTranscripts"/> for suggested dictionary terms, excluding anything already in
    /// <paramref name="dictionary"/> or in <paramref name="dismissedTerms"/>. Returns an empty list when
    /// nothing clears the evidence bar. Deterministic: same inputs, same order out.
    /// </summary>
    /// <param name="rawTranscripts">What the model heard, one string per stored utterance. Nulls are skipped.</param>
    /// <param name="dictionary">The tenant's current glossary; its terms and wrong spellings are excluded.</param>
    /// <param name="dismissedTerms">Terms the tenant dismissed; excluded until restored. May be empty.</param>
    /// <param name="options">Mining policy; <see cref="Options.Default"/> when null.</param>
    public static IReadOnlyList<MistranscriptionSuggestion> Mine(
        IEnumerable<string?> rawTranscripts,
        DictationDictionary dictionary,
        IReadOnlyCollection<string> dismissedTerms,
        Options? options = null)
    {
        if (rawTranscripts is null) throw new ArgumentNullException(nameof(rawTranscripts));
        if (dictionary is null) throw new ArgumentNullException(nameof(dictionary));
        dismissedTerms ??= Array.Empty<string>();
        var opts = options ?? Options.Default;

        var known = BuildKnownNormSet(dictionary, dismissedTerms);
        var transcripts = rawTranscripts.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();

        var results = MineWords(transcripts, known, opts);

        // Rank: most-wrong first, then most-said, then term (ordinal) for a stable, deterministic order.
        return results
            .OrderByDescending(s => s.WrongCount)
            .ThenByDescending(s => s.TotalCount)
            .ThenBy(s => s.Term, StringComparer.Ordinal)
            .Take(opts.MaxSuggestions)
            .ToList();
    }

    /// <summary>Count single-word spellings, cluster the near-identical ones, and keep the clusters that clear
    /// the evidence bar as suggestions (unranked - <see cref="Mine"/> ranks the result).</summary>
    private static List<MistranscriptionSuggestion> MineWords(
        IReadOnlyList<string> transcripts, HashSet<string> known, Options opts)
    {
        // normKey -> (surface spelling -> count). One normalized key gathers every casing/punctuation of a
        // spelling ("Con-TY" and "ConTY" both normalize to "conty" but stay distinct surface rows).
        var spellings = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var transcript in transcripts)
        {
            foreach (var token in Tokenize(transcript))
            {
                var norm = Normalize(token);
                if (norm.Length < opts.MinTermChars)
                    continue;
                if (!spellings.TryGetValue(norm, out var forms))
                    spellings[norm] = forms = new Dictionary<string, int>(StringComparer.Ordinal);
                forms[token] = forms.TryGetValue(token, out var c) ? c + 1 : 1;
            }
        }

        var keyTotals = spellings.ToDictionary(kv => kv.Key, kv => kv.Value.Values.Sum(), StringComparer.Ordinal);

        // Performance bound: cluster only the most-frequent distinct keys. A one-off spelling beyond the cap
        // cannot on its own make a >= MinWrongCount cluster, so dropping the long tail changes no real result.
        var keys = keyTotals.Keys
            .OrderByDescending(k => keyTotals[k])
            .ThenBy(k => k, StringComparer.Ordinal)
            .Take(opts.MaxDistinctSpellings)
            .ToList();

        var clusters = Cluster(keys, opts.ClusterThreshold);

        var results = new List<MistranscriptionSuggestion>();
        foreach (var cluster in clusters)
        {
            // Merge every surface spelling across the cluster's normalized keys into one tally.
            var surfaceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var key in cluster)
                foreach (var kv in spellings[key])
                    surfaceCounts[kv.Key] = surfaceCounts.TryGetValue(kv.Key, out var c) ? c + kv.Value : kv.Value;

            var total = surfaceCounts.Values.Sum();

            // Canonical = the most-frequent spelling (the form the model gets right when the audio is clear).
            // Tie-break by ordinal so the choice is deterministic.
            var canonical = surfaceCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .First().Key;
            var canonicalNorm = Normalize(canonical);

            // Already in the dictionary (as a term or a known wrong spelling) or dismissed - not a suggestion.
            if (known.Contains(canonicalNorm))
                continue;
            if (canonicalNorm.Length < opts.MinTermChars)
                continue;

            // Wrong spellings: every surface form that is not the canonical AND is not itself a real
            // dictionary term (a wrong spelling that happens to be a vocabulary word is not a mishearing).
            var variantRows = surfaceCounts
                .Where(kv => Normalize(kv.Key) != canonicalNorm && !known.Contains(Normalize(kv.Key)))
                .GroupBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(g => new MistranscriptionVariant(g.Key, g.Sum(x => x.Value)))
                .OrderByDescending(v => v.Count)
                .ThenBy(v => v.Heard, StringComparer.Ordinal)
                .ToList();

            var wrongCount = variantRows.Sum(v => v.Count);
            if (variantRows.Count < 1 || wrongCount < opts.MinWrongCount)
                continue;
            if (total <= 0 || (double)wrongCount / total < opts.MinWrongRatio)
                continue;

            if (variantRows.Count > opts.MaxVariantsPerTerm)
                variantRows = variantRows.Take(opts.MaxVariantsPerTerm).ToList();

            results.Add(new MistranscriptionSuggestion(canonical, variantRows, wrongCount, total));
        }

        return results;
    }

    /// <summary>
    /// Single-linkage (connected-components) clustering, blocked by first normalized character to stay
    /// near-linear on large inputs. Two spellings are linked when their Jaro similarity clears the threshold
    /// (base metric, with a cheap length-ratio pre-filter); a cluster is a connected component of those
    /// links. Single-linkage is what lets a chain of drifting spellings - mindzie ~ Mindzee ~ Mindsee, where
    /// the two ends are further apart than one hop - still gather into one term rather than fragmenting the
    /// evidence for that term across several weak suggestions.
    /// </summary>
    private static List<List<string>> Cluster(List<string> keys, double threshold)
    {
        // Block by first normalized char: a mishearing almost always keeps the initial sound, and this turns
        // an O(K^2) sweep into O(sum b_i^2) over much smaller blocks.
        var blocks = new Dictionary<char, List<string>>();
        foreach (var key in keys)
        {
            var head = key[0];
            if (!blocks.TryGetValue(head, out var list))
                blocks[head] = list = new List<string>();
            list.Add(key);
        }

        var clusters = new List<List<string>>();
        foreach (var block in blocks.Values)
        {
            // Union-find over the block, then group by representative root.
            var parent = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var k in block) parent[k] = k;
            string Find(string x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            void Union(string a, string b)
            {
                var ra = Find(a); var rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }

            for (var i = 0; i < block.Count; i++)
                for (var j = i + 1; j < block.Count; j++)
                {
                    if (!LengthRatioOk(block[i], block[j]))
                        continue;
                    if (FuzzyDictionaryMatcher.Jaro(block[i], block[j]) >= threshold)
                        Union(block[i], block[j]);
                }

            var byRoot = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var k in block)
            {
                var root = Find(k);
                if (!byRoot.TryGetValue(root, out var members))
                    byRoot[root] = members = new List<string>();
                members.Add(k);
            }
            clusters.AddRange(byRoot.Values);
        }
        return clusters;
    }

    /// <summary>Cheap pre-filter before scoring: two spellings whose lengths differ by more than a third are
    /// never the same term, and comparing them just wastes a Jaro pass.</summary>
    private static bool LengthRatioOk(string a, string b)
    {
        var min = Math.Min(a.Length, b.Length);
        var max = Math.Max(a.Length, b.Length);
        return max != 0 && (double)min / max >= 0.6;
    }

    /// <summary>The set of normalized forms already handled: every vocabulary term, every
    /// common-mistranscription key AND its wrong spellings, and every dismissed term. Matched against the
    /// same normalization the clustering uses, so casing and punctuation never re-open a handled term.</summary>
    private static HashSet<string> BuildKnownNormSet(DictationDictionary dictionary, IEnumerable<string> dismissed)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? s) { var n = Normalize(s ?? ""); if (n.Length > 0) known.Add(n); }
        foreach (var v in dictionary.Vocabulary) Add(v);
        foreach (var kv in dictionary.CommonMistranscriptions)
        {
            Add(kv.Key);
            foreach (var variant in kv.Value) Add(variant);
        }
        foreach (var d in dismissed) Add(d);
        return known;
    }

    /// <summary>Split a string into surface tokens (a letter/digit start, then letters/digits/'/-).</summary>
    private static IEnumerable<string> Tokenize(string s)
    {
        foreach (Match m in TokenRegex.Matches(s))
            yield return m.Value;
    }

    /// <summary>Lower-cased letters and digits only - identical to <see cref="FuzzyDictionaryMatcher"/>'s
    /// normalization, so a term matched there and mined here fold to the same key.</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
