using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// The judged-correction path (devthrottle_internal #1554).
///
/// String similarity is the right thing to SEARCH with and the wrong thing to DECIDE on: no
/// edit-distance score in any language separates "I am not sure" from the speaker's name. So the
/// matcher nominates and a judge that reads the sentence rules on each nomination.
///
/// Most of these tests are about what happens when the judge does NOT give a clean answer, because
/// that is the case that decides whether the user keeps the words they actually said.
/// </summary>
public sealed class DictationJudgeTests
{
    private static DictationDictionary Dict(bool fuzzy = true, params string[] vocab)
        => new(
            vocab.Length > 0 ? vocab : new[] { "Soren", "Tailscale", "ConPty" },
            new Dictionary<string, IReadOnlyList<string>> { ["ConPty"] = new[] { "Con-TY" } },
            new Dictionary<string, DictationProfile>
            {
                ["default"] = new("default", CleanupEnabled: true, FuzzyCorrectionEnabled: fuzzy),
            });

    private static Task<CleanupOutcome> Run(
        string raw,
        ICandidateJudge? judge,
        UnlistedCorrectionMode mode = UnlistedCorrectionMode.Enforce,
        DictationDictionary? dict = null)
        => new CleanupOrchestrator(judge: judge, mode: mode)
            .CleanAsync(raw, dict ?? Dict(), "default");

    // ===== the invariant: no affirmative ruling, no change =====================

    [Fact]
    public async Task NoJudgeConfigured_ChangesNothing_EvenWithTheGuessingEnabled()
    {
        var outcome = await Run("we deployed Terascale last night", judge: null);

        Assert.Equal("we deployed Terascale last night", outcome.Text);
        Assert.False(outcome.Applied);
        Assert.Contains("judge", outcome.Reason);
    }

    [Fact]
    public async Task JudgeGivesNoRuling_ChangesNothing()
    {
        var outcome = await Run("we deployed Terascale last night", Judges.NoRuling);

        Assert.Equal("we deployed Terascale last night", outcome.Text);
        Assert.False(outcome.Applied);
        Assert.Empty(outcome.ChangedWords);
    }

    [Fact]
    public async Task JudgeThrows_ChangesNothing_AndTheTurnSurvives()
    {
        var outcome = await Run("we deployed Terascale last night", Judges.Throwing);

        Assert.Equal("we deployed Terascale last night", outcome.Text);
        Assert.False(outcome.Applied);
        Assert.Contains("cleanup failed", outcome.Reason);
    }

    [Fact]
    public async Task JudgeRejectsEverything_ChangesNothing()
    {
        var outcome = await Run("we deployed Terascale last night", Judges.RejectAll);

        Assert.Equal("we deployed Terascale last night", outcome.Text);
        Assert.False(outcome.Applied);
    }

    /// <summary>
    /// A judge answering about a candidate that was never offered did not understand the question, so
    /// the REST of its answer is not trustworthy either. The whole ruling is discarded rather than
    /// filtered down to the ids that happen to exist.
    /// </summary>
    [Fact]
    public async Task JudgeAnsweringAboutCandidatesThatDoNotExist_HasItsWholeRulingDiscarded()
    {
        var outcome = await Run("we deployed Terascale last night", Judges.InventingIds);

        Assert.Equal("we deployed Terascale last night", outcome.Text);
        Assert.False(outcome.Applied);
    }

    // ===== enforce: an accepted ruling reaches exactly one span ================

    [Fact]
    public async Task AnAcceptedRuling_IsApplied()
    {
        var outcome = await Run("we deployed Terascale last night", Judges.AcceptAll);

        Assert.Equal("we deployed Tailscale last night", outcome.Text);
        Assert.True(outcome.Applied);
        Assert.Contains(outcome.ChangedWords, e => e.Replace == "Tailscale");
    }

    /// <summary>
    /// The reason offsets exist. The judge ruled on ONE occurrence in one sentence; the same word
    /// later in the same breath is a different use it never saw. Rewriting both would be the corrector
    /// substituting its own rule for the judge's ruling.
    /// </summary>
    [Fact]
    public async Task OnlyTheJudgedOccurrenceChanges_NotEveryCopyOfTheWord()
    {
        var outcome = await Run("sure and then sure again", new FirstCandidateOnly());

        Assert.Equal("Soren and then sure again", outcome.Text);
    }

    [Fact]
    public async Task SeveralAcceptedRulings_AreAllApplied_WithoutShiftingEachOther()
    {
        var outcome = await Run("Terascale and Terascale and Terascale", Judges.AcceptAll);

        Assert.Equal("Tailscale and Tailscale and Tailscale", outcome.Text);
    }

