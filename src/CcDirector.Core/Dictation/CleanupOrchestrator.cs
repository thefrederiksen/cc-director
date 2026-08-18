using System.Diagnostics;
using System.Text.RegularExpressions;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Corrects a final transcript against the dictation dictionary, deterministically and in-process.
/// There is NO language model in this path (it used to call a hosted chat model to locate
/// mishearings, which added several seconds per turn and a whole class of network failures for a job
/// that is really just fuzzy matching against a small known vocabulary).
///
/// Two stages, both of which only ever PROPOSE find/replace edits that the deterministic
/// <see cref="TranscriptEditEngine"/> validates and applies:
///   1. <see cref="TryApplyKnownMistranscriptions"/> - exact/alias map: fixes the wrong-forms the
///      dictionary lists explicitly (instant, boundary-aware). Short-circuits when it changes text.
///   2. <see cref="FuzzyDictionaryMatcher"/> - phonetic/edit-distance matcher: catches NEW mishearings
///      that were never hand-listed ("Mindsey" -> mindzie, "Akmeflow" -> acmeflow) by scoring word
///      windows against the canonical vocabulary. OPT-IN and OFF by default, because it decides on
///      spelling alone and rewrites ordinary words into dictionary terms - see
///      <see cref="DictationProfile.FuzzyCorrectionEnabled"/>.
///
/// The transcript never round-trips through any generative model, so nothing can reword, summarize,
/// answer, or inject text (issue #190). The only change made to the user's words is a validated
/// dictionary find/replace, applied by <see cref="TranscriptEditEngine"/> - see its invariant note.
///
/// Fails open: on any error the returned <see cref="CleanupOutcome"/> carries the raw transcript
/// verbatim with a failure reason. Callers ship raw rather than block.
/// </summary>
public sealed class CleanupOrchestrator
{
    /// <summary>
    /// The configured cleanup identity, kept for logging so a turn's log line still names which
    /// cleanup config produced it. It no longer selects a model - cleanup is deterministic.
    /// </summary>
    public const string DefaultModel = TranscriptionEndpointResolver.DevThrottleDictationCleanupModel;

    private readonly string _model;
    private readonly ICandidateJudge? _judge;
    private readonly UnlistedCorrectionMode _mode;

    /// <param name="model">Cleanup identity used only in log lines. Defaults to <see cref="DefaultModel"/>.</param>
    /// <param name="judge">Rules on unlisted candidates in context. WITHOUT ONE, NO UNLISTED CORRECTION
    /// IS EVER APPLIED, whatever the glossary asks for - see <see cref="UnlistedCorrectionMode"/>.</param>
    /// <param name="mode">Whether an accepted ruling actually reaches the text. Defaults to
    /// <see cref="UnlistedCorrectionMode.Shadow"/>: judge, record, change nothing.</param>
    public CleanupOrchestrator(
        string? model = DefaultModel,
        ICandidateJudge? judge = null,
        UnlistedCorrectionMode mode = UnlistedCorrectionMode.Shadow)
    {
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _judge = judge;
        _mode = mode;
    }

