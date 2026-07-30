namespace CcDirector.Gateway.Speech;

/// <summary>
/// SOMETHING THE PRODUCT IS ABOUT TO SAY OUT LOUD, WITH ITS LANGUAGE AND ITS VOICE ALREADY DECIDED
/// (issue #1031).
///
/// THE POINT OF THIS TYPE IS WHAT IT MAKES IMPOSSIBLE. Before it, a sink was handed a string, a voice and a
/// model as three separate arguments, so it was always possible - and it happened twice - to hand a sink some
/// text and no language at all. That is the hole every one of these bugs came through: the desktop resolved a
/// voice from a machine-global file and never saw the account; the browser set no language on its utterance and
/// read correct French in an English voice. Both plausible local decisions. Both silent: the audio plays.
///
/// So the language is not a parameter a caller may omit or forget. There is no parameterless construction, no
/// defaultable language, no settable-later language, and the constructor is private - the ONLY way an utterance
/// comes into existence is <see cref="For"/>, which cannot be called without naming a language. A sink whose
/// parameter is a <c>SpokenUtterance</c> cannot be passed a bare string: that is a compile error, not a test
/// failure. Issue #1008 reached for a test where a type was the stronger tool. A test tells you afterwards; a
/// type refuses.
///
/// IT CARRIES A VOICE AND IT MUST NEVER CARRY A MODEL. That is the rule the whole mission turns on. The
/// reverted build (devthrottle_internal#547) made choosing a language switch the speech MODEL, and that engine
/// could not say the lengths this product writes. A language selects the VOICE inside the one engine that
/// already serves English. There is deliberately no model, engine, endpoint or rate on this type, so there is
/// nothing here for a future change to branch an engine on - and a reflection test fails the build if a member
/// named like one appears.
///
/// WHY IT IS NOT ONE PHYSICAL SPEAKER. Some speech has to be local and network-free: when the product says it
/// cannot reach the fleet, it cannot send that sentence through the fleet to be synthesized. So there will
/// always be more than one engine - the hosted voice and the device's own - and "one place" means one CONTRACT
/// with N dumb sinks, not one speaker. This is that contract.
/// </summary>
public sealed class SpokenUtterance
{
    private SpokenUtterance(string text, SpokenLanguage language, string voice)
    {
        Text = text;
        Language = language;
        Voice = voice;
    }

    /// <summary>The words to say. Spoken CONTENT, so it carries whatever accents its language needs - and it is
    ///  therefore never written to a log or a console; log <see cref="Length"/> or the language code instead
    ///  (docs/MISSION-multilingual-RULINGS.md, guard 1).</summary>
    public string Text { get; }

    /// <summary>The language these words are in. Present on every utterance by construction.</summary>
    public SpokenLanguage Language { get; }

    /// <summary>The voice to say them with - always a voice that speaks <see cref="Language"/>, because the one
    ///  resolver that builds these decides both together.</summary>
    public string Voice { get; }

    /// <summary>How long the text is. THE log-safe fact about an utterance: a length in a log line tells you
    ///  what you need for a length bug without putting spoken content on an output channel.</summary>
    public int Length => Text.Length;

    /// <summary>
    /// Build an utterance. The only way one exists.
    ///
    /// Every argument is required, and that is the entire mechanism: there is no overload without a language,
    /// no default, and no property to set one afterwards. A caller who does not know the language cannot get
    /// past this line, which is exactly the position the old three-loose-arguments call sites were never put in.
    ///
    /// It is deliberately NOT the place that decides anything. It validates and packages what it is given; the
    /// DECISION - which language this account is spoken to in, and which voice speaks it - belongs to
    /// <c>TenantSettingsResolver</c>, which is the one caller of this method in the Gateway and is where a
    /// fourth language is added.
    /// </summary>
    /// <exception cref="ArgumentNullException">The language is null - the failure this type exists to make
    ///  loud. Never a silent English default: an account that quietly gets English is the reported bug.</exception>
    /// <exception cref="ArgumentException">The text or the voice is blank. Synthesizing nothing costs money and
    ///  returns silence, and a blank voice reaches the engine as a 422 the listener hears as silence too - so
    ///  both fail here, where the caller can see them, rather than at the far end.</exception>
    public static SpokenUtterance For(SpokenLanguage language, string voice, string text)
    {
        ArgumentNullException.ThrowIfNull(language);
        var spokenVoice = (voice ?? "").Trim();
        if (spokenVoice.Length == 0)
            throw new ArgumentException(
                "A spoken utterance needs a voice. The resolver always has one - every language has a default "
                + "voice - so a blank here means the voice was lost between the decision and this call.",
                nameof(voice));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException(
                "A spoken utterance needs words. Synthesizing an empty string is billed and returns silence, "
                + "which reaches the listener as a voice that failed.", nameof(text));
        return new SpokenUtterance(text, language, spokenVoice);
    }

    /// <summary>
    /// The same utterance with different words - same language, same voice.
    ///
    /// For the one legitimate reason to change the text after the decision: a length guard that trims a runaway
    /// narration and appends the sentence telling the listener it was cut. Going back through
    /// <see cref="For"/> would mean handing a language in again at a call site that has one in its hand, which
    /// is precisely the shape this type removes.
    /// </summary>
    public SpokenUtterance WithText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("A spoken utterance needs words.", nameof(text));
        return new SpokenUtterance(text, Language, Voice);
    }
}
