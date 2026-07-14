namespace CcDirector.Core.Drivers;

/// <summary>
/// Thrown by <see cref="TerminalSubmit"/> when a submit typed its text into the composer, pressed
/// Enter, nudged the parked TUI repeatedly, and still never saw the agent start the turn - the
/// prompt is sitting in the composer unsubmitted. It is a distinct type, not a bare
/// <see cref="InvalidOperationException"/>, so a caller can tell "the text is in the composer but
/// nothing ran" apart from <see cref="ComposerNotAcceptingInputException"/> ("the text never even
/// reached the composer") and from every other failure. It derives from
/// <see cref="InvalidOperationException"/> so existing generic catches keep working unchanged.
///
/// This exists so a lost Enter FAILS instead of reporting success: before it, a swallowed Enter
/// left the prompt parked while the session was marked Working, so the operator watched a dictation
/// sit in the composer, saw the session claim to be working, and then saw it settle back to idle
/// with their words never sent - and the NEXT send typed itself onto the end of the orphan.
/// </summary>
public sealed class PromptNotSubmittedException : InvalidOperationException
{
    public PromptNotSubmittedException(string message) : base(message) { }
}