    // ===== shadow: ask, record, change nothing ================================

    [Fact]
    public async Task ShadowMode_LeavesTheTranscriptCompletelyAlone()
    {
        var outcome = await Run(
            "we deployed Terascale last night", Judges.AcceptAll, UnlistedCorrectionMode.Shadow);

        Assert.Equal("we deployed Terascale last night", outcome.Text);
        Assert.False(outcome.Applied);
        Assert.Empty(outcome.ChangedWords);
    }

    /// <summary>Shadow is only worth running if it records what it would have done - that record is
    /// the evidence the judge is trusted on.</summary>
    [Fact]
    public async Task ShadowMode_RecordsWhatItWouldHaveCorrected()
    {
        var outcome = await Run(
            "we deployed Terascale last night", Judges.AcceptAll, UnlistedCorrectionMode.Shadow);

        Assert.Contains(outcome.ShadowChanges, e => e.Find == "Terascale" && e.Replace == "Tailscale");
        Assert.Contains("shadow", outcome.Reason);
    }

    [Fact]
    public async Task ShadowIsTheDefaultMode()
    {
        var outcome = await new CleanupOrchestrator(judge: Judges.AcceptAll)
            .CleanAsync("we deployed Terascale last night", Dict(), "default");

        Assert.Equal("we deployed Terascale last night", outcome.Text);
        Assert.NotEmpty(outcome.ShadowChanges);
    }

    // ===== when the judge must not be asked at all ============================

    [Fact]
    public async Task TheJudgeIsNotAsked_WhenTheGuessingIsOff()
    {
        var recording = new Judges.Recording();
        await new CleanupOrchestrator(judge: recording, mode: UnlistedCorrectionMode.Enforce)
            .CleanAsync("we deployed Terascale last night", Dict(fuzzy: false), "default");

        Assert.Equal(0, recording.Calls);
    }

    [Fact]
    public async Task TheJudgeIsNotAsked_WhenCleanupIsDisabledEntirely()
    {
        var recording = new Judges.Recording();
        var dict = new DictationDictionary(
            new[] { "Tailscale" },
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, DictationProfile>
            {
                ["default"] = new("default", CleanupEnabled: false, FuzzyCorrectionEnabled: true),
            });

        await new CleanupOrchestrator(judge: recording, mode: UnlistedCorrectionMode.Enforce)
            .CleanAsync("we deployed Terascale last night", dict, "default");

        Assert.Equal(0, recording.Calls);
    }

    /// <summary>No candidates means no question, which means no model call and no cost. Most sentences
    /// contain nothing that resembles a glossary term at all.</summary>
    [Fact]
    public async Task TheJudgeIsNotAsked_WhenNothingResemblesATerm()
    {
        var recording = new Judges.Recording();
        await new CleanupOrchestrator(judge: recording, mode: UnlistedCorrectionMode.Enforce)
            .CleanAsync("please water the plants before you leave", Dict(), "default");

        Assert.Equal(0, recording.Calls);
    }

    /// <summary>A wrong form the user listed by hand is not a guess and needs no ruling.</summary>
    [Fact]
    public async Task ListedWrongForms_AreCorrectedWithoutAskingTheJudge()
    {
        var recording = new Judges.Recording();
        var outcome = await new CleanupOrchestrator(judge: recording, mode: UnlistedCorrectionMode.Enforce)
            .CleanAsync("restart the Con-TY renderer", Dict(), "default");

        Assert.Equal("restart the ConPty renderer", outcome.Text);
        Assert.Equal(0, recording.Calls);
    }

    // ===== what the judge is actually shown ===================================

    [Fact]
    public async Task TheJudgeSeesTheWholeSentence_AndTheSpanWithItsOffset()
    {
        var recording = new Judges.Recording();
        await new CleanupOrchestrator(judge: recording, mode: UnlistedCorrectionMode.Enforce)
            .CleanAsync("we deployed Terascale last night", Dict(), "default");

        Assert.Equal("we deployed Terascale last night", recording.LastUtterance);
        var c = Assert.Single(recording.LastCandidates);
        Assert.Equal("Terascale", c.Find);
        Assert.Equal("Tailscale", c.Replace);
        Assert.Equal("we deployed ".Length, c.Start);
    }

    [Fact]
    public async Task TheCandidateListPutToTheJudgeIsBounded()
    {
        var recording = new Judges.Recording();
        var many = string.Join(" ", Enumerable.Repeat("Terascale", CandidateJudgeProtocol.MaxCandidates + 8));

        await new CleanupOrchestrator(judge: recording, mode: UnlistedCorrectionMode.Enforce)
            .CleanAsync(many, Dict(), "default");

        Assert.InRange(recording.LastCandidates.Count, 1, CandidateJudgeProtocol.MaxCandidates);
    }

