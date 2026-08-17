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
///      dictionary lists explicitly (instant, boundary-aware). Always runs when cleanup is enabled.
///   2. <see cref="FuzzyDictionaryMatcher"/> - phonetic/edit-distance matcher: catches NEW mishearings
///      that were never hand-listed ("Mindsey" -> mindzie, "Akmeflow" -> acmeflow) by scoring word
///      windows against the canonical vocabulary. OPT-IN and OFF by default, because it decides on
///      spelling alone and rewrites ordinary words into dictionary terms - see
///      <see cref="DictationProfile.FuzzyCorrectionEnabled"/>.
///
/// The two stages COMPOSE: stage 1 no longer returns early, so stage 2 (when enabled) works on the
/// alias-corrected text. Whether an unrelated alias fired must not change what else gets corrected.
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

    /// <param name="model">Cleanup identity used only in log lines. Defaults to <see cref="DefaultModel"/>.</param>
    public CleanupOrchestrator(string? model = DefaultModel)
    {
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
    }

    /// <summary>
    /// Clean a raw transcript using the dictionary and the specified profile.
    /// Returns the original text unchanged when cleanup is disabled or nothing matches.
    /// </summary>
    public Task<CleanupOutcome> CleanAsync(
        string rawTranscript,
        DictationDictionary dictionary,
        string profileName,
        CancellationToken ct = default)
    {
        FileLog.Write($"[CleanupOrchestrator] CleanAsync: profile={profileName}, model={_model}, len={rawTranscript?.Length ?? 0}");

        if (string.IsNullOrWhiteSpace(rawTranscript))
            return Done(new CleanupOutcome(rawTranscript ?? "", Applied: false, Reason: "empty transcript"));

        var profile = ResolveProfile(dictionary, profileName);
        if (!profile.CleanupEnabled)
        {
            FileLog.Write($"[CleanupOrchestrator] CleanAsync: cleanup disabled for profile '{profile.Name}', returning verbatim");
            return Done(new CleanupOutcome(rawTranscript, Applied: false, Reason: $"profile '{profile.Name}' has cleanup disabled"));
        }

        // No dictionary knowledge at all means there is nothing to correct.
        if (dictionary.Vocabulary.Count == 0 && dictionary.CommonMistranscriptions.Count == 0)
        {
            FileLog.Write("[CleanupOrchestrator] CleanAsync: empty dictionary, returning verbatim");
            return Done(new CleanupOutcome(rawTranscript, Applied: false, Reason: "no dictionary terms to correct"));
        }

        var sw = Stopwatch.StartNew();
        try
        {
            // Stage 1: exact/alias map (the hand-listed wrong forms the user chose for themselves).
            // It no longer short-circuits: whether an unrelated alias happened to fire must not decide
            // whether the rest of the pipeline runs, or the same sentence corrects differently
            // depending on what else was said in it.
            var aliasOutcome = TryApplyKnownMistranscriptions(rawTranscript, dictionary);
            var textAfterAliases = aliasOutcome?.Text ?? rawTranscript;
            var aliasEdits = aliasOutcome?.ChangedWords ?? Array.Empty<TranscriptEdit>();

            // Stage 2 is OPT-IN and off unless the glossary asks for it. The fuzzy matcher guesses
            // from spelling alone and rewrites ordinary words into dictionary terms ("make sure" ->
            // "make Soren"); it stays off until a judge that can read the sentence rules on each
            // candidate (devthrottle_internal #1554). Stage 1 still runs above, so the corrections
            // the user listed by hand keep working.
            if (!profile.FuzzyCorrectionEnabled)
            {
                sw.Stop();
                FileLog.Write($"[CleanupOrchestrator] CleanAsync: alias-only cleanup "
                              + $"applied={aliasEdits.Count} in {sw.Elapsed.TotalMilliseconds:0.###}ms "
                              + $"(unlisted fuzzy correction is off for profile '{profile.Name}')");
                return Done(aliasOutcome ?? new CleanupOutcome(
                    rawTranscript, Applied: false, Reason: "no dictionary corrections needed"));
            }

            // The fuzzy matcher proposes edits for the unlisted mishearings; the SAME engine gate
            // validates and applies them, so the safety invariant is identical to before. It runs on
            // the alias-corrected text, not the raw text, so both stages compose.
            var proposed = FuzzyDictionaryMatcher.Propose(textAfterAliases, dictionary);
            var validation = TranscriptEditEngine.Validate(proposed, textAfterAliases, dictionary);
            foreach (var r in validation.Rejected)
                FileLog.Write($"[CleanupOrchestrator] edit REJECTED: \"{Truncate(r.Edit.Find, 60)}\" -> "
                              + $"\"{Truncate(r.Edit.Replace, 60)}\" ({r.Reason})");
            foreach (var a in validation.Accepted)
                FileLog.Write($"[CleanupOrchestrator] edit accepted: \"{Truncate(a.Find, 60)}\" -> \"{a.Replace}\"");

            var (cleaned, appliedCount) = TranscriptEditEngine.Apply(textAfterAliases, validation.Accepted);
            sw.Stop();

            // Both stages may have contributed, so the reason names whichever actually did. Stage 1
            // keeps saying "deterministic ..." exactly as it did when it returned on its own.
            var reasons = new List<string>();
            if (aliasEdits.Count > 0)
                reasons.Add("deterministic known-mistranscription cleanup");
            if (validation.Rejected.Count > 0)
                reasons.Add($"{validation.Rejected.Count} proposed edit(s) rejected");
            if (appliedCount == 0 && aliasEdits.Count == 0)
                reasons.Add("no dictionary corrections needed");
            var reason = reasons.Count > 0 ? string.Join("; ", reasons) : null;

            FileLog.Write($"[CleanupOrchestrator] CleanAsync done in {sw.Elapsed.TotalMilliseconds:0.###}ms: "
                          + $"proposed={proposed.Count} accepted={validation.Accepted.Count} "
                          + $"applied={appliedCount} rejected={validation.Rejected.Count}");

            // Report which dictionary terms were swapped (issue #587): the accepted edits ARE the
            // change list, and only when something actually reached the text. Both stages report,
            // because both stages may now have changed the text.
            var changedWords = appliedCount > 0
                ? aliasEdits.Concat(validation.Accepted).ToList()
                : (IReadOnlyList<TranscriptEdit>)aliasEdits;
            return Done(new CleanupOutcome(
                cleaned,
                Applied: appliedCount > 0 || aliasEdits.Count > 0,
                Reason: reason,
                ChangedWords: changedWords));
        }
        catch (Exception ex)
        {
            // Cleanup is best-effort. A regex timeout or any other fault must never fail the recording;
            // ship the raw transcript, exactly as before the cleanup step existed.
            sw.Stop();
            FileLog.Write($"[CleanupOrchestrator] CleanAsync FAILED in {sw.Elapsed.TotalMilliseconds:0.###}ms: {ex.Message}");
            return Done(new CleanupOutcome(rawTranscript, Applied: false, Reason: "cleanup failed: " + ex.Message));
        }
    }

    private static Task<CleanupOutcome> Done(CleanupOutcome outcome) => Task.FromResult(outcome);

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

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
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
    IReadOnlyList<TranscriptEdit> ChangedWords)
{
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
