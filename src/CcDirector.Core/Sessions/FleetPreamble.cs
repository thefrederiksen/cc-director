using CcDirector.Core.Account;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Builds the one-screen "fleet awareness" preamble that a session receives at launch so the
/// agent knows its own identity and how to reach the rest of the fleet WITHOUT first having to
/// discover and read a skill. This removes the discovery delay: every session already carries
/// CC_SESSION_ID and CC_DIRECTOR_API in its environment, but an agent never reads environment
/// variables unless something surfaces them - this is that something.
///
/// Surfaced into Claude sessions through the SessionStart hook's additionalContext (zero turn
/// cost; see <see cref="Claude.ClaudeHookInstaller"/>) and reusable by other agent integrations
/// through the GET /sessions/{sid}/fleet-preamble Control API endpoint.
///
/// THE TEXT ITSELF LIVES IN <see cref="FleetPreambleTemplate"/> and is filled in by
/// <see cref="FleetPreambleRenderer"/>. It used to be assembled here with C# string interpolation,
/// which made it impossible for a user to see or change what we put into their agents. Keeping the
/// text as data is what lets the Settings tab show it and, later, let the user replace it.
///
/// ASCII only (no Unicode) so it renders cleanly in every agent's terminal on Windows.
/// </summary>
public static class FleetPreamble
{
    /// <summary>
    /// Render the DEFAULT preamble - the text DevThrottle ships - for one session.
    /// <paramref name="name"/> may be null/empty (an unnamed session); the other values are always
    /// present on a live session. <paramref name="user"/> is the signed-in DevThrottle user (issue
    /// #1357); when null (no one signed in) the user-identity line is omitted cleanly - no blank
    /// line, no "null" artifact.
    /// </summary>
    public static string Build(string sessionId, string? name, string machine, string repoPath, SignedInUser? user = null)
        => FleetPreambleRenderer.Render(FleetPreambleTemplate.Default, sessionId, name, machine, repoPath, user);
}
