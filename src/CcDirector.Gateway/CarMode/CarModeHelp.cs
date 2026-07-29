using CcDirector.Gateway.Speech;

namespace CcDirector.Gateway.CarMode;

/// <summary>
/// The Car Mode Help content (Car Mode mission, Help Mode - issue #1441). This is the SINGLE SOURCE for
/// what Car Mode tells the owner about itself: one curated spoken script and a small structured cheat-sheet
/// that says the SAME thing. It lives here, next to the brain's tool catalog and system prompt, so when the
/// tools or the addressing grammar change, the help changes in the same place and never drifts out of sync.
///
/// Both help triggers return this exact content, so the owner hears the identical explanation whichever way
/// he asks:
///   - The big "Help" button on /m/car reads it through the direct, model-free front door GET /carmode/help
///     (instant, reliable, no credits) - which also serves the cheat-sheet for the on-screen glance.
///   - The spoken "help" / "what can you do" goes through the brain, which classifies the intent and calls
///     the get_help tool; that tool returns <see cref="Script"/> verbatim as the spoken answer.
///
/// The script teaches the addressing model the Help phase settles: by default you COMMAND the manager, and
/// to speak INTO a session you start with a relay verb (tell / answer / reply / message) and name it. It is
/// deliberately short (~15-20 seconds spoken), spoken prose, ASCII only, sessions by name never number.
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

    /// <summary>The glanceable, on-screen version of the same content: the two addressing modes with a few
    ///  example phrases each, plus how to end a turn and how to get help. Same content as
    ///  <see cref="SpokenScript"/>, laid out so the owner can read it when stopped or learning (Architect
    ///  decision 5: spoken primary, plus a small cheat-sheet). Kept simple on purpose.
    ///
    ///  IT STAYS IN ENGLISH, deliberately. The mission translates what the product SAYS, not what it
    ///  DISPLAYS - every other label, button and screen in the product is English, and translating this
    ///  one card would read as a half-finished translation rather than a feature. If the interface is
    ///  ever localized, this is one of the first things in it.</summary>
    public static readonly CarModeCheatSheet CheatSheet = new(
        Modes: new[]
        {
            new CarModeHelpMode(
                Title: "Command me",
                Hint: "The default - you're telling me what to do.",
                Examples: new[]
                {
                    "Who needs me?",
                    "Read me the next one",
                    "Snooze it",
                    "Approve it",
                    "Remove it",
                }),
            new CarModeHelpMode(
                Title: "Talk to a session",
                Hint: "Start with tell, answer, reply, or message, and name it.",
                Examples: new[]
                {
                    "Tell the devthrottle session to run the tests",
                    "Answer it - yes, go ahead",
                    "Message the local files session: what's your status?",
                }),
        },
        EndTurn: "Say \"over and out\" when you're done.",
        Help: "Say \"help\" any time.");
}

/// <summary>One addressing mode on the Car Mode cheat-sheet: its title, a one-line hint, and a few example
///  phrases the owner can say.</summary>
public sealed record CarModeHelpMode(string Title, string Hint, IReadOnlyList<string> Examples);

/// <summary>The structured, on-screen Car Mode cheat-sheet returned by GET /carmode/help alongside the
///  spoken script: the addressing modes, how to end a turn, and how to get help.</summary>
public sealed record CarModeCheatSheet(IReadOnlyList<CarModeHelpMode> Modes, string EndTurn, string Help);
