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

    /// <summary>
    /// Render the text that is ACTUALLY injected into this session - the user's own version when they
    /// are running one, otherwise the default above. Every delivery path calls this, so no agent can
    /// receive a different answer to "whose text is live" than the Settings tab shows.
    ///
    /// NOTHING MEANS NOTHING, AND IT MEANS IT EVERYWHERE. Text that renders to whitespace comes back
    /// as the empty string, so every delivery path reaches the same conclusion by the same route. This
    /// rule lives here, in the one function they all call, because when it lived in the delivery paths
    /// they disagreed: a user who saved "   " got whitespace through some agents and nothing through
    /// others. An injected space is not a thing anyone wants; the only question was whether all the
    /// agents agreed, and now they do.
    /// </summary>
    /// <exception cref="InjectedTextUnavailableException">
    /// The user's text is live but unreadable. Not recovered by substituting ours: they turned ours
    /// off. See <see cref="InjectedTextStore.ActiveTemplate"/>.
    /// </exception>
    /// <exception cref="FleetPreambleTemplateException">
    /// The user's text is live but is not a renderable template - it is validated when saved, so this
    /// means it was edited on disk afterwards. Also not recovered by substituting ours.
    /// </exception>
    public static string BuildForSession(
        string sessionId,
        string? name,
        string machine,
        string repoPath,
        SignedInUser? user = null,
        InjectedTextStore? store = null)
    {
        var text = FleetPreambleRenderer.Render(
            (store ?? new InjectedTextStore()).ActiveTemplate(), sessionId, name, machine, repoPath, user);

        return string.IsNullOrWhiteSpace(text) ? "" : text;
    }
}
