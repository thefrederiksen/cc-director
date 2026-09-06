namespace CcDirector.Core.Sessions;

/// <summary>
/// THE ONE RULE FOR WHEN A SUBMITTED TURN IS SPOKEN, shared by every surface (ruling R10 of the "Clean up
/// Your Throttle" mission; ruling R20 after the owner described how he actually dictates, 2026-09-05).
///
/// A turn is spoken only when the words sent are ONE transcription and nothing else. Typed text before or
/// after the transcript, an earlier dictated segment already turned to text, or an edit to the transcript
/// itself makes the turn typed - it is delivered exactly the same, and counted as typed, on every surface.
///
/// Before this class the rule lived in two places that disagreed. The phone's durable dictation applied it
/// at the Gateway (inspection finding I2-01); the desktop had no rule at all: its compose box did not know
/// which of its characters came from a microphone, so the send path guessed from which BUTTON was pressed.
/// A transcript inserted into the box and sent with the ordinary Send was stamped typed under a comment
/// asserting the composer is typed "by construction" - false the moment dictation was inserted - and the
/// background Send stamped typed text composed AROUND a dictation as spoken. Measured over the owner's
/// 2026-W35: 25 of 655 typed turns carried a stored transcript verbatim, against a 97.5 per cent control.
///
/// Both surfaces now call <see cref="IsSpokenAlone"/>, and <see cref="Examples"/> is the table both
/// surfaces' tests feed through their REAL paths, so an identical mixture cannot classify differently
/// on the desktop and on the phone without a test noticing.
/// </summary>
public static class SpokenTurnRule
{
    /// <summary>
    /// True when the message is the transcript and nothing else: no typed text before or after the caret,
    /// and no earlier dictated segment already turned to text (a prefix). Whitespace is not text.
    /// </summary>
    public static bool IsSpokenAlone(string? before, string? prefix, string? after)
        => string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(prefix) && string.IsNullOrWhiteSpace(after);

    /// <summary>The modality of a composed turn on any surface: the transcript alone is voice; a mixture is typed.</summary>
    public static InputModality Classify(string? before, string? prefix, string? after)
        => IsSpokenAlone(before, prefix, after) ? InputModality.Voice : InputModality.Typed;

    /// <summary>
    /// One mixture a surface can send: what was typed before the caret, the earlier dictated text, the
    /// transcript, what was typed after, and the modality the rule gives it. <see cref="Examples"/> is the
    /// contract between the surfaces.
    /// </summary>
    public readonly record struct Example(string Name, string Before, string Prefix, string Transcript, string After, InputModality Expected);

    /// <summary>
    /// THE TABLE BOTH SURFACES ARE HELD TO. The desktop's compose box and background Send, and the phone's
    /// durable dictation route, each feed every row here through their real path and must land on the
    /// row's modality. A row is added here, once, and both surfaces' tests pick it up.
    /// </summary>
    public static readonly IReadOnlyList<Example> Examples = new[]
    {
        new Example("the transcript alone", "", "", "deploy the gateway and tell me when it is up", "", InputModality.Voice),
        new Example("the transcript with whitespace around it", "  ", "", "deploy the gateway and tell me when it is up", "\t ", InputModality.Voice),
        new Example("typed text before the transcript", "please", "", "deploy the gateway and tell me when it is up", "", InputModality.Typed),
        new Example("typed text after the transcript", "", "", "deploy the gateway and tell me when it is up", "and restart it", InputModality.Typed),
        new Example("typed text on both sides", "please", "", "deploy the gateway and tell me when it is up", "and restart it", InputModality.Typed),
        new Example("an earlier dictated segment ahead of the transcript", "", "first check the logs", "deploy the gateway and tell me when it is up", "", InputModality.Typed),
        new Example("an earlier segment and typed text on both sides", "please", "first check the logs", "deploy the gateway and tell me when it is up", "and restart it", InputModality.Typed),
    };

    /// <summary>One run of characters in the compose box that came from a microphone: the box text from
    /// <paramref name="Start"/> for <paramref name="Length"/> characters is one inserted transcript,
    /// untouched since it was inserted.</summary>
    public readonly record struct SpokenSpan(int Start, int Length)
    {
        public int End => Start + Length;
    }

