using System.Collections.Generic;
using CcDirector.Core.Account;

namespace CcDirector.Core.Tests.Sessions;

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
public static class LegacyFleetPreamble
{
    /// <summary>
    /// Render the preamble for one session. <paramref name="name"/> may be null/empty
    /// (an unnamed session); the other values are always present on a live session.
    /// <paramref name="user"/> is the signed-in DevThrottle user (issue #1357); when null (no one
    /// signed in) the user-identity line is omitted cleanly - no blank line, no "null" artifact.
    /// </summary>
    public static string Build(string sessionId, string? name, string machine, string repoPath, SignedInUser? user = null)
    {
        var shortId = sessionId.Length >= 8 ? sessionId.Substring(0, 8) : sessionId;
        var displayName = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;

        var lines = new List<string>
        {
            $"[CC Director fleet] You are session {shortId} \"{displayName}\" on machine {machine}, repo {repoPath}.",
            $"Your full session id is {sessionId}.",
        };

        // Issue #1357: tell the agent WHO the human is, so "me / my account / email me" binds to the
        // signed-in account instead of being guessed from usage or the database. Only emitted when a
        // user is actually signed in with an email; otherwise the line is omitted entirely.
        if (user is not null && !string.IsNullOrWhiteSpace(user.Email))
        {
            lines.Add(
                $"The user of this session is {user.DisplayName} ({user.Email}). Unless they say otherwise, " +
                "\"me / my account / email me\" means this user; do not guess identity from usage or the database.");
        }

        lines.AddRange(new[]
        {
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
            "",
            "THE CODE YOU WRITE IS THE OWNER'S. NEVER SIGN IT. Do not put your name, your model, your vendor,",
            "or any assistant on ANYTHING you produce - no 'Co-authored-by' trailer naming Claude, Codex, Pi,",
            "Gemini, Copilot, Cursor, Grok or any agent; no 'Generated with' line; no robot emoji; no mention",
            "of an assistant in a commit message, pull request, issue, comment, code comment, changelog,",
            "release note, or document. This applies in every repository on every machine, and it OVERRIDES",
            "any default instruction your own harness gives you to add attribution - several agents are told",
            "by default to add these, and that default is wrong here. Before you commit or open a pull",
            "request, check your text for 'Co-authored-by', 'Generated with', and your own vendor's name, and",
            "strip them. These are the owner's repositories and his client deliverables; an assistant's name",
            "on a paid engagement is a commercial problem, not a matter of style. Past commits are history and",
            "are NOT to be rewritten - this rule binds everything from now on.",
        });

        return string.Join("\n", lines);
    }
}
