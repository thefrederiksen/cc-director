using CcDirector.Core.Dictation;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// Whether this deployment lets the dictation judge's rulings reach the user's words yet.
///
/// It is deliberately NOT a per-user glossary setting. The glossary already carries the user's wish
/// (<c>fuzzy_correction_enabled</c> - "I want unlisted corrections"); this is the operator's separate
/// answer to "is the judge trusted to act yet", and the two are different questions. A user asking for
/// corrections should not be the thing that promotes an unproven judge into production.
///
/// It defaults to <see cref="UnlistedCorrectionMode.Enforce"/>, and that default was EARNED rather
/// than assumed. The judge model was measured through the live route on 2026-08-18 against 21 cases:
/// the twelve sentences this feature is known to have corrupted, five real mishearings, two mixed
/// sentences containing one of each, and two non-English. It rejected all twelve corruptions and made
/// ZERO false accepts; its four failures were refusals to correct, never a corruption. The models that
/// did NOT earn it were rejected on the same evidence - one accepted every candidate put to it.
///
/// Shadow is still one environment variable away and needs no new build, which is the point: if the
/// judge starts making bad calls, it can be demoted without shipping anything. Set the variable to
/// exactly <c>shadow</c>.
///
/// Note what this switch does NOT control. An unlisted word is changed only on an affirmative ruling
/// in either mode - no judge, no ruling, a malformed ruling, or one past the deadline all mean the
/// user keeps the words they said. This only decides whether an ACCEPTED ruling reaches the text.
/// </summary>
public static class DictationJudgeMode
{
    /// <summary>Environment variable that DEMOTES the judge to shadow. Only the exact value
    /// <c>shadow</c> demotes; anything else - unset, empty, misspelled, differently-cased or padded -
    /// leaves it enforcing. Exact means exact, in both directions: a garbled value must not silently
    /// change what the product does.</summary>
    public const string EnvVar = "DEVTHROTTLE_DICTATION_JUDGE_MODE";

    /// <summary>The mode this process runs in. Read per call so the setting can be changed without a
    /// restart; it is one environment lookup on a path that already does a model round trip.</summary>
    public static UnlistedCorrectionMode Current => Parse(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>Exposed for testing without touching process environment.</summary>
    internal static UnlistedCorrectionMode Parse(string? raw)
    {
        var shadow = string.Equals(raw, "shadow", StringComparison.Ordinal);
        if (shadow)
            FileLog.Write($"[DictationJudgeMode] {EnvVar}=shadow - judged corrections are RECORDED, not applied");
        return shadow ? UnlistedCorrectionMode.Shadow : UnlistedCorrectionMode.Enforce;
    }
}