    /// <summary>
    /// THE COMPOSE BOX'S OWN RECORD OF WHICH OF ITS CHARACTERS WERE SPOKEN (ruling R20). The box is a plain text
    /// control, so this sits beside it and follows every change to its text: each inserted transcript is a
    /// <see cref="SpokenSpan"/> - a character RANGE, not a string - and every change to the box is applied to the
    /// spans as an edit: characters typed or pasted before a span move it, characters after it leave it alone,
    /// and any change that touches the span's own characters forgets it, because an edited transcript is typed.
    ///
    /// Ranges, not strings, because the fix-round inspector showed the difference: with the same words typed
    /// and then dictated, a record that only knew the transcript's TEXT still called the box spoken after the
    /// spoken copy was deleted and the typed copy kept. A range knows which of the two occurrences was spoken.
    ///
    /// On Send, <see cref="Classify"/> applies <see cref="SpokenTurnRule"/>: the text is voice only when it holds
    /// ONE spoken span and nothing but whitespace outside it - typed words around it, a second dictation, or an
    /// edit to the spoken words all make it typed, exactly as on the phone.
    ///
    /// The box's text-changed hook is deferred by the toolkit (it is posted, not raised inline), so the record
    /// is kept in step in two ways: the insert path tells it the new text and the span directly, and the hook
    /// tells it every text it did not already know. <see cref="OriginFor"/> refuses a text it has not been
    /// told about rather than classify a box it has lost track of.
    /// </summary>
    public sealed class ComposerProvenance
    {
        private string _text = "";
        private readonly List<SpokenSpan> _spans = new();

        /// <summary>The box text this record describes.</summary>
        public string Text => _text;

        /// <summary>The spoken ranges still standing in the box, in text order.</summary>
        public IReadOnlyList<SpokenSpan> Spans => _spans;

