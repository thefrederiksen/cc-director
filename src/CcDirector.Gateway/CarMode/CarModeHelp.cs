using CcDirector.Gateway.Speech;

namespace CcDirector.Gateway.CarMode;

/// <summary>
/// What the fleet brain tells a person about itself: ONE curated spoken script. It lives here, next to the
/// brain's tool catalog and system prompt, so when the tools or the addressing grammar change, the help
/// changes in the same place and never drifts out of sync.
///
/// It is reached one way: the person asks - "help", "what can you do" - the brain classifies the intent and
/// calls its get_help tool, and that tool returns <see cref="SpokenScript"/> VERBATIM as the spoken answer,
/// never the model's own words.
///
/// Car Mode had a second way in and a second form of this content: a model-free GET /carmode/help door behind
/// a big Help button, and a structured cheat-sheet the phone screen drew beside it. Car Mode was removed from
/// the product (#1028) and both went with it - nothing else ever drew that card or called that door.
///
/// The script is deliberately short (~15-20 seconds spoken), spoken prose, sessions by name never number.
/// </summary>
public static class CarModeHelp
{
    /// <summary>
    /// The curated spoken explanation, read aloud verbatim when the person asks for help. Plain spoken prose: it
    /// names the two ways to talk to the Assistant, the key commands, and a concrete relay example.
    ///
    /// IT TAKES NO SETTING (audit finding C6). It used to quote the tenant's configured end phrase, because Car
    /// Mode ended a hands-free turn on a spoken phrase. Car Mode is gone and the Assistant has an explicit Send
    /// action, so the help was teaching a command that ends nothing - and this is the second time this script has
    /// gone stale against a setting, the first being a hardcoded "over and out" while the phrase was
    /// configurable. There is now nothing in it that can go stale.
    ///
    /// It is SPOKEN, so it is spoken in the account's language, and the words live with every other fixed spoken
    /// sentence in <see cref="SpokenPhrases.AssistantHelpScript"/>.
    /// </summary>
    public static string SpokenScript(SpokenLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return SpokenPhrases.AssistantHelpScript.In(language);
    }
}
