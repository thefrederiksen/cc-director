namespace CcDirector.Core.Drivers;

/// <summary>
/// Thrown by <see cref="TerminalSubmit"/> when an echo-verified submit types text into a session's
/// composer but the composer never echoes it back after two attempts - the TUI is not accepting input
/// (a modal, a picker, or a composer still initializing at the startup splash). It is a distinct type,
/// not a bare <see cref="InvalidOperationException"/>, so a caller that retries a submit can tell "the
/// composer would not take this turn" apart from every other failure and stop re-typing instead of
/// stacking duplicate copies (issue #1135). It derives from <see cref="InvalidOperationException"/> so
/// existing generic catches keep working unchanged.
/// </summary>
public sealed class ComposerNotAcceptingInputException : InvalidOperationException
{
    public ComposerNotAcceptingInputException(string message) : base(message) { }
}
