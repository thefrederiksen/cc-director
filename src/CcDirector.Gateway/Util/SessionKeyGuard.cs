namespace CcDirector.Gateway.Util;

/// <summary>The verdict on one request from a session key, and the sentence explaining it. A refusal always
/// names its reason so an agent whose command breaks is debuggable from one log line.</summary>
public readonly record struct SessionKeyVerdict(bool Allowed, string Reason)
{
    public static readonly SessionKeyVerdict Allow = new(true, "");

    public static SessionKeyVerdict Refuse(string reason) => new(false, reason);
}

/// <summary>
/// What a SESSION KEY may call on the Gateway (Remove-the-network-port mission, phase 1b).
///
/// This is the Gateway twin of <c>ControlApiGuard.CheckSessionChild</c>, and it is written the same way and
/// for the same reason: it is an ALLOW LIST. Anything the product grows later is DENIED to an agent until
/// somebody deliberately adds it here - the opposite of a deny list, where every new dangerous route is open
/// until someone remembers to close it. A pure function on the method and path, so there is exactly one place
/// this rule lives and it is unit-testable without a server.
///
/// THE LINE IT DRAWS. A session key may do the FLEET WORK an agent's command line does - see the roster,
/// find repositories and worktrees and machines, read a session's terminal, message/prompt/interrupt/hold/
/// rename another session, spawn one, take a mission or a role, mark itself done, and read and publish the
/// fleet's shared skills and workflows. It may NOT touch the ACCOUNT: no sign-in or sign-out, no device
/// enrollment or revocation, no credits or subscription, no Gateway or Director settings, no shutdown, no
/// Director registration, no diagnostics surface, and no voice/dictation/transcription data. Those are the
/// owner's, and an agent that is compromised or merely mistaken must not be able to reach them with a
/// credential that was handed to it automatically.
///
/// A NOTE ON WHAT IS DELIBERATELY IN. Spawning a session and launching an application on a machine are both
/// CODE EXECUTION on a computer, and both are allowed here - because both are what the fleet's agents do all
/// day through the command line today, and phase 1b is a credential change, not a capability change.
/// Narrowing them is a product decision for the owner, not a decision to smuggle in behind a refactor. What
/// this guard DOES add over today is a boundary: the key is bound to one session and one tenant, so those
/// verbs can only ever act inside the account that issued it.
/// </summary>
public static class SessionKeyGuard
{
    /// <summary>
    /// Decide whether a session key may make this request. <paramref name="path"/> is the request path with
    /// no query string; the query is deliberately not consulted, because a rule that depends on a query
    /// parameter is a rule a caller can move.
    /// </summary>
    public static SessionKeyVerdict Check(string? method, string? path)
    {
        var verb = (method ?? "").ToUpperInvariant();
        var p = (path ?? "").TrimEnd('/');
        if (p.Length == 0) p = "/";

        // Matched lower-cased, because ASP.NET routing matches a path case-insensitively and a guard that
        // did not would be bypassed by /Sessions. Only the STRUCTURE is compared - the identifier segments
        // ({sid}, {id}) are never read here - so folding their case cannot change a decision.
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
            segments[i] = segments[i].ToLowerInvariant();

        if (IsAllowed(verb, segments))
            return SessionKeyVerdict.Allow;

        return SessionKeyVerdict.Refuse(
            $"a session key may not call {verb} {p}; it may call the fleet's agent routes only, never the account surface");
    }