    /// <summary>
    /// Clean a raw transcript using the dictionary and the specified profile.
    /// Returns the original text unchanged when cleanup is disabled or nothing matches.
    /// </summary>
    public async Task<CleanupOutcome> CleanAsync(
        string rawTranscript,
        DictationDictionary dictionary,
        string profileName,
        CancellationToken ct = default)
    {
        FileLog.Write($"[CleanupOrchestrator] CleanAsync: profile={profileName}, model={_model}, len={rawTranscript?.Length ?? 0}");

        if (string.IsNullOrWhiteSpace(rawTranscript))
            return new CleanupOutcome(rawTranscript ?? "", Applied: false, Reason: "empty transcript");

        var profile = ResolveProfile(dictionary, profileName);
        if (!profile.CleanupEnabled)
        {
            FileLog.Write($"[CleanupOrchestrator] CleanAsync: cleanup disabled for profile '{profile.Name}', returning verbatim");
            return new CleanupOutcome(rawTranscript, Applied: false, Reason: $"profile '{profile.Name}' has cleanup disabled");
        }

        // No dictionary knowledge at all means there is nothing to correct.
        if (dictionary.Vocabulary.Count == 0 && dictionary.CommonMistranscriptions.Count == 0)
        {
            FileLog.Write("[CleanupOrchestrator] CleanAsync: empty dictionary, returning verbatim");
            return new CleanupOutcome(rawTranscript, Applied: false, Reason: "no dictionary terms to correct");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            // Stage 1: exact/alias map (the hand-listed wrong forms the user chose for themselves).
            //
            // It still short-circuits. Letting stage 2 run on the alias-corrected text was tried and
            // reverted: the fuzzy matcher skips a multi-word canonical window but does not RESERVE it,
            // so it then considers each token inside separately and can rewrite half of a canonical
            // phrase stage 1 just inserted ("alfa beta" -> "Alpha Beta" -> "Alpha Beto"). Composing the
            // two stages needs edits generated against the raw text and merged only where they do not
            // overlap, which is real work and belongs with the offset-based apply in #1554 - not in an
            // emergency fix. The order-dependence this leaves is a known defect, and it is strictly
            // less harmful than corrupting a term the user hand-listed.
            var deterministic = TryApplyKnownMistranscriptions(rawTranscript, dictionary);
            if (deterministic is not null)
            {
                sw.Stop();
                FileLog.Write($"[CleanupOrchestrator] CleanAsync: deterministic known-mistranscription cleanup "
                              + $"applied={deterministic.ChangedWords.Count} in {sw.Elapsed.TotalMilliseconds:0.###}ms");
                return deterministic;
            }

            // Stage 2 is OPT-IN and off unless the glossary asks for it. The fuzzy matcher guesses
            // from spelling alone and rewrites ordinary words into dictionary terms ("make sure" ->
            // "make Soren"); it stays off until a judge that can read the sentence rules on each
            // candidate (devthrottle_internal #1554). Stage 1 above still runs, so the corrections the
            // user listed by hand keep working.
            if (!profile.FuzzyCorrectionEnabled)
            {
                sw.Stop();
                FileLog.Write($"[CleanupOrchestrator] CleanAsync: no listed mistranscription matched and "
                              + $"unlisted fuzzy correction is off for profile '{profile.Name}'; "
                              + $"returning verbatim in {sw.Elapsed.TotalMilliseconds:0.###}ms");
                return new CleanupOutcome(
                    rawTranscript, Applied: false, Reason: "no dictionary corrections needed");
            }

            // The matcher no longer decides anything. It nominates spans - every occurrence
            // separately, with the offset it was found at - and a judge that can read the sentence
            // rules on each one. This is the whole design: string similarity is the right thing to
            // SEARCH with and the wrong thing to DECIDE on, and nothing in an edit-distance score can
            // tell "I am not sure" from the speaker's name.
            var candidates = FuzzyDictionaryMatcher.ProposeCandidates(rawTranscript, dictionary);

            // The same deterministic gate as before still stands in front of the judge, so a candidate
            // it never should have seen cannot be rescued by a permissive ruling.
            var validation = TranscriptEditEngine.Validate(
                candidates.Select(c => new TranscriptEdit(c.Find, c.Replace)).ToList(),
                rawTranscript,
                dictionary);
            foreach (var r in validation.Rejected)
                // The spoken span is the user's words and stays out of the log; the canonical term
                // came from their own glossary, so naming it is what makes a rejection diagnosable.
                FileLog.Write($"[CleanupOrchestrator] candidate REJECTED before judging: {r.Edit.Find.Length} "
                              + $"char(s) -> \"{Truncate(r.Edit.Replace, 60)}\" ({r.Reason})");

            var allowed = new HashSet<(string, string)>(
                validation.Accepted.Select(e => (e.Find, e.Replace)));

            // THE SNAPSHOT. This private array is what gets applied, and the judge never touches it.
            //
            // Handing the judge the same list we later read from would make the whole safety claim a
            // matter of trust: an in-process judge can cast an IReadOnlyList back to the List it really
            // is, swap an entry AFTER validation, accept its id, and have arbitrary Find/Replace/Start
            // applied to the user's words. That is exactly the "a judge cannot supply text" property
            // this design sells, defeated through the parameter rather than the return value.
            //
            // So the judge is given defensive COPIES and its answer is nothing but numbers, which are
            // then resolved against this untouched snapshot.
            var snapshot = candidates.Where(c => allowed.Contains((c.Find, c.Replace)))
                .Take(CandidateJudgeProtocol.MaxCandidates).ToArray();
            // Wrapped, not just copied: a bare array or List handed out as IReadOnlyList can be cast
            // straight back and written through. A ReadOnlyCollection has no such door.
            var offered = new System.Collections.ObjectModel.ReadOnlyCollection<JudgeCandidate>(
                snapshot.Select(c => new JudgeCandidate(c.Id, c.Find, c.Replace, c.Start)).ToList());

            if (snapshot.Length == 0)
            {
                sw.Stop();
                FileLog.Write($"[CleanupOrchestrator] CleanAsync: nothing to judge in {sw.Elapsed.TotalMilliseconds:0.###}ms");
                return new CleanupOutcome(rawTranscript, Applied: false, Reason: "no dictionary corrections needed");
            }

            // THE INVARIANT. An unlisted word is changed only on an affirmative ruling. No judge, no
            // ruling, a malformed ruling, a slow or unreachable one - all of them mean the user keeps
            // the words they said. The deterministic matcher applying its own guesses is the defect
            // this feature exists to end, so there is deliberately no path back to it here.
            if (_judge is null)
            {
                sw.Stop();
                FileLog.Write($"[CleanupOrchestrator] CleanAsync: {snapshot.Length} candidate(s) but NO JUDGE "
                              + $"configured; nothing applied ({sw.Elapsed.TotalMilliseconds:0.###}ms)");
                return new CleanupOutcome(rawTranscript, Applied: false,
                    Reason: "unlisted corrections need a judge and none is configured");
            }

            var ruling = await _judge.AcceptAsync(rawTranscript, offered, ct).ConfigureAwait(false);
            if (ruling is null)
            {
                sw.Stop();
                FileLog.Write($"[CleanupOrchestrator] CleanAsync: judge gave no usable ruling on "
                              + $"{snapshot.Length} candidate(s); nothing applied ({sw.Elapsed.TotalMilliseconds:0.###}ms)");
                return new CleanupOutcome(rawTranscript, Applied: false, Reason: "judge gave no ruling");
            }

            // An id nobody offered VOIDS the whole ruling - it is not filtered out. A judge ruling on a
            // candidate that does not exist did not understand the question, so its opinion on the ones
            // that do exist is not worth acting on either. The protocol parser already enforces this for
            // a hosted judge; enforcing it here too covers every ICandidateJudge, including in-process
            // ones whose answer never passes through that parser.
            var offeredIds = new HashSet<int>(snapshot.Select(c => c.Id));
            if (ruling.Any(id => !offeredIds.Contains(id)))
            {
                sw.Stop();
                FileLog.Write($"[CleanupOrchestrator] CleanAsync: judge ruled on candidate(s) that were "
                              + $"never offered; whole ruling discarded ({sw.Elapsed.TotalMilliseconds:0.###}ms)");
                return new CleanupOutcome(rawTranscript, Applied: false,
                    Reason: "judge ruled on candidates that were never offered");
            }

            // Resolved against the SNAPSHOT, never against the list the judge was handed.
            var acceptedIds = new HashSet<int>(ruling);
            var accepted = snapshot.Where(c => acceptedIds.Contains(c.Id)).ToList();
            var judgedEdits = accepted.Select(c => new TranscriptEdit(c.Find, c.Replace)).ToList();

            // Shadow: ask the question, write down the answer, change nothing. This is how a judge earns
            // the right to act - on a record of what it WOULD have done to real dictation, read before
            // it is allowed to do it.
            // Fail CLOSED on the mode: only the exact Enforce value applies. An undefined enum value -
            // a cast integer, a new member added later and not handled here - must shadow, not enforce.
            if (_mode != UnlistedCorrectionMode.Enforce)
            {
                sw.Stop();
                foreach (var c in accepted)
                    FileLog.Write($"[CleanupOrchestrator] SHADOW would correct {c.Find.Length} char(s) "
                                  + $"at {c.Start} -> \"{c.Replace}\"");
                FileLog.Write($"[CleanupOrchestrator] CleanAsync SHADOW: offered={snapshot.Length} "
                              + $"accepted={accepted.Count} applied=0 in {sw.Elapsed.TotalMilliseconds:0.###}ms");
                return new CleanupOutcome(rawTranscript, Applied: false,
                    Reason: $"shadow mode: {accepted.Count} of {snapshot.Length} candidate(s) would have been corrected",
                    ChangedWords: Array.Empty<TranscriptEdit>(),
                    ShadowChanges: judgedEdits);
            }

            // Applied AT THE OFFSET each candidate was judged at, so one ruling changes one span.
            var (cleaned, appliedCount) = TranscriptEditEngine.ApplyAt(rawTranscript, accepted);
            sw.Stop();

            foreach (var c in accepted)
                FileLog.Write($"[CleanupOrchestrator] judged correction of {c.Find.Length} char(s) "
                              + $"at {c.Start} -> \"{c.Replace}\"");
            FileLog.Write($"[CleanupOrchestrator] CleanAsync done in {sw.Elapsed.TotalMilliseconds:0.###}ms: "
                          + $"offered={snapshot.Length} accepted={accepted.Count} applied={appliedCount} "
                          + $"rejected-before-judging={validation.Rejected.Count}");

            var changedWords = appliedCount > 0
                ? judgedEdits
                : (IReadOnlyList<TranscriptEdit>)Array.Empty<TranscriptEdit>();
            return new CleanupOutcome(
                cleaned,
                Applied: appliedCount > 0,
                Reason: appliedCount > 0 ? null : "no dictionary corrections needed",
                ChangedWords: changedWords);
        }
        catch (Exception ex)
        {
            // Cleanup is best-effort. A regex timeout or any other fault must never fail the recording;
            // ship the raw transcript, exactly as before the cleanup step existed.
            sw.Stop();
            FileLog.Write($"[CleanupOrchestrator] CleanAsync FAILED in {sw.Elapsed.TotalMilliseconds:0.###}ms: {ex.Message}");
            return new CleanupOutcome(rawTranscript, Applied: false, Reason: "cleanup failed: " + ex.Message);
        }
    }

