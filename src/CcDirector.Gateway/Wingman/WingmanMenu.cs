using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Wingman;

/// <summary>One choice in an on-screen menu the coding agent is presenting (issue #531 menu
/// handling). Mirrors the proven brief option shape: a visible label, the EXACT keystrokes that
/// pick it (for a picker that confirms with Enter the send already includes "\r"), an optional
/// consequence note, and whether it is the recommended/default pick.</summary>
public sealed class WingmanMenuOption
{
    /// <summary>Short visible label, e.g. "1. Yes" or "Yes, and don't ask again".</summary>
    public string Key { get; set; } = "";

    /// <summary>Raw keystrokes that choose this option (e.g. "1\r" for a picker; "1" toggle for multi-select).</summary>
    public string Send { get; set; } = "";

    /// <summary>Consequence/risk of choosing this option (a permission scope, a destructive effect).</summary>
    public string? Note { get; set; }

    /// <summary>At most one option is the wingman's recommended/default pick.</summary>
    public bool Recommended { get; set; }
}

/// <summary>The structured menu the agent is showing on screen, extracted by the warm brain, plus
/// a ready-to-speak reading of it. <see cref="IsMenu"/> is false when the terminal is not an
/// interactive choice (a free-text prompt or just idle), in which case the rest is empty.</summary>
public sealed class WingmanMenu
{
    public bool IsMenu { get; set; }

    /// <summary>The choice the agent is asking, in plain speakable words.</summary>
    public string Question { get; set; } = "";

    /// <summary>"single" (pick one) | "multiple" (toggle any that apply, then <see cref="Submit"/>).</summary>
    public string SelectionMode { get; set; } = "single";

    /// <summary>The completing keystroke for "multiple" (e.g. "\r"); empty for single (each send self-submits).</summary>
    public string Submit { get; set; } = "";

    public List<WingmanMenuOption> Options { get; set; } = new();

    /// <summary>The full speakable reading: the question, each option, and how to answer. Built by the gateway.</summary>
    public string Spoken { get; set; } = "";
}