    private static bool IsAllowed(string verb, string[] s)
    {
        // ---------- Reads ----------
        if (verb is "GET" or "HEAD")
        {
            switch (Join(s))
            {
                // Liveness, and the discovery set the fleet preamble and the read verbs need to orient an
                // agent: who is running, where, in which repositories and worktrees, on which machines.
                case "healthz":
                case "sessions":
                case "repositories":
                case "worktrees":
                case "directors":
                case "launchers":
                case "machines":
                case "missions":
                case "gateway/about":
                case "gateway/snooze-presets":
                case "gateway/skills":
                case "gateway/workflows":
                case "gateway/workflow-runs":
                case "cron/jobs":
                    return true;
            }

            // One session (the roster row) and its terminal scrollback. The buffer carries whatever any agent
            // typed, so it is account-scoped by the key's tenant - a session key can only ever read a session
            // inside its own account, which the tenant binding enforces before the handler runs.
            if (s.Length == 2 && s[0] == "sessions") return true;
            if (s.Length == 3 && s[0] == "sessions" && s[2] == "buffer") return true;

            // One mission, one scheduled job, one workflow run.
            if (s.Length == 2 && s[0] == "missions") return true;
            if (s.Length == 3 && s[0] == "cron" && s[1] == "jobs") return true;
            if (s.Length == 3 && s[0] == "gateway" && s[1] == "workflow-runs") return true;

            // What is installed on another machine, and which files it can see - the "start something over
            // there" discovery pair. Reads only; the start itself is a POST below.
            if (s.Length == 3 && s[0] == "machines" && (s[2] == "apps" || s[2] == "files")) return true;

            // The fleet's shared skills and workflows: the catalogue entry, its body/instructions, and its
            // version history. This is how an agent reads a capability the fleet holds centrally.
            if (IsCatalogueRead(s)) return true;

            return false;
        }

        // ---------- Fleet actions ----------
        if (verb == "POST")
        {
            // Talk to a session: prompt it, interrupt it, park it, give it a role or a mission, ask it to
            // compact, or flag it finished. Every one of these names a session in the path, and the tenant
            // binding keeps it inside the calling account.
            if (s.Length == 3 && s[0] == "sessions")
            {
                switch (s[2])
                {
                    case "prompt":
                    case "interrupt":
                    case "escape":
                    case "hold":
                    case "role":
                    case "mission":
                    case "request-deletion":
                    case "compact-context":
                        return true;
                }
                return false;
            }

            // A message to the agent's own team (the fanout the fleet's "message send all" uses).
            if (Join(s) == "fanout") return true;

            // Create a mission - the unit of work sessions attach to.
            if (Join(s) == "missions") return true;

            // Start a session, or an application, on a machine in this account.
            if (s.Length == 3 && s[0] == "machines" && (s[2] == "sessions" || s[2] == "launch")) return true;

            // Contribute to the fleet's shared skills and workflows: save a draft, publish it, clone one.
            if (IsCatalogueWrite(s)) return true;

            return false;
        }

        // Rename a session (PATCH /sessions/{sid}).
        if (verb == "PATCH" && s.Length == 2 && s[0] == "sessions") return true;

        return false;
    }

    /// <summary>The read shapes of the shared skill/workflow catalogue.</summary>
    private static bool IsCatalogueRead(string[] s)
    {
        if (s.Length < 3 || s[0] != "gateway") return false;
        if (s[1] != "skills" && s[1] != "workflows") return false;

        // /gateway/{skills|workflows}/{id}
        if (s.Length == 3) return true;

        // /gateway/{skills|workflows}/{id}/{body|instructions|versions}
        if (s.Length == 4)
            return s[3] is "body" or "instructions" or "versions";

        // /gateway/{skills|workflows}/{id}/versions/{version}
        return s.Length == 5 && s[3] == "versions";
    }

    /// <summary>The write shapes of the shared skill/workflow catalogue. Deliberately NOT enable/disable -
    /// turning a fleet-wide capability off for everyone is an owner's decision, not an agent's.</summary>
    private static bool IsCatalogueWrite(string[] s)
    {
        if (s.Length != 4 || s[0] != "gateway") return false;
        if (s[1] != "skills" && s[1] != "workflows") return false;
        return s[3] is "draft" or "publish" or "clone";
    }

    private static string Join(string[] segments) => string.Join('/', segments);
}
