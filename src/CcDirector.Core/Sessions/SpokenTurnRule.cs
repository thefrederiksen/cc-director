using System.Text.RegularExpressions;

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

    /// <summary>
    /// The desktop compose box's record of which of its words came from a microphone (ruling R20). The box
    /// itself is a plain text control, so the provenance is kept beside it: every transcript inserted since
    /// the box last changed to something that no longer holds it. On Send, <see cref="Classify(string)"/>
    /// applies <see cref="SpokenTurnRule"/>: the text sent is voice only when it is ONE inserted transcript
    /// and nothing else - typed words around it, a second dictation joined to it, or an edit to its words
    /// all make it typed, exactly as on the phone.
    /// </summary>
    public sealed class ComposerProvenance
    {
        private readonly List<string> _transcripts = new();

        /// <summary>The transcripts still standing in the box, in the order they were inserted.</summary>
        public IReadOnlyList<string> Transcripts => _transcripts;

        /// <summary>A transcript was inserted into the box.</summary>
        public void Inserted(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript)) return;
            _transcripts.Add(Normalize(transcript));
        }

        /// <summary>The box's text changed (typing, a paste, a programmatic replacement): a transcript the
        /// text no longer contains is forgotten. Called from the box's text-changed hook, so no caller that
        /// sets the text has to remember the provenance.</summary>
        public void TextChanged(string? text)
        {
            var now = Normalize(text ?? "");
            _transcripts.RemoveAll(t => !now.Contains(t, StringComparison.Ordinal));
        }

        /// <summary>The box was sent or cleared: nothing dictated stands in it any more.</summary>
        public void Reset() => _transcripts.Clear();

        /// <summary>
        /// The modality of the text about to be sent from the box. Voice only when exactly one transcript
        /// stands in the box and the text IS that transcript (whitespace aside); any other text around it is
        /// "before" or "after", a second transcript is a "prefix", and both are typed under the one rule.
        /// </summary>
        public InputModality Classify(string? text)
        {
            var sent = Normalize(text ?? "");
            if (_transcripts.Count == 0 || sent.Length == 0) return InputModality.Typed;
            var transcript = _transcripts[^1];
            var at = sent.IndexOf(transcript, StringComparison.Ordinal);
            if (at < 0) return InputModality.Typed;
            var before = sent[..at];
            var after = sent[(at + transcript.Length)..];
            var prefix = _transcripts.Count > 1 ? string.Join(" ", _transcripts.Take(_transcripts.Count - 1)) : "";
            return SpokenTurnRule.Classify(before, prefix, after);
        }

        /// <summary>The desktop origin for the text about to be sent from the box.</summary>
        public InputOrigin OriginFor(string? text)
            => Classify(text) == InputModality.Voice ? InputOrigin.DesktopVoice : InputOrigin.DesktopTyped;

        /// <summary>The words, and nothing else: surrounding whitespace and runs of internal whitespace do
        /// not make a transcription a different utterance, but any other character does.</summary>
        internal static string Normalize(string text) => Regex.Replace(text.Trim(), @"\s+", " ");
    }
}