/// <summary>
/// Pure (no-brain) helpers for menu handling: a cheap heuristic to decide whether the terminal is
/// even worth a brain look, and local mapping of a spoken/typed answer to an option so the common
/// cases ("two", "the recommended one", "yes") never need a second model call.
/// </summary>
public static class WingmanMenuLogic
{
    // A line like "1. Yes", "  2) No", "a. Cancel", "> 1. Proceed", "❯ 3. ...". Leading non-word run
    // swallows arrows/markers/whitespace; then a number or single letter, a . or ), a space, content.
    private static readonly Regex OptionLine = new(@"^\W*(?:\d{1,2}|[A-Za-z])[.)]\s+\S", RegexOptions.Compiled);
    private static readonly Regex LeadingKeyNum = new(@"^\W*(\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex DigitWord = new(@"\b(\d{1,2})\b", RegexOptions.Compiled);

    /// <summary>
    /// Cheap gate: does the BOTTOM of the screen (where an active prompt lives) look like an
    /// interactive menu? True when the last ~40 lines hold 2+ numbered/lettered option lines, OR
    /// they carry a Claude-Code permission-prompt fingerprint (its "❯ 1" selection cursor, or the
    /// stock "do you want to proceed" / "don't ask again" / "yes, and" phrasing). These fingerprints
    /// are menu-specific, so a normal turn does not trip them. When false, skip the brain
    /// menu-detection entirely and treat the input as a normal prompt - that keeps non-menu turns
    /// from paying for a brain call, and (correctly) ignores a numbered list sitting in scrollback.
    /// </summary>
    public static bool LooksLikeMenu(string? terminal)
    {
        if (string.IsNullOrWhiteSpace(terminal)) return false;
        var lines = terminal.Replace("\r", "").Split('\n');
        var tailStart = Math.Max(0, lines.Length - 40);
        var count = 0;
        for (var i = tailStart; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (OptionLine.IsMatch(t)) count++;
        }
        if (count >= 2) return true;

        // Permission-prompt fingerprints (for boxed/wrapped menus the per-line regex can miss).
        var tail = string.Join("\n", lines.Skip(tailStart)).ToLowerInvariant();
        return tail.Contains("❯ 1") || tail.Contains("❯1")          // "❯ 1" selection cursor on option 1
            || tail.Contains("do you want to proceed")
            || tail.Contains("don't ask again") || tail.Contains("dont ask again")
            || tail.Contains("yes, and");
    }

    /// <summary>
    /// The AUTHORITATIVE menu gate (issue #1777), applied to the RESOLVED live screen grid rows rather than
    /// the scrollback text. The live grid is alternate-screen-correct, so this sees the menu a full-screen
    /// picker draws even though the scrollback is empty by design - which is exactly the case where the old
    /// scrollback-only <see cref="LooksLikeMenu"/> returned false and the spoken words got typed into the
    /// picker. Same fingerprint heuristic as <see cref="LooksLikeMenu"/> (2+ option lines, or a Claude-Code
    /// permission-prompt fingerprint), but the LIVE screen rules the verdict: a menu counts only when its
    /// choices and selection cursor are on screen NOW. The scrollback may only SUPPLEMENT extraction text
    /// later; it can never create a menu on its own (it is full of already-answered menus). Empty/no grid is
    /// not a menu here - an unreadable screen is handled by the caller, which fails closed.
    /// </summary>
    public static bool LiveScreenLooksLikeMenu(IReadOnlyList<string>? rows)
    {
        if (rows is null || rows.Count == 0) return false;
        return LooksLikeMenu(string.Join("\n", rows));
    }

    /// <summary>True when a row is a menu option line (a leading marker run - a box edge, a selection arrow
    /// like <c>❯</c> or <c>&gt;</c>, whitespace - then a number or letter, a <c>.</c> or <c>)</c>, a space, and
    /// content). This is the same shape <see cref="LooksLikeMenu"/> counts.</summary>
    public static bool IsOptionLine(string? row)
        => !string.IsNullOrWhiteSpace(row) && OptionLine.IsMatch(row.Trim());

    /// <summary>The DRAWN selection marker at the start of an option row: <c>❯</c> (Claude Code's Ink picker)
    /// or a plain <c>&gt;</c>, then a space or the option content.</summary>
    private static readonly Regex SelectedOption =
        new(@"^(?:❯|>)\s*(?:\d{1,2}|[A-Za-z])[.)]\s+\S", RegexOptions.Compiled);

    /// <summary>
    /// True when a row is the SELECTED option of a menu - it carries the drawn selection marker (<c>❯</c> or
    /// <c>&gt;</c>) directly before a numbered/lettered option (issue #1777, round-4). The Ink picker HIDES the
    /// hardware cursor and draws this marker instead, so this - not the cursor cell - is how the live grid says
    /// "a menu owns the turn". A bare <c>&gt; production</c> selector (no numbered option after the marker) is
    /// deliberately NOT a selected option line.
    /// </summary>
    public static bool IsSelectedOptionLine(string? row)
    {
        if (string.IsNullOrWhiteSpace(row)) return false;
        var stripped = row!.TrimStart(BorderPadding);
        return SelectedOption.IsMatch(stripped);
    }

    /// <summary>Box-drawing glyphs and pipes stripped from a row's leading edge before looking for a selection
    /// marker, so a bordered "│ ❯ 1. Yes │" reads as "❯ 1. Yes".</summary>
    private static readonly char[] BorderPadding =
    {
        '│','┃','┆','┇','┊','┋','╎','╏','║',
        '╭','╮','╰','╯','┌','┐','└','┘','╔','╗','╚','╝',
        '─','━','═','┄','┅','┈','┉','|',' ','\t','\r',
    };

    /// <summary>
    /// True when the LIVE grid carries a menu OWNED BY ITS DRAWN SELECTION MARKER (issue #1777, round-4): a row
    /// with the drawn <c>❯</c>/<c>&gt;</c> marker on a numbered/lettered option, plus two or more option lines
    /// forming ONE CONTIGUOUS BLOCK with the marker inside it. The block rule exists because an agent's prose
    /// reply routinely ends in a numbered summary of finished work; counting "1." and "3." with paragraphs of
    /// prose between them as menu options declared a finished session to be "waiting on a menu" (the session-115
    /// misfire, where a stale composer glyph supplied the marker). A real Ink picker draws its options as
    /// consecutive rows; a single non-option row inside the block is tolerated for a wrapped option label.
    /// This is cursor-INDEPENDENT on purpose - a full-screen Ink menu hides the hardware cursor - so
    /// menu-answering works with a hidden cursor. A menu with no recognizable textual marker (reverse-video
    /// only) is NOT recognized here and the caller fails closed (styled-picker parsing is deferred).
    /// This shape check remains a TRIPWIRE, not a conviction: a compact one-line-per-item summary with a stray
    /// marker still passes it, which is why every surface that BLOCKS on a menu confirms with the model first
    /// (see WaitingScreenReader.ConfirmedMenuAsync).
    /// </summary>
    public static bool LiveScreenHasMenuSelection(IReadOnlyList<string>? rows)
    {
        if (rows is null || rows.Count == 0) return false;
        var options = 0;
        var hasMarker = false;
        var gap = 0;
        foreach (var r in rows)
        {
            if (IsOptionLine(r))
            {
                options++;
                if (IsSelectedOptionLine(r)) hasMarker = true;
                gap = 0;
            }
            else if (options > 0 && gap == 0 && !string.IsNullOrWhiteSpace(r))
            {
                gap = 1;   // one wrapped option-label row is allowed inside the block
            }
            else
            {
                if (hasMarker && options >= 2) return true;   // the block that just ended was a menu
                options = 0; hasMarker = false; gap = 0;
            }
        }
        return hasMarker && options >= 2;
    }

    /// <summary>
    /// True when an extracted menu is actually ANSWERABLE (issue #1777, finding 4): it has options, every
    /// option has a non-empty visible label (a bare <c>1.</c> / <c>2.</c> with no words is not answerable), and
    /// every label actually appears on the live grid rows (the model did not invent options that are not on
    /// screen). Fail closed when any of these does not hold.
    /// </summary>
    public static bool MenuHasAnswerableOptions(WingmanMenu? menu, IReadOnlyList<string>? liveRows)
    {
        if (menu?.Options is null || menu.Options.Count == 0 || liveRows is null || liveRows.Count == 0) return false;
        var screen = Norm(string.Join("\n", liveRows));
        foreach (var o in menu.Options)
        {
            var label = Norm(StripLeadingKey(o.Key));
            if (label.Length == 0) return false;         // a bare "1." with no words - not answerable
            if (!screen.Contains(label)) return false;    // an option the model invented, not on the live grid
        }
        return true;
    }

    private static readonly Dictionary<string, int> NumberWords = new()
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
    };

