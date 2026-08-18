using CcDirector.Core.Dictation;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Judges that behave badly on purpose. The point of the judge design is that the transcript survives
/// whatever the backend does, so every failure a real one can produce - silence, nonsense, a stall, a
/// crash, an answer about a candidate that was never offered - needs a stand-in here.
/// </summary>
internal static class Judges
{
    /// <summary>Accepts everything. Used to prove the matcher still finds what it used to find, so a
    /// test that goes quiet is failing because the judge refused, not because nothing was proposed.</summary>
    public static ICandidateJudge AcceptAll { get; } = new Lambda(c => c.Select(x => x.Id).ToList());

    /// <summary>Refuses everything - the conservative real-world answer.</summary>
    public static ICandidateJudge RejectAll { get; } = new Lambda(_ => new List<int>());

    /// <summary>Returns no ruling at all: unreachable, timed out, or unparseable output.</summary>
    public static ICandidateJudge NoRuling { get; } = new Lambda(_ => null);

    /// <summary>Accepts only candidates whose spoken text is in <paramref name="finds"/>.</summary>
    public static ICandidateJudge Accepting(params string[] finds)
        => new Lambda(c => c.Where(x => finds.Contains(x.Find, StringComparer.Ordinal))
                            .Select(x => x.Id).ToList());

    /// <summary>Answers about ids that were never offered - a judge that did not understand the
    /// question. Its whole answer must be discarded, not filtered.</summary>
    public static ICandidateJudge InventingIds { get; } = new Lambda(c => new List<int> { 9_999 });

    /// <summary>Throws. A backend fault must never reach the user's transcript.</summary>
    public static ICandidateJudge Throwing { get; } = new Lambda(_ => throw new InvalidOperationException("judge exploded"));

    /// <summary>Records what it was asked, so a test can assert the judge was never called at all.</summary>
    public sealed class Recording : ICandidateJudge
    {
        public int Calls { get; private set; }
        public string? LastUtterance { get; private set; }
        public IReadOnlyList<JudgeCandidate> LastCandidates { get; private set; } = Array.Empty<JudgeCandidate>();

        public Task<IReadOnlyList<int>?> AcceptAsync(
            string utterance, IReadOnlyList<JudgeCandidate> candidates, CancellationToken ct = default)
        {
            Calls++;
            LastUtterance = utterance;
            LastCandidates = candidates;
            return Task.FromResult<IReadOnlyList<int>?>(candidates.Select(c => c.Id).ToList());
        }
    }

    private sealed class Lambda(Func<IReadOnlyList<JudgeCandidate>, IReadOnlyList<int>?> f) : ICandidateJudge
    {
        public Task<IReadOnlyList<int>?> AcceptAsync(
            string utterance, IReadOnlyList<JudgeCandidate> candidates, CancellationToken ct = default)
            => Task.FromResult(f(candidates));
    }
}
