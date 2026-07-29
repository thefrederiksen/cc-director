using CcDirector.Gateway.Speech;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The runaway guard on text handed to speech synthesis - and, when it fires, the sentence that
/// TELLS the listener their narration was cut (issue #1612).
///
/// Two things this is NOT:
///
/// 1. It is NOT the length limit for a narration. That belongs where the text is PRODUCED - the
///    wingman's own instructions (<see cref="WingmanTranslator.FidelityPrompt"/>), which now carry a
///    spoken-length budget of about thirty seconds. A summary is short because we asked for a
///    summary, not because we cut an essay in half. This is only the backstop for when that fails.
///
/// 2. It is NOT a provider limit. There is no such limit. The old cap was 4000 characters, inherited
///    from OpenAI's <c>/v1/audio/speech</c> (which genuinely caps at 4096) by commit 265af32b on
///    2026-06-19 - correct for a provider we then stopped using. The provider was swapped and the cap
///    rode along unexamined, so for months narration was silently cut to satisfy a limit belonging to
///    a company we no longer call. It happened 24 times in one day: of 873 narrations, the worst lost
///    969 characters - about 55 seconds of speech - dropped mid-word with the user told nothing. It is
///    the longest, most complex turns that get cut: exactly the ones whose ending matters most. Our
///    own metered API has no cap at all and is a correct pass-through; the cut was entirely ours.
///
/// THE CUT IS THE LIE, NOT THE LIMIT. A guard is fine. Cutting someone off mid-word and saying
/// nothing is not. If this fires, the listener hears that they are missing something and can go read
/// the reply - which is a recoverable situation. Silence is not.
/// </summary>
internal static class NarrationText
{
    /// <summary>
    /// The runaway ceiling: the longest input we have MEASURED working end to end (2026-07-15,
    /// production key: 12,000 chars -> 200 OK, 4.8 MB mp3, 21.1 s). It is deliberately the largest
    /// measured-good value rather than a number someone liked: beyond it we would be guessing, and
    /// guessing is what put a 4000 here for a year.
    ///
    /// It is ~22x a normal narration (~550 chars) and 2.4x the worst ever observed (4,969), so in
    /// normal operation it never fires. It exists so a runaway wingman cannot bill us for a
    /// four-minute synthesis, and it is what bounds <see cref="HostedAi.TtsSynthesis.DeadlineFor"/> -
    /// the cap and the deadline are ONE decision.
    /// </summary>
    public const int MaxChars = 12_000;

    // What the listener hears instead of being cut off mid-word in silence lives with every other
    // fixed spoken sentence, in SpokenPhrases.NarrationCutNotice (issue #1009) - it is SPOKEN, so it is
    // spoken in the account's language. Telling someone in English that their French summary was cut is
    // the exact "English fragment in a French session" the owner ruled out.

    /// <summary>
    /// Bound <paramref name="text"/> for synthesis. Returns the text unchanged when it is within
    /// <see cref="MaxChars"/>. Otherwise it cuts at the last sentence end (falling back to a word
    /// boundary, never mid-word) and appends <see cref="SpokenPhrases.NarrationCutNotice"/>, in the
    /// account's language, so the cut is AUDIBLE.
    /// </summary>
    /// <param name="wasCut">True when the text was shortened - the caller should say so in its log,
    /// because a cut is a defect worth seeing, not routine.</param>
    public static string LimitForSpeech(string? text, SpokenLanguage language, out bool wasCut)
    {
        ArgumentNullException.ThrowIfNull(language);
        wasCut = false;
        if (string.IsNullOrEmpty(text) || text.Length <= MaxChars) return text ?? "";

        wasCut = true;
        var cutNotice = SpokenPhrases.NarrationCutNotice.In(language);
        // Leave room for the notice so the result stays near the ceiling the deadline is derived from.
        var budget = MaxChars - cutNotice.Length;
        var head = text[..budget];

        // Prefer the last sentence end, so the listener hears a whole thought before being told the
        // rest is missing. Only accept one in the last third, otherwise a text with no punctuation
        // for pages would be cut absurdly short.
        var lastStop = head.LastIndexOfAny(new[] { '.', '!', '?' });
        if (lastStop >= budget * 2 / 3)
            return head[..(lastStop + 1)] + cutNotice;

        // No usable sentence end: fall back to the last word boundary. Never cut mid-word - that is
        // the old behaviour, and it is what made the truncation sound like a crash.
        var lastSpace = head.LastIndexOf(' ');
        if (lastSpace >= budget * 2 / 3)
            return head[..lastSpace].TrimEnd() + cutNotice;

        return head.TrimEnd() + cutNotice;
    }
}
