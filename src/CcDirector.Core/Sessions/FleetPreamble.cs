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
/// ASCII only (no Unicode) so it renders cleanly in every agent's terminal on Windows.
/// </summary>
public static class FleetPreamble
{
    /// <summary>
    /// Render the preamble for one session. <paramref name="name"/> may be null/empty
    /// (an unnamed session); the other values are always present on a live session.
    /// </summary>
    public static string Build(string sessionId, string? name, string machine, string repoPath)
    {
        var shortId = sessionId.Length >= 8 ? sessionId.Substring(0, 8) : sessionId;
        var displayName = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;

        var lines = new[]
        {
            $"[CC Director fleet] You are session {shortId} \"{displayName}\" on machine {machine}, repo {repoPath}.",
            $"Your full session id is {sessionId}.",
            "You can talk to other sessions across the fleet. This command is already on your PATH:",
            "  cc-devthrottle actions --json        list agent-discoverable DevThrottle actions",
            "  cc-devthrottle session list          list every session in the fleet",
            "  cc-devthrottle session whoami        print your own id, name, machine, and repo",
            "  cc-devthrottle session rename \"name\" rename this session (uses CC_SESSION_ID)",
            "  cc-devthrottle session done          flag THIS session for deletion when you are finished",
            "                                       and nothing needs the user (the Director reaps it shortly;",
            "                                       does not kill you mid-turn). Use on unattended runs.",
            "  cc-devthrottle message send <id> \"msg\"  message a specific session",
            "  cc-devthrottle message send all \"msg\"   message your OWN TEAM (your mission / same repo)",
            "  cc-devthrottle message ask <id> \"question\"  ask a session and wait for its answer",
            "  cc-devthrottle session spawn <repo>  open a new session on this Director",
            "  cc-devthrottle schedule list       list Gateway schedules",
            "  cc-devthrottle setup status        show local setup status",
            "Address a session by a short prefix of its id or by its name. You reach the fleet through your",
            "own Director (CC_DIRECTOR_API); no Gateway address or token is needed.",
            "Every message you send interrupts the receiving agent. 'message send all' reaches only your own",
            "team, which is what you want. Do NOT try to reach the WHOLE fleet ('--everyone') - it freezes",
            "every session on every machine and repo; the Gateway Hub refuses it without a human grant (issue #1229).",
        };

        return string.Join("\n", lines);
    }
}
