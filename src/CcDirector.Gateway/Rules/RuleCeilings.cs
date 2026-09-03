namespace CcDirector.Gateway.Rules;

/// <summary>
/// THE BOUNDS ON THE TWO CEILINGS (fix round D, ruling D6).
///
/// The store used to refuse only a ceiling at or below zero, so a daily cap of 2,147,483,647 and a
/// one-second cooldown both passed the safety gate. "Mathematically finite" is not a safety bound. These
/// numbers are the ARCHITECT'S, chosen so that a live rule cannot type more than a hundred times a day
/// into one session, and they are deliberately generous next to the design's own examples (ten minutes
/// apart and five a day; fifteen minutes and six). THE OWNER CAN WIDEN THEM - they are recorded as the
/// Architect's decision and not presented as his.
/// </summary>
public static class RuleCeilings
{
    /// <summary>A rule may not act on the same session again sooner than this.</summary>
    public const int MinCooldownSeconds = 60;

    /// <summary>Nor may it be told to wait longer than a day, which would make "wait" mean "never".</summary>
    public const int MaxCooldownSeconds = 24 * 60 * 60;

    /// <summary>A rule that may act zero times a day is not a rule.</summary>
    public const int MinDailyCap = 1;

    /// <summary>The most times a live rule may act on one session in a day.</summary>
    public const int MaxDailyCap = 100;

    /// <summary>The bounds in the words the question to the model and the refusals use.</summary>
    public const string CooldownStated = "at least 60 seconds and at most 24 hours";

    /// <summary>The bounds in the words the question to the model and the refusals use.</summary>
    public const string DailyCapStated = "at least 1 and at most 100";

    /// <summary>Why a cooldown is outside the bounds, naming the value and the bound, or null.</summary>
    public static string? WhyCooldownIsOut(int cooldownSeconds)
    {
        if (cooldownSeconds <= 0)
            return "a rule has to say how long to wait before acting on the same session again. " +
                   "The ceiling is what makes a rule in a loop finite.";
        if (cooldownSeconds < MinCooldownSeconds || cooldownSeconds > MaxCooldownSeconds)
            return $"a cooldown of {cooldownSeconds} seconds is outside the bounds: it has to be " +
                   CooldownStated + ".";
        return null;
    }

    /// <summary>Why a daily cap is outside the bounds, naming the value and the bound, or null.</summary>
    public static string? WhyDailyCapIsOut(int dailyCap)
    {
        if (dailyCap <= 0)
            return "a rule has to say how many times a day it may act on one session. " +
                   "The ceiling is what makes a rule in a loop finite.";
        if (dailyCap < MinDailyCap || dailyCap > MaxDailyCap)
            return $"a daily cap of {dailyCap} is outside the bounds: it has to be " + DailyCapStated + ".";
        return null;
    }
}