        /// <summary>
        /// A transcript was inserted: the box now holds <paramref name="textNow"/>, and its characters from
        /// <paramref name="at"/> for the transcript's length ARE the transcript. Any change the box made around
        /// the insertion (a separating space) is applied to the earlier spans first.
        /// </summary>
        public void Inserted(string textNow, string transcript, int at)
        {
            ArgumentNullException.ThrowIfNull(textNow);
            ArgumentNullException.ThrowIfNull(transcript);
            if (transcript.Length == 0) { TextChanged(textNow); return; }
            if (at < 0 || at + transcript.Length > textNow.Length
                || !string.Equals(textNow.Substring(at, transcript.Length), transcript, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"The box text does not hold the transcript at {at}: the caller said {transcript.Length} spoken characters " +
                    "were inserted there, and the text there is different. The record refuses to mark characters it cannot see.",
                    nameof(at));
            TextChanged(textNow);
            _spans.Add(new SpokenSpan(at, transcript.Length));
            _spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        /// <summary>
        /// The box's text is now <paramref name="text"/>. The change from the text last known is applied to the
        /// spans as an edit: a spoken character that is still there keeps its span, a span whose characters
        /// were removed or changed, or that had characters inserted inside it, is forgotten, and a surviving
        /// span moves to where its characters now stand. Called from the box's text-changed hook, so no
        /// caller that sets the text has to remember the provenance; harmless when the text is already known.
        ///
        /// Where the change happened is found in this order. <paramref name="caretAfter"/> - where the box's
        /// caret stands after the change, when the caller knows it - decides when the text alone cannot: with
        /// the same words twice over, deleting the FIRST copy and deleting the SECOND leave the identical text,
        /// and only the caret says which characters went; it is used only when the region it names reproduces
        /// the new text from the old. Otherwise the change is the region between the common prefix and the
        /// common suffix; when both of those are non-empty (a replacement, or edits in more than one place, as
        /// a whole text set from the expanded editor can be) the two middles are aligned character by character
        /// so that spoken characters left untouched are found wherever they now stand.
        /// </summary>
        public void TextChanged(string? text, int? caretAfter = null)
        {
            var now = text ?? "";
            var was = _text;
            if (string.Equals(now, was, StringComparison.Ordinal)) return;
            if (_spans.Count == 0) { _text = now; return; }

            var prefix = 0;
            var max = Math.Min(was.Length, now.Length);
            while (prefix < max && was[prefix] == now[prefix]) prefix++;
            var suffix = 0;
            while (suffix < max - prefix && was[was.Length - 1 - suffix] == now[now.Length - 1 - suffix]) suffix++;
            var oldRegionEnd = was.Length - suffix;
            var newRegionEnd = now.Length - suffix;
            var delta = now.Length - was.Length;

            if (caretAfter is int caret)
            {
                if (delta < 0 && caret >= 0 && caret - delta <= was.Length
                    && string.Equals(was.Remove(caret, -delta), now, StringComparison.Ordinal))
                {
                    prefix = caret; oldRegionEnd = caret - delta; newRegionEnd = caret;
                }
                else if (delta > 0 && caret - delta >= 0 && caret <= now.Length
                         && string.Equals(now.Remove(caret - delta, delta), was, StringComparison.Ordinal))
                {
                    prefix = caret - delta; oldRegionEnd = caret - delta; newRegionEnd = caret;
                }
            }

            // Where every old character went: its index in the new text, or -1 when it is gone.
            var map = new int[was.Length];
            for (var i = 0; i < prefix; i++) map[i] = i;
            for (var i = oldRegionEnd; i < was.Length; i++) map[i] = i + delta;
            for (var i = prefix; i < oldRegionEnd; i++) map[i] = -1;
            var oldMiddle = oldRegionEnd - prefix;
            var newMiddle = newRegionEnd - prefix;
            if (oldMiddle > 0 && newMiddle > 0 && (long)oldMiddle * newMiddle <= AlignmentCells)
                Align(was, now, prefix, oldMiddle, newMiddle, map);

            for (var i = _spans.Count - 1; i >= 0; i--)
            {
                var span = _spans[i];
                var start = map[span.Start];
                var intact = start >= 0;
                for (var k = 1; intact && k < span.Length; k++)
                    intact = map[span.Start + k] == start + k;
                if (intact) _spans[i] = new SpokenSpan(start, span.Length);
                else _spans.RemoveAt(i);
            }
            _text = now;
        }

        /// <summary>The largest middle-by-middle alignment attempted (about eight megabytes of table). Beyond
        /// it the whole middle is treated as one changed region, which can only forget a span, never invent one.</summary>
        private const long AlignmentCells = 4_000_000;

        /// <summary>The longest common subsequence of the two middles, so each old character that survived is
        /// mapped to where it stands now. Earlier matches are preferred on a tie, which is the reading a person
        /// gives an edit as well.</summary>
        private static void Align(string was, string now, int offset, int oldLen, int newLen, int[] map)
        {
            var table = new int[oldLen + 1, newLen + 1];
            for (var i = oldLen - 1; i >= 0; i--)
                for (var j = newLen - 1; j >= 0; j--)
                    table[i, j] = was[offset + i] == now[offset + j]
                        ? table[i + 1, j + 1] + 1
                        : Math.Max(table[i + 1, j], table[i, j + 1]);
            int a = 0, b = 0;
            while (a < oldLen && b < newLen)
            {
                if (was[offset + a] == now[offset + b]) { map[offset + a] = offset + b; a++; b++; }
                else if (table[a + 1, b] >= table[a, b + 1]) a++;
                else b++;
            }
        }

        /// <summary>Put back a record saved for this box text - a session switched back to, or a Director
        /// restarted. A span outside the text is a corrupt record and is refused.</summary>
        public void Restore(string? text, IEnumerable<SpokenSpan> spans)
        {
            ArgumentNullException.ThrowIfNull(spans);
            var now = text ?? "";
            var list = spans.OrderBy(s => s.Start).ToList();
            foreach (var span in list)
                if (span.Start < 0 || span.Length <= 0 || span.End > now.Length)
                    throw new ArgumentException($"A spoken span {span.Start}+{span.Length} lies outside a text of {now.Length} characters.", nameof(spans));
            _text = now;
            _spans.Clear();
            _spans.AddRange(list);
        }

        /// <summary>The box was sent: nothing dictated stands in it any more. The text itself is learnt from the
        /// box's hook when the clear reaches it.</summary>
        public void Reset() => _spans.Clear();

        /// <summary>
        /// The modality of the text in the box: voice only when exactly one spoken span stands in it and every
        /// character outside that span is whitespace. Two spans are an earlier dictated segment ahead of the
        /// transcript; any other character is typed text before or after it; both are typed under the one rule.
        /// </summary>
        public InputModality Classify()
        {
            if (_spans.Count != 1) return InputModality.Typed;
            var span = _spans[0];
            var before = _text[..span.Start];
            var after = _text[span.End..];
            return SpokenTurnRule.Classify(before, "", after);
        }

        /// <summary>
        /// The desktop origin for the text about to be sent. <paramref name="boxText"/> is the box's text at the
        /// moment of the send, and it must be the text this record was told about: the box's hook is deferred, so a
        /// send that read the box after clearing it, or a box whose hook was never wired, would otherwise be
        /// classified against the wrong text. That is refused as the defect it is, not classified as typed.
        /// </summary>
        public InputOrigin OriginFor(string? boxText)
        {
            var now = boxText ?? "";
            if (!string.Equals(now, _text, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The compose box's provenance describes a different text from the one being sent " +
                    $"({_text.Length} characters known, {now.Length} in the box). Every change to the box must reach " +
                    "ComposerProvenance.TextChanged or Inserted before Send asks it what the text is.");
            return Classify() == InputModality.Voice ? InputOrigin.DesktopVoice : InputOrigin.DesktopTyped;
        }
    }
}
