using System.Text;
using System.Text.Json;

namespace CcDirector.Core.Dictation;

/// <summary>
/// One span the fuzzy matcher thinks MIGHT be a misheard dictionary term, offered to the judge for a
/// ruling. The judge never sees a replacement it can edit - it sees the sentence, the exact words in
/// question, and the term they might be, and answers with ids.
/// </summary>
/// <param name="Id">Position in the offered list. The judge's whole vocabulary is these numbers.</param>
/// <param name="Find">The exact text as spoken, copied verbatim out of the transcript.</param>
/// <param name="Replace">The canonical dictionary term it might be.</param>
/// <param name="Start">Where <paramref name="Find"/> starts in the transcript. The edit is applied at
/// THIS offset and nowhere else, so one ruling can never change a word somewhere else in the turn.</param>
public sealed record JudgeCandidate(int Id, string Find, string Replace, int Start);

/// <summary>
/// Rules on candidate corrections in context. The ONLY thing in the product allowed to decide that an
/// UNLISTED word should be swapped.
///
/// The contract is deliberately narrow, and the narrowness is the safety property: an implementation
/// receives the utterance and a bounded candidate list, and returns the ids it accepts. It cannot
/// return text, cannot propose a candidate of its own, and cannot reach a word the matcher did not
/// already isolate. A hostile or broken implementation's worst case is accepting a bad candidate -
/// exactly the failure the deterministic matcher had by default - and it can never invent one.
/// </summary>
public interface ICandidateJudge
{
    /// <summary>
    /// Which of <paramref name="candidates"/> are real mishearings, given the sentence they sit in?
    /// Returns the accepted ids. An empty list means "none of them", which is always a safe answer.
    /// Implementations must not throw for a slow or unreachable backend - they return null, which the
    /// caller treats as "no ruling" and applies nothing.
    /// </summary>
    Task<IReadOnlyList<int>?> AcceptAsync(
        string utterance,
        IReadOnlyList<JudgeCandidate> candidates,
        CancellationToken ct = default);
}

/// <summary>
/// The wire protocol for <see cref="ICandidateJudge"/>: how the question is asked and how the answer is
/// read. Pure and static - no I/O, no model, no network - so the exact bytes we send and every shape of
/// reply we might get back are unit-testable without touching a backend.
///
/// The reply is parsed STRICTLY. Anything that is not a well-formed
/// <c>{"acceptedCandidateIds":[...]}</c> object of known ids is null, and null means nothing is
/// applied. Prose, an apology, a refusal, a rewritten sentence, an explanation wrapped around the JSON,
/// an id that was never offered - all of it fails closed. This is the lesson from the model cleanup
/// that was removed in July: the danger was never a wrong answer, it was accepting output shaped
/// differently from what we asked for.
/// </summary>
public static class CandidateJudgeProtocol
{
    /// <summary>Hard cap on candidates put to the judge in one turn. Beyond this the extra candidates
    /// are dropped unjudged, which means unapplied - a bounded prompt matters more than the tail.</summary>
    public const int MaxCandidates = 12;

    /// <summary>
    /// The instruction sent with every question. It states the job, the answer shape, and the tie-break:
    /// when unsure, reject. A missed correction costs a wrong spelling; a wrong acceptance costs the
    /// user's meaning, and those are not the same price.
    /// </summary>
    public const string SystemPrompt =
        "You decide whether words in a speech transcript were misheard versions of a known term.\n" +
        "You are given the transcript and a numbered list of candidates. Each candidate names the exact\n" +
        "spoken text and the term it might be.\n" +
        "\n" +
        "Accept a candidate ONLY if, reading the sentence, the speaker clearly meant the term and the\n" +
        "transcriber misheard it. Reject it if the spoken word is being used as an ordinary word of the\n" +
        "language, even when it looks similar to the term. When you are not sure, REJECT.\n" +
        "\n" +
        "Reply with JSON and nothing else, in exactly this shape:\n" +
        "{\"acceptedCandidateIds\": [0, 2]}\n" +
        "Use an empty array when none should be corrected. Never include any other field, any text\n" +
        "before or after the JSON, or any explanation.";

    /// <summary>Build the user message: the sentence, then the numbered candidates.</summary>
    public static string BuildUserPrompt(string utterance, IReadOnlyList<JudgeCandidate> candidates)
    {
        var sb = new StringBuilder();
        sb.Append("Transcript:\n").Append(utterance).Append("\n\nCandidates:\n");
        foreach (var c in candidates)
        {
            sb.Append(c.Id).Append(": \"").Append(c.Find).Append("\" might be \"")
              .Append(c.Replace).Append("\"\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Read a judge reply. Returns the accepted ids, or null when the reply is not exactly the shape we
    /// asked for. Null is not an error state to recover from - it means no ruling was obtained, so
    /// nothing is applied.
    /// </summary>
    /// <param name="reply">Raw model output.</param>
    /// <param name="offered">The ids that were actually put to the judge. An id outside this set voids
    /// the whole reply rather than being skipped: a judge answering about a candidate that does not
    /// exist did not understand the question, and the rest of its answer is not trustworthy either.</param>
    public static IReadOnlyList<int>? ParseAccepted(string? reply, IReadOnlyCollection<int> offered)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;

        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("acceptedCandidateIds", out var ids)) return null;
            if (ids.ValueKind != JsonValueKind.Array) return null;

            var accepted = new List<int>();
            foreach (var item in ids.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number) return null;
                if (!item.TryGetInt32(out var id)) return null;
                if (!offered.Contains(id)) return null;
                if (!accepted.Contains(id)) accepted.Add(id);
            }
            return accepted;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Whether a judge's ruling reaches the user's words.
///
/// There is no "apply without judging" member, deliberately. That state was the defect: a matcher
/// scoring spelling similarity and acting on its own score rewrote 293 ordinary English words out of a
/// 22,000-word corpus against a real glossary. Unlisted corrections are either judged or they do not
/// happen, and the enum is shaped so no configuration can ask for the third thing.
/// </summary>
public enum UnlistedCorrectionMode
{
    /// <summary>Judge, write down what would have changed, change nothing. The default, and how a judge
    /// earns the right to act: on a record of its rulings over real dictation, read first.</summary>
    Shadow = 0,

    /// <summary>Apply accepted rulings, at the offset each was judged at.</summary>
    Enforce = 1,
}
