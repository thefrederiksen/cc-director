using CcDirector.Gateway.CarMode;
using CcDirector.Gateway.Wingman;

namespace CcDirector.Gateway.Speech;

/// <summary>
/// THE LIST OF EVERY PLACE THIS PRODUCT ASKS A MODEL FOR WORDS THAT WILL BE SPOKEN (issue #1008).
///
/// This exists because the last multilingual attempt was pulled over a counting error. There were four
/// model-driven spoken paths and the language reached ONE of them, so an account set to another language
/// had its narration translated and was answered in English the moment it spoke back. Nobody could see
/// the miss, because nowhere in the codebase was there a list of the four to check against - each one
/// lived in its own file with its own prompt, and "all of them" was something a person had to hold in
/// their head.
///
/// So the list is here, in production code rather than in a test, and it is enforced from both ends:
///   - <c>SpokenPathContractTests</c> renders every path below in every language and fails if one does
///     not carry <see cref="SpeechContract.SpokenOutputContract"/> - a path that ignores the language
///     goes red.
///   - <c>SpokenPathRegistryCompletenessTests</c> scans the Gateway source for spoken-prompt builders
///     and fails if one exists that is not listed below - a FIFTH path that nobody registered goes red
///     before it can quietly ship in English.
///
/// Together those two make the bar in the issue structural: adding a fifth spoken path picks up the
/// language automatically, and the only way to avoid that is to trip a test.
/// </summary>
public static class SpokenPaths
{
    /// <summary>One place the product asks a model for words a person will hear.</summary>
    /// <param name="Name">What it is, in plain words, for a failing test's message.</param>
    /// <param name="Builder">The prompt-building method this path goes through, as
    ///  <c>TypeName.MethodName</c>. The completeness guard matches source declarations against these, so
    ///  the spelling has to be exact.</param>
    /// <param name="Render">Builds this path's prompt for a language, with sample arguments. The
    ///  arguments do not matter - what is being checked is that the language reached the prompt.</param>
    public sealed record SpokenPath(string Name, string Builder, Func<SpokenLanguage, string> Render);

    /// <summary>
    /// The four model-driven spoken paths, plus the cockpit Assistant surface of the fourth (same brain,
    /// a different prompt, and therefore a separate chance to lose the language).
    /// </summary>
    public static readonly IReadOnlyList<SpokenPath> All = new[]
    {
        new SpokenPath(
            "turn narration (WingmanTranslator.TranslateAsync)",
            "WingmanTranslator.BuildPrompt",
            language => WingmanTranslator.BuildPrompt(
                language, WingmanTranslator.FidelityPrompt, "recent context", "an agent reply", "a session")),

        new SpokenPath(
            "direct reply (WingmanTranslator.AskDirectAsync)",
            "WingmanTranslator.BuildDirectPrompt",
            language => WingmanTranslator.BuildDirectPrompt(language, "hey wingman, what is going on?")),

        new SpokenPath(
            "in-product help (WingmanTranslator.AskAboutDevThrottleAsync)",
            "WingmanTranslator.BuildDevThrottlePrompt",
            language => WingmanTranslator.BuildDevThrottlePrompt(language, "what is DevThrottle?")),

        new SpokenPath(
            "Car Mode (CarModeBrain.RunTurnAsync, car surface)",
            "CarModeBrain.BuildSystemPrompt",
            language => CarModeBrain.BuildSystemPrompt(language, CarModeSurface.Car)),

        new SpokenPath(
            "cockpit Assistant (CarModeBrain.RunTurnAsync, desk surface)",
            "CarModeBrain.BuildSystemPrompt",
            language => CarModeBrain.BuildSystemPrompt(language, CarModeSurface.Desk)),
    };

    /// <summary>
    /// <summary>
    /// SPOKEN-FIELD paths: the output as a whole is machine-read, but named FIELDS inside it are read
    /// aloud verbatim. They carry <see cref="SpeechContract.SpeakInLanguageRule"/> and NOT the whole
    /// contract, because "output plain spoken prose only, no formatting characters" would break the
    /// envelope the machine parses.
    ///
    /// This category exists because the alternative was worse in both directions. Applying the full
    /// contract would have broken menu handling, which decides whether a keypress lands in somebody's
    /// terminal. Applying nothing left a French account hearing a French frame wrapped around an English
    /// question - the "English fragment in a French session" the owner ruled out on issue #1009.
    /// </summary>
    public static readonly IReadOnlyList<SpokenPath> SpokenFieldPaths = new[]
    {
        new SpokenPath(
            "menu reading, extracted fields (WingmanTranslator.DetectMenuAsync)",
            "WingmanTranslator.BuildMenuDetectPrompt",
            language => WingmanTranslator.BuildMenuDetectPrompt(language, "1. Yes  2. No")),
    };

    /// <summary>
    /// Prompt builders in the Gateway that are DELIBERATELY not spoken at all, each named as
    /// <c>TypeName.MethodName</c> with the reason. The completeness guard reads this list, so adding an
    /// entry here is a one-line, review-visible act - the same shape the tenant-isolation gate uses for
    /// its global-table allowlist. There is no silent fourth category.
    ///
    /// Both entries produce MACHINE-READ text, and nothing they produce ever reaches a person's ears.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NotSpokenOutput = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WingmanTranslator.BuildMenuMapPrompt"] =
            "Returns a single option number for code to act on. Nothing it produces is ever spoken.",
        ["DictionarySuggestionScreen.BuildPrompt"] =
            "Screens candidate dictation-dictionary terms and returns JSON verdicts read by code. Nothing "
            + "it produces reaches a person's ears, or their eyes.",
    };
}