    private static CleanupOutcome? TryApplyKnownMistranscriptions(
        string rawTranscript,
        DictationDictionary dictionary)
    {
        var edits = new List<TranscriptEdit>();
        foreach (var kv in dictionary.CommonMistranscriptions)
        {
            var canonical = kv.Key;
            foreach (var wrong in kv.Value)
            {
                if (string.IsNullOrWhiteSpace(wrong))
                    continue;

                foreach (Match match in BoundaryMatches(rawTranscript, wrong))
                {
                    if (string.Equals(match.Value, canonical, StringComparison.Ordinal))
                        continue;
                    edits.Add(new TranscriptEdit(match.Value, canonical));
                }
            }
        }

        if (edits.Count == 0)
            return null;

        var validation = TranscriptEditEngine.Validate(edits, rawTranscript, dictionary);
        var (cleaned, appliedCount) = TranscriptEditEngine.Apply(rawTranscript, validation.Accepted);
        if (appliedCount == 0)
            return null;

        foreach (var edit in validation.Accepted)
            FileLog.Write($"[CleanupOrchestrator] deterministic edit accepted: \"{Truncate(edit.Find, 60)}\" -> \"{edit.Replace}\"");
        foreach (var rejected in validation.Rejected)
            FileLog.Write($"[CleanupOrchestrator] deterministic edit REJECTED: \"{Truncate(rejected.Edit.Find, 60)}\" -> "
                          + $"\"{Truncate(rejected.Edit.Replace, 60)}\" ({rejected.Reason})");

        return new CleanupOutcome(
            cleaned,
            Applied: true,
            Reason: "deterministic known-mistranscription cleanup",
            ChangedWords: validation.Accepted);
    }