    /// <summary>
    /// Map a spoken/typed answer to a 0-based option index, or -1 when there is no confident local
    /// match (the caller then asks the brain). Deliberately conservative - it returns a hit only when
    /// sure, deferring anything fuzzy to the model. Tries, in order: an explicit number (digit or
    /// word; the option's own key number, else its position), "recommended/default", ordinals
    /// ("first".."fifth"/"last"), a normalized label match, then yes/no shortcuts.
    /// </summary>
    public static int MatchOption(WingmanMenu menu, string userText)
    {
        if (menu?.Options is null || menu.Options.Count == 0 || string.IsNullOrWhiteSpace(userText)) return -1;
        var opts = menu.Options;
        var padded = " " + Norm(userText) + " ";

        // 1. "recommended" / "default" / "suggested" (most explicit intent; also beats the filler
        //    word "one" in "the recommended one").
        if (padded.Contains("recommend") || padded.Contains("default") || padded.Contains("suggest"))
        {
            var rec = opts.FindIndex(o => o.Recommended);
            if (rec >= 0) return rec;
        }

        // 2. Ordinals - BEFORE numbers, so "the last one"/"the first one" are not read as the
        //    number-word "one".
        var ord = Ordinal(padded, opts.Count);
        if (ord >= 0) return ord;

        // 3. Explicit number (digit "2" or word "two") -> option whose key starts with it, else position.
        foreach (var n in NumbersIn(padded))
        {
            for (var i = 0; i < opts.Count; i++)
                if (LeadingNumber(opts[i].Key) == n) return i;
            if (n >= 1 && n <= opts.Count) return n - 1;
        }

        // 4. Label match: the longest option label (punctuation-normalized) contained in the speech.
        var best = -1; var bestLen = 0;
        for (var i = 0; i < opts.Count; i++)
        {
            var label = Norm(StripLeadingKey(opts[i].Key));
            if (label.Length < 3) continue;
            if (padded.Contains(" " + label + " ") || padded.Contains(" " + label) || padded.Contains(label + " "))
            {
                if (label.Length > bestLen) { best = i; bestLen = label.Length; }
            }
        }
        if (best >= 0) return best;

        // 5. yes/no shortcuts -> first option whose label starts with yes / no. Negation wins so
        //    "not sure"/"no thanks" never reads as a yes.
        if (IsNegative(padded)) { var i = FindLabelStartsWith(opts, "no"); if (i >= 0) return i; }
        else if (IsAffirmative(padded)) { var i = FindLabelStartsWith(opts, "yes"); if (i >= 0) return i; }

        return -1;
    }

