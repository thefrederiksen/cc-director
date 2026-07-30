using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Speech;

/// <summary>
/// THE SPOKEN OUTPUT CONTRACT (issue #1008): the one set of rules every spoken path in this product
/// obeys, and the one post-processor every spoken string passes through.
///
/// Why this type exists at all. The product speaks from roughly ten places. Four of them ask a model
/// (turn narration, the direct reply, in-product help, Car Mode) and each one used to carry its own
/// hand-written copy of the same rules: the no-Markdown rule alone was written four different ways in
/// two files, and the post-processor was applied by three of the four generators and not by Car Mode.
/// A rule copied four times is four rules, and they drift the moment one is improved. That drift is
/// not a tidiness complaint - it is the exact mechanism that got the last multilingual attempt pulled.
/// From the post-mortem (devthrottle_internal#547):
///
///   "The language reached one generator out of four. An account set to Danish had its narration
///    translated and was answered in English the moment it was spoken to."
///
/// Nobody forgot on purpose. The language was a thing you had to REMEMBER to thread into each prompt,
/// and four separate prompts meant four separate chances to forget. The fix is not to remember harder.
/// It is that there is now exactly one block of spoken rules, it is built from the language, and every
/// spoken prompt appends it - so a path either carries the language or does not carry the contract at
/// all, and <c>SpokenPathContractTests</c> fails when a registered path does not carry it.
///
/// THE BAR THIS TYPE HAS TO CLEAR: adding a FIFTH spoken path in future must pick up the language
/// automatically. It does, in three steps that are each enforced rather than remembered:
///   1. the generator already REQUIRES a <c>TenantId</c> (all four do, and a fifth will, because the
///      Gateway cannot reach a model without one), and the language is resolved FROM that tenant -
///      so there is no extra parameter to forget;
///   2. the prompt is assembled by appending <see cref="SpokenOutputContract"/>, which is the only
///      place the rules exist to be appended FROM;
///   3. the path is listed in <see cref="SpokenPaths"/>, and a source guard fails the build when a
///      spoken-prompt builder exists that is not listed there.
///
/// WHAT THIS TYPE MUST NEVER DO: choose a speech model. Nothing here branches on a language to pick
/// an engine. See <see cref="SpokenLanguage"/> for why that sentence is load-bearing.
/// </summary>
public static class SpeechContract
{
    /// <summary>
    /// The no-Markdown rule, stated ONCE. Everything the spoken paths produce is handed to speech
    /// synthesis verbatim, so a formatting character is not invisible - it is VOICED. A listener
    /// hearing "star star BPMN Studio star star" is the real bug report this rule comes from.
    ///
    /// It is a prompt rule and not a regular expression because the standing rule in this repository
    /// is that model behaviour is fixed in the prompt. <see cref="Finish"/> is the belt to this
    /// rule's suspenders, for the cheaper models that disobey it, and it exists precisely because a
    /// prompt is not a guarantee.
    /// </summary>
    public const string PlainSpokenProseRule =
        "OUTPUT PLAIN SPOKEN PROSE ONLY - NO MARKDOWN. What you write is read out loud exactly as you "
        + "wrote it, so it must contain no formatting characters at all: no asterisks for bold or "
        + "italics, no hash-mark headings, no numbered or bulleted lists, no backticks, no underscores, "
        + "no table pipes, no emoji. Write in flowing sentences. When you need to enumerate, say it in "
        + "words inside a sentence (\"first ... second ... third ...\"), never as a \"1.\" / \"2.\" list "
        + "or a \"- \" bullet. A person hearing \"star star BPMN Studio star star\" or \"hashtag Root "
        + "cause\" is exactly the failure to avoid - say the words, drop the marks.";

    /// <summary>
    /// The speak-in-this-language rule, stated ONCE and built from the account's chosen language.
    ///
    /// It is written for ALL THREE languages including English, with no special case anywhere: the
    /// English prompt says "SPEAK ENTIRELY IN ENGLISH" exactly as the French one says French. That
    /// uniformity is the point. A contract with an <c>if (language is English)</c> in it has a path
    /// that is only exercised by the non-default case, and the non-default case is the one nobody
    /// runs before shipping.
    ///
    /// The material handed to these paths is usually English - a coding agent's written reply, a
    /// terminal screen, a question typed in English - so the instruction has to say what to do with
    /// it explicitly, or a model will happily mirror the input language back.
    /// </summary>
    public static string SpeakInLanguageRule(SpokenLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var upper = language.EnglishName.ToUpperInvariant();
        return
            $"SPEAK ENTIRELY IN {upper}. Every word you output is spoken aloud to a person who has "
            + $"chosen to be spoken to in {language.EnglishName}, so write your answer in "
            + $"{language.EnglishName} - not in English unless {language.EnglishName} IS English, and "
            + "never a mixture of two languages in one answer. This holds no matter what language the "
            + $"material you are given is in: translate it into {language.EnglishName} as you say it, "
            + "rather than quoting it back in the language it arrived in. Proper nouns stay as they are "
            + "- the names of people, sessions, repositories, branches, files and products are not "
            + $"translated - but every word around them is {language.EnglishName}.";
    }

