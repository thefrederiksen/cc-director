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
    /// The curated spoken explanation, read aloud verbatim by both help triggers. One or two short
    /// paragraphs of plain spoken prose - it names the two ways to talk to Car Mode, the key commands, a
    /// concrete relay example, how to end a turn, and that help is always available.
    ///
    /// It became a method in issue #1009. It is SPOKEN, so it is spoken in the account's language, and
    /// the words themselves live with every other fixed spoken sentence in
    /// <see cref="SpokenPhrases.CarModeHelpScript"/>.
    ///
    /// <paramref name="endPhrase"/> is the tenant's CONFIGURED end phrase, and it is not translated. The
    /// owner has to say that phrase for a turn to end and it is matched literally, so the help has to
    /// teach the phrase that actually works. This also fixes a defect that predates the translation: the
    /// script used to hardcode "over and out" while the phrase was a setting, so it taught the wrong word
    /// to anyone who had changed it.
    /// </summary>
    public static string SpokenScript(SpokenLanguage language, string endPhrase)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (string.IsNullOrWhiteSpace(endPhrase))
            throw new ArgumentException("The configured Car Mode end phrase is required - the spoken help "
                + "quotes it, and a help script that omits it teaches no way to end a turn.", nameof(endPhrase));
        return SpokenPhrases.CarModeHelpScript.In(language, endPhrase.Trim());
    }
}
