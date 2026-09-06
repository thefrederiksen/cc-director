using CcDirector.Core.Sessions;

namespace CcDirector.Core.Sessions;

/// <summary>
/// The two Session entry points REQUIRE a <see cref="SubmissionProvenance"/> (source logging, 2026-09-05), so
/// every production door says what it is. Tests that are not about the door - a session's delivery, its
/// state, its wingman - send through this one test door instead, so the requirement stays on production
/// code without every test restating it. A test ABOUT a door never uses this: it drives the real door.
/// </summary>
internal static class SessionTestDoors
{
    public static readonly SubmissionProvenance TestDoor = SubmissionProvenance.Typed("test-door", "test");

    public static Task SendTextAsync(this Session session, string text, SendSource source = SendSource.UserInput, InputOrigin? origin = null)
        => session.SendTextAsync(text, TestDoor, source, origin);

    public static void SendInput(this Session session, byte[] data, InputOrigin? origin = null)
        => session.SendInput(data, origin, TestDoor);
}
