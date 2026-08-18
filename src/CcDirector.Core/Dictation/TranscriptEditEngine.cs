using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Dictation.Models;

namespace CcDirector.Core.Dictation;

/// <summary>
/// One proposed dictionary correction:
/// replace every standalone occurrence of <see cref="Find"/> (text copied
/// verbatim from the raw transcript) with <see cref="Replace"/> (a canonical
/// dictionary term).
/// </summary>
public sealed record TranscriptEdit(string Find, string Replace);

/// <summary>One rejected edit plus the reason it was refused.</summary>
public sealed record RejectedEdit(TranscriptEdit Edit, string Reason);

/// <summary>Outcome of validating a proposed edit document against the raw transcript and dictionary.</summary>
public sealed record EditValidation(
    IReadOnlyList<TranscriptEdit> Accepted,
    IReadOnlyList<RejectedEdit> Rejected);

/// <summary>
/// Deterministic core of the dictation cleanup pass (issue #190).
///
/// No model echoes the transcript (the mechanism behind every logged corruption:
/// paraphrasing, truncation, refusals, few-shot leakage). THIS class is the only
/// thing that ever touches the user's words:
///
///   1. <see cref="ParseEdits"/>   - strict JSON parse of the ORIGINAL edit-document
///                                   protocol, in which the model returned its own
///                                   find/replace pairs. HISTORICAL: it has no
///                                   production caller. The judge answers with
///                                   candidate ids over spans this code isolated
///                                   first, which is strictly tighter - it cannot
///                                   name a span nobody offered. Kept because the
///                                   validation below is shared.
///   2. <see cref="Validate"/>     - every edit must point at text that exists
///                                   in the raw transcript and rewrite it to a
///                                   canonical dictionary term that the found
///                                   text plausibly misheard. Everything else
///                                   is rejected with a reason.
///   3. <see cref="Apply"/>        - plain boundary-aware string replacement
///                                   of the surviving edits on the RAW text.
///
/// The user's transcript never round-trips through the model, so the model
/// physically cannot reword, summarize, answer, refuse, or inject example
/// text. The worst it can do is propose a bad edit, and bad edits die here.
///
/// Pure and static: no I/O, no logging, no model - fully unit-testable.
///
/// INVARIANT (transcription integrity - see docs/CodingStyle.md section 16):
/// This is the ONLY code in the product allowed to change a user's transcribed
/// words, and the only change it may make is applying a validated dictionary
/// find/replace. A language model may RULE on spans this code isolated first,
/// answering with candidate ids; it must NEVER receive the transcript and return
/// free text used as the user's words. Do not add a second cleanup path and do not route a transcript
/// through a text-generating model.
///
/// HOW MUCH OF THIS IS ACTUALLY ENFORCED: the model half is, structurally - a
/// judge returns candidate ids and is handed a read-only list, so it can neither
/// return text nor reach the candidates that get applied
/// (TranscriptionIntegrityGuardTests). The "only this class rewrites transcript
/// text" half is NOT checked by anything; it is a rule people follow. This
/// comment used to claim a build-time test that did not exist, which is worse
/// than claiming nothing - a reader trusts it. See devthrottle_internal#1556.
/// </summary>
public static class TranscriptEditEngine
{
    /// <summary>Maximum edits considered per transcript; the rest are rejected.</summary>
    internal const int MaxEdits = 16;

    /// <summary>Maximum length of a single find span, in characters.</summary>
    internal const int MaxFindChars = 40;

    /// <summary>Maximum length of a single find span, in whitespace-separated words.</summary>
    internal const int MaxFindWords = 4;

    /// <summary>
    /// Minimum normalized Levenshtein similarity between an UNLISTED find and
    /// its canonical replacement for the edit to count as a plausible
    /// mishearing. Listed wrong forms from the dictionary bypass this gate.
    /// Calibrated so real phonetic mishearings ("Mindsy", "Teraskale",
    /// "See Director") pass while unrelated words ("Claude" -> cc-director,
    /// "conformance" -> CenCon - both real logged corruptions) are blocked.
    /// </summary>
    internal const double MinSimilarity = 0.55;

    /// <summary>Minimum length of a whole word shared by find and replace for the token-overlap plausibility rule.</summary>
    internal const int MinSharedTokenLength = 5;