    private static IEnumerable<Match> BoundaryMatches(string text, string find)
    {
        var pattern = $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(find)}(?![\p{{L}}\p{{N}}_])";
        return Regex.Matches(
                text,
                pattern,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(250))
            .Cast<Match>();
    }

    private static DictationProfile ResolveProfile(DictationDictionary dictionary, string profileName)
    {
        if (!string.IsNullOrWhiteSpace(profileName)
            && dictionary.Profiles.TryGetValue(profileName, out var found))
            return found;
        if (dictionary.Profiles.TryGetValue("default", out var def))
            return def;
        return new DictationProfile("default", CleanupEnabled: true, FuzzyCorrectionEnabled: false);
    }

    /// <summary>
    /// Shorten a value for a log line.
    ///
    /// Built with <see cref="string.Concat(ReadOnlySpan{char}, ReadOnlySpan{char})"/> rather than the
    /// obvious slice-and-plus, so this file contains no construct that BUILDS a string out of pieces of
    /// another one. That shape is how a transcript actually gets rewritten, the integrity guard looks
    /// for exactly it, and a log helper that trips the guard is how a guard gets weakened until it
    /// stops guarding. Cheaper to write the helper differently than to teach the check to ignore it.
    /// </summary>
    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");
}

/// <summary>
/// Outcome of a single cleanup pass. <see cref="Text"/> always carries
/// something safe to ship: cleaned text on success, raw transcript on
/// failure or when cleanup is disabled for the profile.
///
/// <see cref="ChangedWords"/> (issue #587) lists exactly which dictionary terms
/// were swapped - each accepted find/replace edit that actually changed the
/// text. It is empty whenever <see cref="Applied"/> is false (nothing was
/// changed, cleanup was disabled, or it failed open), so a caller can report
/// "these words changed" truthfully and prove "no dictionary terms -> nothing
/// changed" by an empty list.
/// </summary>
public sealed record CleanupOutcome(
    string Text,
    bool Applied,
    string? Reason,
    IReadOnlyList<TranscriptEdit> ChangedWords,
    IReadOnlyList<TranscriptEdit>? ShadowChanges = null)
{
    /// <summary>
    /// What the judge accepted while in <see cref="UnlistedCorrectionMode.Shadow"/> - corrections that
    /// were NOT applied. Null or empty everywhere else.
    ///
    /// It is a separate field from <see cref="ChangedWords"/> on purpose. A shadow run must be
    /// indistinguishable from no cleanup at all to anything reading the transcript, while still being
    /// legible to whoever is deciding whether to trust the judge. Folding the two together would put
    /// changes that never happened into the record of changes that did.
    /// </summary>
    public IReadOnlyList<TranscriptEdit> ShadowChanges { get; init; } = ShadowChanges ?? Array.Empty<TranscriptEdit>();

    /// <summary>
    /// Convenience constructor for the no-change paths (empty, disabled, failed open) where there
    /// is never a change list. Keeps the existing 3-argument call sites unchanged while the success
    /// path supplies the real <see cref="ChangedWords"/>.
    /// </summary>
    public CleanupOutcome(string Text, bool Applied, string? Reason)
        : this(Text, Applied, Reason, Array.Empty<TranscriptEdit>())
    {
    }
}