    /// <summary>Lowercase, strip punctuation to spaces, collapse runs - so "Yes, and don't" and
    /// "yes and dont" compare equal.</summary>
    private static string Norm(string s)
        => Regex.Replace(Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();

    /// <summary>The numbers named in the padded, normalized text - digits and number-words.</summary>
    private static IEnumerable<int> NumbersIn(string padded)
    {
        foreach (Match m in DigitWord.Matches(padded))
            if (int.TryParse(m.Groups[1].Value, out var n)) yield return n;
        foreach (var kv in NumberWords)
            if (padded.Contains(" " + kv.Key + " ")) yield return kv.Value;
    }

    private static int LeadingNumber(string key)
    {
        var m = LeadingKeyNum.Match(key ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : -1;
    }

    /// <summary>Drop a leading "1." / "2)" / "a." marker from an option label, leaving the words.</summary>
    private static string StripLeadingKey(string key)
        => Regex.Replace(key ?? "", @"^\W*(?:\d{1,2}|[A-Za-z])[.)]\s*", "").Trim();

    private static int Ordinal(string text, int count)
    {
        if (text.Contains(" last ") && count > 0) return count - 1;
        string[] words = { "first", "second", "third", "fourth", "fifth" };
        for (var i = 0; i < words.Length && i < count; i++)
            if (text.Contains(" " + words[i] + " ")) return i;
        return -1;
    }

    private static int FindLabelStartsWith(IReadOnlyList<WingmanMenuOption> opts, string prefix)
    {
        for (var i = 0; i < opts.Count; i++)
            if (StripLeadingKey(opts[i].Key).TrimStart().ToLowerInvariant().StartsWith(prefix, StringComparison.Ordinal))
                return i;
        return -1;
    }

    // Conservative on purpose (the brain handles the fuzzy cases): only strong, unambiguous tokens.
    // Input is already normalized (lowercased, apostrophes removed) - "don't" arrives as "dont".
    private static bool IsAffirmative(string text)
        => text.Contains(" yes ") || text.Contains(" yeah ") || text.Contains(" yep ") || text.Contains(" yup ")
        || text.Contains(" proceed ") || text.Contains(" approve ");

    private static bool IsNegative(string text)
        => text.Contains(" no ") || text.Contains(" nope ") || text.Contains(" cancel ") || text.Contains(" deny ")
        || text.Contains(" dont ") || text.Contains(" do not ") || text.Contains(" reject ");
}