    /// <summary>
    /// The whole contract, ready to append to a spoken prompt: the language rule and the plain-prose
    /// rule, under a heading that tells the model these two OVERRIDE anything above them.
    ///
    /// The override sentence matters because of the wingman's user-editable instructions (issue #537).
    /// A person may replace the entire fidelity prompt with their own words, and their words will not
    /// mention a language. Appending the contract AFTER their instructions - always, unconditionally -
    /// is what makes it impossible for a customized prompt to silently drop the language or the
    /// no-Markdown rule. That is why the contract lives out here and not inside the default
    /// instructions text.
    /// </summary>
    public static string SpokenOutputContract(SpokenLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return "SPOKEN OUTPUT CONTRACT. These two rules bind every spoken answer this product "
            + "produces, and they override anything above that conflicts with them.\n"
            + "- " + SpeakInLanguageRule(language) + "\n"
            + "- " + PlainSpokenProseRule;
    }

    /// <summary>
    /// The deterministic sanitize-for-speech pass every spoken string goes through, whether a model
    /// wrote it or the product did (issue #1157 follow-up; made uniform by issue #1008).
    ///
    /// The output of a spoken path is the exact string handed to text-to-speech, so any Markdown mark
    /// left in it is voiced literally - "star star", "hashtag", "dash". <see cref="PlainSpokenProseRule"/>
    /// asks the model not to emit Markdown, but a prompt is not a guarantee (the narration runs on the
    /// cheaper fast tier, which disobeys it often), so this is the belt to that rule's suspenders: it
    /// removes the FORMATTING characters that are never valid spoken words while leaving the real words
    /// - including every non-Latin script, every accented letter, and every number - completely
    /// untouched. It changes how text is SPOKEN, never WHAT is said, so it cannot touch the fidelity of
    /// an answer, and it is language-blind by construction: it matches punctuation, never words.
    ///
    /// It used to live on the turn-narration generator, which is why Car Mode - the one generator that
    /// did not go through that class - never got it. It lives here now, where every spoken path can
    /// reach it and none of them has to know it exists as a separate step.
    ///
    /// Stripped/normalized: fenced and inline code, Markdown links and images (kept as their text),
    /// bold/italic/strikethrough emphasis, hash-mark headings, blockquote markers, bullet and
    /// numbered-list markers, horizontal rules, and table pipes. Word-internal underscores (identifiers
    /// like snake_case) are deliberately preserved - only underscores that wrap a word as emphasis are
    /// removed.
    /// </summary>
    public static string Finish(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // Fenced code blocks are never spoken - drop them whole (both ``` and ~~~ fences).
        text = Regex.Replace(text, @"```[\s\S]*?```", " ");
        text = Regex.Replace(text, @"~~~[\s\S]*?~~~", " ");

        // Images before links so the alt text survives: ![alt](url) -> alt, [text](url) -> text.
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");

        // Inline code keeps its inner text (issue #368) - it is often the answer's content; then
        // remove any stray unpaired backtick so none is read as "backtick".
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = text.Replace("`", "");

        // Line-leading block markers: headings, blockquotes, bullets, numbered lists, and rules.
        // Handled per line so a marker is only stripped where Markdown puts it - at the line start -
        // and a mid-sentence "step 1." or "a - b" is left alone.
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // A horizontal rule is a line of only -, * or _ (three or more) - drop it entirely.
            if (Regex.IsMatch(line, @"^\s*([-*_])(?:\s*\1){2,}\s*$")) { lines[i] = ""; continue; }
            line = Regex.Replace(line, @"^\s*#{1,6}\s+", "");     // ## Heading -> Heading
            line = Regex.Replace(line, @"^\s*>+\s?", "");          // > quote -> quote
            line = Regex.Replace(line, @"^\s*[-*+]\s+", "");       // - bullet -> bullet
            line = Regex.Replace(line, @"^\s*\d{1,3}[.)]\s+", ""); // 1. item / 2) item -> item
            lines[i] = line;
        }
        text = string.Join("\n", lines);

        // Tables: drop separator rows (|---|:--:|) and turn remaining cell pipes into a light pause
        // so "pipe" is never voiced.
        text = Regex.Replace(text, @"^\s*\|?[\s:|-]{3,}\|?\s*$", "", RegexOptions.Multiline);
        text = text.Replace("|", ", ");

        // Emphasis marks. Asterisks: strip the wrappers around bold/italic (**x**, *x*, ***x***),
        // then any leftover stray asterisk. Underscores: only when they wrap a word at a boundary,
        // so identifiers like snake_case and file_name keep their underscores. Strikethrough ~~x~~.
        text = Regex.Replace(text, @"\*{1,3}([^*\n]+?)\*{1,3}", "$1");
        text = text.Replace("*", "");
        text = Regex.Replace(text, @"(?<=^|\s)_{1,3}([^_\n]+?)_{1,3}(?=$|\s|[.,!?;:])", "$1");
        text = Regex.Replace(text, @"~~([^~\n]+?)~~", "$1");

        // Collapse the whitespace the stripping left behind.
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}