    // The find text originates from the model (external input). It is always
    // Regex.Escape'd so the pattern cannot backtrack catastrophically, but the
    // CodingStyle rule stands: every regex over external input gets a timeout.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Parse the model's output as an edit document. Returns null when the
    /// output is not a well-formed <c>{"edits":[{"find":...,"replace":...}]}</c>
    /// object - including prose, refusals, narration, or leaked example text.
    /// A null result means "ship the raw transcript untouched".
    /// </summary>
    public static IReadOnlyList<TranscriptEdit>? ParseEdits(string? modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput)) return null;
        try
        {
            using var doc = JsonDocument.Parse(modelOutput);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("edits", out var editsProp)) return null;
            if (editsProp.ValueKind != JsonValueKind.Array) return null;

            var edits = new List<TranscriptEdit>();
            foreach (var item in editsProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                if (!item.TryGetProperty("find", out var f) || f.ValueKind != JsonValueKind.String) return null;
                if (!item.TryGetProperty("replace", out var r) || r.ValueKind != JsonValueKind.String) return null;
                edits.Add(new TranscriptEdit(f.GetString() ?? "", r.GetString() ?? ""));
            }
            return edits;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validate proposed edits against the raw transcript and the dictionary.
    /// An edit survives only when ALL of these hold:
    ///   - the find text is non-empty and within the span caps;
    ///   - the find text occurs verbatim in the raw transcript;
    ///   - the replacement is exactly one of the canonical dictionary terms
    ///     (vocabulary entries or mistranscription-map keys);
    ///   - the find text is not itself a canonical term (a correct term must
    ///     never be rewritten into a different term);
    ///   - the find text is a plausible mishearing of the replacement: a
    ///     listed wrong form for that term, a capitalization variant of it,
    ///     or close enough by string similarity / shared-word overlap.
    /// No-op edits (find == replace) are dropped silently.
    /// </summary>
    public static EditValidation Validate(
        IReadOnlyList<TranscriptEdit> edits,
        string rawTranscript,
        DictationDictionary dictionary)
    {
        var canonical = BuildCanonicalSet(dictionary);
        var accepted = new List<TranscriptEdit>();
        var rejected = new List<RejectedEdit>();

        foreach (var edit in edits)
        {
            if (accepted.Count >= MaxEdits)
            {
                rejected.Add(new RejectedEdit(edit, $"edit limit of {MaxEdits} exceeded"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(edit.Find))
            {
                rejected.Add(new RejectedEdit(edit, "empty find text"));
                continue;
            }
            if (string.Equals(edit.Find, edit.Replace, StringComparison.Ordinal))
                continue; // no-op; harmless, not worth a rejection record
            if (edit.Find.Length > MaxFindChars || CountWords(edit.Find) > MaxFindWords)
            {
                rejected.Add(new RejectedEdit(edit, $"find span exceeds {MaxFindChars} chars / {MaxFindWords} words"));
                continue;
            }
            if (!canonical.Contains(edit.Replace))
            {
                rejected.Add(new RejectedEdit(edit, "replacement is not a canonical dictionary term"));
                continue;
            }
            if (!rawTranscript.Contains(edit.Find, StringComparison.Ordinal))
            {
                rejected.Add(new RejectedEdit(edit, "find text does not occur in the transcript"));
                continue;
            }
            if (canonical.Contains(edit.Find))
            {
                rejected.Add(new RejectedEdit(edit, "find text is already a canonical term"));
                continue;
            }
            if (!IsPlausibleMishearing(edit.Find, edit.Replace, dictionary))
            {
                rejected.Add(new RejectedEdit(edit, $"'{edit.Find}' is not a plausible mishearing of '{edit.Replace}'"));
                continue;
            }
            accepted.Add(edit);
        }

        return new EditValidation(accepted, rejected);
    }

    /// <summary>
    /// Apply accepted edits to the raw transcript. Longer finds are applied
    /// first so a shorter find can never corrupt a longer phrase it is part
    /// of. Replacement is boundary-aware: a find that starts or ends with a
    /// letter/digit only matches where it is not glued to another letter/digit,
    /// so "Conty" never rewrites the inside of "Contying". Returns the edited
    /// text and the number of edits that actually changed something.
    /// </summary>
    public static (string Text, int AppliedCount) Apply(
        string rawTranscript,
        IReadOnlyList<TranscriptEdit> accepted)
    {
        var text = rawTranscript;
        var appliedCount = 0;
        foreach (var edit in accepted.OrderByDescending(e => e.Find.Length))
        {
            var replaced = ReplaceWithBoundaries(text, edit.Find, edit.Replace, out var hits);
            if (hits > 0)
            {
                text = replaced;
                appliedCount++;
            }
        }
        return (text, appliedCount);
    }

    /// <summary>
    /// Apply judged corrections AT THEIR OFFSETS. Each accepted candidate rewrites the one span it was
    /// judged about and nothing else.
    ///
    /// This is the difference between a ruling and a rule. <see cref="Apply"/> rewrites every occurrence
    /// of the find string in the utterance, which is right for a wrong form the user listed by hand -
    /// they meant it everywhere. It is wrong for a judged correction: the judge ruled on "sure" in one
    /// sentence, and a second "sure" later in the same breath is a different word it never saw.
    ///
    /// Edits are applied right to left so an earlier offset is never shifted by a later replacement, and
    /// a candidate whose span no longer matches the text verbatim is skipped rather than trusted - an
    /// offset that has gone stale is a bug in the caller, and guessing past it would corrupt the turn.
    /// </summary>
    public static (string Text, int AppliedCount) ApplyAt(
        string rawTranscript,
        IReadOnlyList<JudgeCandidate> accepted)
    {
        var applied = 0;
        var text = rawTranscript;
        foreach (var c in accepted.OrderByDescending(c => c.Start))
        {
            if (c.Start < 0 || c.Start + c.Find.Length > text.Length)
                continue;
            if (string.CompareOrdinal(text, c.Start, c.Find, 0, c.Find.Length) != 0)
                continue;
            text = text[..c.Start] + c.Replace + text[(c.Start + c.Find.Length)..];
            applied++;
        }
        return (text, applied);
    }

    // ===== internals =========================================================

    private static HashSet<string> BuildCanonicalSet(DictationDictionary dictionary)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in dictionary.Vocabulary) set.Add(term);
        foreach (var key in dictionary.CommonMistranscriptions.Keys) set.Add(key);
        return set;
    }

    /// <summary>
    /// Is <paramref name="find"/> believably a mishearing of the canonical
    /// <paramref name="replace"/>? Three ways in:
    ///   1. It is a listed wrong form for that exact term in the dictionary.
    ///   2. It is a capitalization variant of the term itself.
    ///   3. It is phonetically close (normalized Levenshtein) or shares a
    ///      substantial whole word with the term ("See Director" shares
    ///      "director" with "cc-director").
    /// </summary>
    internal static bool IsPlausibleMishearing(string find, string replace, DictationDictionary dictionary)
    {
        if (dictionary.CommonMistranscriptions.TryGetValue(replace, out var wrongForms)
            && wrongForms.Any(w => string.Equals(w, find, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (string.Equals(find, replace, StringComparison.OrdinalIgnoreCase))
            return true; // capitalization fix

        if (NormalizedSimilarity(find, replace) >= MinSimilarity)
            return true;

        return SharesSubstantialToken(find, replace);
    }

    /// <summary>Normalized Levenshtein similarity in [0,1] over lowercased input. 1 = identical.</summary>
    internal static double NormalizedSimilarity(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 1.0;
        return 1.0 - (double)Levenshtein(a, b) / maxLen;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    private static bool SharesSubstantialToken(string find, string replace)
    {
        var findTokens = Tokenize(find);
        var replaceTokens = Tokenize(replace);
        return findTokens.Any(f => f.Length >= MinSharedTokenLength
            && replaceTokens.Any(r => string.Equals(f, r, StringComparison.OrdinalIgnoreCase)));
    }

    private static string[] Tokenize(string s)
        => Regex.Split(s, @"[^\p{L}\p{Nd}]+", RegexOptions.None, RegexTimeout)
            .Where(t => t.Length > 0).ToArray();

    private static int CountWords(string s)
        => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string ReplaceWithBoundaries(string text, string find, string replace, out int count)
    {
        var pattern = (char.IsLetterOrDigit(find[0]) ? @"(?<![\p{L}\p{Nd}])" : "")
                      + Regex.Escape(find)
                      + (char.IsLetterOrDigit(find[^1]) ? @"(?![\p{L}\p{Nd}])" : "");
        var hits = 0;
        var result = Regex.Replace(text, pattern, _ => { hits++; return replace; }, RegexOptions.None, RegexTimeout);
        count = hits;
        return result;
    }
}
