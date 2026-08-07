using System.Text;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Dictation.Models;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The model SCREENING pass over mined dictionary-suggestion candidates (devthrottle issue #2115). The
/// heuristic miner is good at FINDING clusters and provably bad at JUDGING them: it chained distinct common
/// words ("that" ~ "then" ~ "them") into "mistranscriptions" and topped the list with them. Judging whether
/// a cluster is a distinctive term (a name, a brand, jargon) or ordinary vocabulary is exactly the judgment
/// a language model already has - in EVERY language, which is what makes this multilingual out of the box
/// with no per-language word list to build or maintain.
///
/// CHUNKED CALLS: candidates go to the model <see cref="ChunkSize"/> at a time (a 50-candidate single batch
/// was measured blowing the model call's deadline on the owner's real corpus, and shorter answers also keep
/// the strict-JSON contract reliable). The caller persists every verdict (a term is judged at most once per
/// tenant, ever), so the steady-state cost is near zero.
///
/// FAIL LOUD: a model failure (unreachable, empty, unparseable, or missing terms) THROWS - the caller records
/// "screening unavailable" on the scan. Unjudged candidates are never guessed at and never shown unscreened;
/// the standing "no language model in the dictation path" rule is untouched because this never runs during
/// live dictation - only inside the daily scan or an explicit "Scan now".
/// </summary>
public static class DictionarySuggestionScreen
{
    /// <summary>Candidates per model call. Sized so one answer (a verdict object per candidate) stays well
    /// inside the inference call's deadline and the model holds the strict-JSON contract.</summary>
    public const int ChunkSize = 20;

    /// <summary>
    /// Judge <paramref name="candidates"/> with <paramref name="brain"/>, in chunks of <see cref="ChunkSize"/>.
    /// Returns one verdict per candidate, in candidate order.
    /// </summary>
    /// <param name="brain">The model to ask (the hosted inference brain in production; a stub in tests).</param>
    /// <param name="candidates">The unjudged candidates, each with its wrong-spelling evidence. Non-empty.</param>
    /// <param name="ct">Cancellation for the model call.</param>
    /// <exception cref="ArgumentNullException">The brain or candidates are null.</exception>
    /// <exception cref="ArgumentException">The candidate list is empty.</exception>
    /// <exception cref="InvalidOperationException">The model answered unusably (empty, unparseable JSON, or
    /// verdicts missing for some candidates). The model's own transport errors propagate as thrown by it.</exception>
    public static async Task<IReadOnlyList<DictionarySuggestionVerdictStore.Verdict>> JudgeAsync(
        IAgentBrain brain,
        IReadOnlyList<MistranscriptionSuggestion> candidates,
        CancellationToken ct = default)
    {
        if (brain is null) throw new ArgumentNullException(nameof(brain));
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (candidates.Count == 0) throw new ArgumentException("at least one candidate is required", nameof(candidates));

        var verdicts = new List<DictionarySuggestionVerdictStore.Verdict>(candidates.Count);
        for (var offset = 0; offset < candidates.Count; offset += ChunkSize)
        {
            var chunk = candidates.Skip(offset).Take(ChunkSize).ToList();
            var prompt = BuildPrompt(chunk);
            var answer = await brain.AskAsync(prompt, ct);
            verdicts.AddRange(ParseVerdicts(answer.Text, chunk));
        }
        return verdicts;
    }

    /// <summary>Build the one-batch judgment prompt. Internal so tests can pin its shape.</summary>
    internal static string BuildPrompt(IReadOnlyList<MistranscriptionSuggestion> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are screening candidate terms for a speech-to-text personal dictionary.");
        sb.AppendLine("The dictionary is applied AFTER transcription - it corrects the finished transcript,");
        sb.AppendLine("and nothing in it is ever sent to the speech model. The terms it holds are the");
        sb.AppendLine("distinctive ones the speech model keeps misspelling: product names, company names,");
        sb.AppendLine("people's names, and technical jargon.");
        sb.AppendLine();
        sb.AppendLine("A clustering pass over the user's dictation transcripts produced the candidates below.");
        sb.AppendLine("The clustering is naive: it groups near-identical spellings, so many candidates are");
        sb.AppendLine("actually clusters of DIFFERENT ordinary words (for example \"that\" grouped with");
        sb.AppendLine("\"then\", \"them\", \"there\") or one ordinary word with its grammatical forms (for");
        sb.AppendLine("example \"want\" with \"wanted\", \"wants\"). Those are NOT mistranscriptions and must");
        sb.AppendLine("be rejected. The user may dictate in any language; ordinary words of ANY language are");
        sb.AppendLine("rejected the same way.");
        sb.AppendLine();
        sb.AppendLine("APPROVE a candidate only when BOTH hold:");
        sb.AppendLine("1. The TERM ITSELF is a distinctive term - a proper noun, a brand, a person's name, or");
        sb.AppendLine("   a piece of domain jargon. An ordinary everyday word is never approved, however its");
        sb.AppendLine("   cluster looks.");
        sb.AppendLine("2. Its variants look like a speech model's MISSPELLINGS of that exact term - near-");
        sb.AppendLine("   phonetic non-words or rare words. The following are NOT misspellings and count as");
        sb.AppendLine("   evidence AGAINST a candidate:");
        sb.AppendLine("   - grammatical forms of the term (a plural, a past tense, an -ing form: \"issues\"");
        sb.AppendLine("     for \"issue\", \"killed\" for \"kill\") - the model heard those words correctly;");
        sb.AppendLine("   - DIFFERENT real words (\"project\" is not a misspelling of \"product\",");
        sb.AppendLine("     \"important\" is not a misspelling of \"implement\");");
        sb.AppendLine("   - DIFFERENT names or products (\"GPT-5\" is not a misspelling of \"GPT-4\",");
        sb.AppendLine("     \"OpenAI\" is not a misspelling of \"open\") - rewriting one into the other would");
        sb.AppendLine("     corrupt the user's text.");
        sb.AppendLine("REJECT everything else. When unsure, REJECT - a wrong approval corrupts every future");
        sb.AppendLine("dictation, a wrong rejection costs nothing.");
        sb.AppendLine();
        sb.AppendLine("Candidates (term, then the variant spellings heard for it):");
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var variants = string.Join(", ", c.Variants.Select(v => v.Heard));
            sb.AppendLine($"{i + 1}. \"{c.Term}\" heard as: {variants}");
        }
        sb.AppendLine();
        sb.AppendLine("Answer with ONLY a JSON array, no other text, one object per candidate, in order:");
        sb.AppendLine("[{\"term\":\"<the term exactly as listed>\",\"approved\":true|false,\"reason\":\"<one short sentence>\"}]");
        return sb.ToString();
    }

    /// <summary>Parse the model's JSON verdicts, tolerating a fenced code block around the array but nothing
    /// else. Internal so tests can drive it directly.</summary>
    /// <exception cref="InvalidOperationException">The text has no parseable JSON array, or a candidate got
    /// no verdict.</exception>
    internal static IReadOnlyList<DictionarySuggestionVerdictStore.Verdict> ParseVerdicts(
        string text, IReadOnlyList<MistranscriptionSuggestion> candidates)
    {
        var json = ExtractJsonArray(text);
        List<VerdictJson> parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<VerdictJson>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The screening model did not answer with valid JSON verdicts: {ex.Message}");
        }

        var byNorm = new Dictionary<string, VerdictJson>(StringComparer.Ordinal);
        foreach (var v in parsed)
            if (!string.IsNullOrWhiteSpace(v.term))
                byNorm[Normalize(v.term!)] = v;

        var verdicts = new List<DictionarySuggestionVerdictStore.Verdict>(candidates.Count);
        var missing = new List<string>();
        foreach (var candidate in candidates)
        {
            if (byNorm.TryGetValue(Normalize(candidate.Term), out var v))
                verdicts.Add(new DictionarySuggestionVerdictStore.Verdict(
                    candidate.Term, v.approved, (v.reason ?? "").Trim()));
            else
                missing.Add(candidate.Term);
        }
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"The screening model returned no verdict for: {string.Join(", ", missing)}");
        return verdicts;
    }

    /// <summary>The JSON array in the model's answer: the whole trimmed text, or the first bracketed span when
    /// the model wrapped it (a code fence, a leading sentence). Anything without a bracketed span fails.</summary>
    private static string ExtractJsonArray(string text)
    {
        var trimmed = (text ?? "").Trim();
        var start = trimmed.IndexOf('[');
        var end = trimmed.LastIndexOf(']');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("The screening model's answer contained no JSON array.");
        return trimmed.Substring(start, end - start + 1);
    }

    private sealed class VerdictJson
    {
        public string? term { get; set; }
        public bool approved { get; set; }
        public string? reason { get; set; }
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
