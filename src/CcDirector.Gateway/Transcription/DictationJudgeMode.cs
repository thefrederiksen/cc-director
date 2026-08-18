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
/// It defaults to <see cref="UnlistedCorrectionMode.Shadow"/>: rule on real dictation and write down
/// what would have changed, while changing nothing. That record is how the judge earns the right to
/// act. Flipping to Enforce is one environment variable and needs no deploy of new code - which is the
/// point, because the decision to flip should rest on shadow evidence rather than on shipping a build.
/// </summary>
public static class DictationJudgeMode
{
    /// <summary>Environment variable that promotes the judge. Anything other than the exact value
    /// <c>enforce</c> (case-insensitive, trimmed) leaves it shadowing - an unset, empty, misspelled or
    /// garbled value must never be read as permission to rewrite someone's words.</summary>
    public const string EnvVar = "DEVTHROTTLE_DICTATION_JUDGE_MODE";

    /// <summary>The mode this process runs in. Read per call so the setting can be changed without a
    /// restart; it is one environment lookup on a path that already does a model round trip.</summary>
    public static UnlistedCorrectionMode Current => Parse(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>Exposed for testing without touching process environment.</summary>
    internal static UnlistedCorrectionMode Parse(string? raw)
    {
        var enforce = string.Equals(raw?.Trim(), "enforce", StringComparison.OrdinalIgnoreCase);
        if (enforce)
            FileLog.Write($"[DictationJudgeMode] {EnvVar}=enforce - judged corrections WILL be applied");
        return enforce ? UnlistedCorrectionMode.Enforce : UnlistedCorrectionMode.Shadow;
    }
}