    // ===== the judge cannot reach past its own answer =========================

    /// <summary>
    /// The Critical hole a review found, pinned.
    ///
    /// The judge is handed a candidate list and its answer is only ids - but if it were handed the very
    /// list we later read from, an in-process judge could cast the IReadOnlyList back to what it really
    /// is, swap an entry AFTER validation, accept that entry's id, and have arbitrary replacement text
    /// applied. The "a judge cannot supply text" property would be defeated through the PARAMETER
    /// rather than the return value, which the return-type guard cannot see.
    ///
    /// So the ids are resolved against a private snapshot the judge never touches. This judge tries the
    /// attack; the transcript must be untouched.
    /// </summary>
    [Fact]
    public async Task AJudgeThatRewritesItsOwnCandidateList_ChangesNothing()
    {
        var attacker = new MutatingJudge();

        var outcome = await Run("we deployed Terascale last night", attacker);

        Assert.True(attacker.Tried, "the attack never ran - this test would pass for the wrong reason");
        Assert.False(attacker.MutationSucceeded,
            "the judge was able to write through its candidate list, so the snapshot is not protecting anything");
        Assert.DoesNotContain("ARBITRARY", outcome.Text);
        Assert.Equal("we deployed Tailscale last night", outcome.Text);
    }

    /// <summary>
    /// A ruling mixing real ids with invented ones must be VOIDED, not filtered. The earlier double
    /// returned only an invented id, so the filter happened to produce no edits and the test passed
    /// without ever covering the mixed case.
    /// </summary>
    [Fact]
    public async Task ARulingMixingRealAndInventedIds_IsDiscardedEntirely()
    {
        var outcome = await Run("we deployed Terascale last night", new MixedIdsJudge());

        Assert.Equal("we deployed Terascale last night", outcome.Text);
        Assert.False(outcome.Applied);
        Assert.Contains("never offered", outcome.Reason);
    }

    /// <summary>An undefined mode value must shadow. Only the exact Enforce member may change text.</summary>
    [Fact]
    public async Task AnUndefinedModeValue_Shadows()
    {
        var outcome = await new CleanupOrchestrator(
                judge: Judges.AcceptAll, mode: (UnlistedCorrectionMode)99)
            .CleanAsync("we deployed Terascale last night", Dict(), "default");

        Assert.Equal("we deployed Terascale last night", outcome.Text);
        Assert.False(outcome.Applied);
    }

    /// <summary>Tries to swap a validated candidate for one carrying arbitrary replacement text, and
    /// records whether the collection let it.</summary>
    private sealed class MutatingJudge : ICandidateJudge
    {
        public bool Tried { get; private set; }
        public bool MutationSucceeded { get; private set; }

        public Task<IReadOnlyList<int>?> AcceptAsync(
            string utterance, IReadOnlyList<JudgeCandidate> candidates, CancellationToken ct = default)
        {
            Tried = true;
            var poison = new JudgeCandidate(candidates[0].Id, "we", "ARBITRARY TEXT", 0);

            try
            {
                if (candidates is JudgeCandidate[] array) array[0] = poison;
                else if (candidates is IList<JudgeCandidate> list) list[0] = poison;
            }
            catch (NotSupportedException)
            {
                // A genuinely read-only collection refuses the write. That is the property.
            }

            MutationSucceeded = !ReferenceEquals(candidates[0], poison)
                ? candidates[0].Replace == "ARBITRARY TEXT"
                : true;

            return Task.FromResult<IReadOnlyList<int>?>(candidates.Select(c => c.Id).ToList());
        }
    }

    /// <summary>Returns one real id and one that was never offered.</summary>
    private sealed class MixedIdsJudge : ICandidateJudge
    {
        public Task<IReadOnlyList<int>?> AcceptAsync(
            string utterance, IReadOnlyList<JudgeCandidate> candidates, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<int>?>(
                candidates.Select(c => c.Id).Concat(new[] { 9_999 }).ToList());
    }

    /// <summary>Accepts only the first candidate offered, by id.</summary>
    private sealed class FirstCandidateOnly : ICandidateJudge
    {
        public Task<IReadOnlyList<int>?> AcceptAsync(
            string utterance, IReadOnlyList<JudgeCandidate> candidates, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<int>?>(candidates.Take(1).Select(c => c.Id).ToList());
    }
}
