namespace CcDirector.Core.Sessions;

/// <summary>
/// Thrown by <see cref="Session.SendTextAsync(string, SendSource)"/> when a human
/// (<see cref="SendSource.UserInput"/>) tries to send into a session that is LOCKED because a
/// dictation is inbound to it (issue #1181, Task 3b). Entry points (the desktop typing handler,
/// the Director control API) catch this and surface the message; it is never expected to escape a
/// UI/endpoint boundary. The message matches the Gateway front-door lock wording (issue #1188) so
/// the user sees ONE consistent sentence no matter which surface they typed on.
/// </summary>
public sealed class SessionLockedException : Exception
{
    /// <summary>The one user-facing sentence, identical to the Gateway front-door 423 (issue #1188).</summary>
    public const string LockMessage =
        "This session is receiving a dictation. You cannot send input until it arrives or is cancelled.";

    public SessionLockedException() : base(LockMessage) { }
}
