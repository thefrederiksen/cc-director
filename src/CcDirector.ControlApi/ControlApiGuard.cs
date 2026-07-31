namespace CcDirector.ControlApi;

/// <summary>
/// The verdict on one request, and the sentence explaining it. A refusal always names its reason so
/// a client that breaks is debuggable from the Director log without guesswork.
/// </summary>
public readonly record struct GuardVerdict(bool Allowed, string Reason)
{
    public static readonly GuardVerdict Allow = new(true, "");

    public static GuardVerdict Refuse(string reason) => new(false, reason);
}

/// <summary>
/// The server-side decisions the Director's Control API makes about a request BEFORE any handler
/// runs: is this request addressed to us at all (the Host allowlist), is it a cross-site browser
/// mutation (the cross-site gate), and is the presented credential allowed to do this (the scope
/// gate).
///
/// All three are PURE functions on request facts, so they are unit-testable and there is exactly one
/// place each rule lives. The middleware in <see cref="ControlApiHost"/> only reads request headers
/// and asks these methods.
///
/// This is NOT CORS. CORS is a set of RESPONSE headers that asks a browser to enforce a rule on our
/// behalf; anything that is not a browser ignores it entirely, which makes it a convenience and never
/// a boundary. Every decision here is made server-side and ends in a refusal before the handler.
/// </summary>
public static class ControlApiGuard
{
    /// <summary>
    /// The DNS-rebinding defence. A browser that has been handed a rebound name resolving to
    /// 127.0.0.1 still sends the ATTACKER'S name in the Host header, so accepting only the exact
    /// loopback authority this Director bound refuses the request before a handler ever sees it.
    ///
    /// The bind is loopback-only and stays that way; this is the authorization half of the same
    /// question, because loopback tells you the peer is on this machine and nothing about who it is.
    ///
    /// A missing Host header is refused: every HTTP/1.1 and HTTP/2 client sends one, so its absence
    /// is a hand-rolled caller, not a client we support.
    /// </summary>
    public static GuardVerdict CheckHost(string? hostHeader, int boundPort)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
            return GuardVerdict.Refuse("the request carried no Host header");

        var host = hostHeader.Trim();
        var colon = host.LastIndexOf(':');
        // An IPv6 literal is bracketed; a colon inside the brackets is part of the address, not a port.
        var closingBracket = host.LastIndexOf(']');
        string name;
        int port;
        if (colon > closingBracket && colon >= 0)
        {
            name = host[..colon];
            if (!int.TryParse(host[(colon + 1)..], out port))
                return GuardVerdict.Refuse($"the Host header '{hostHeader}' has no readable port");
        }
        else
        {
            name = host;
            port = 80;
        }

        var nameAllowed = string.Equals(name, "127.0.0.1", StringComparison.Ordinal)
                          || string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase);

        if (!nameAllowed)
            return GuardVerdict.Refuse($"the Host header '{hostHeader}' is not this Director's loopback address");

        if (port != boundPort)
            return GuardVerdict.Refuse($"the Host header '{hostHeader}' names port {port}, not the bound port {boundPort}");

        return GuardVerdict.Allow;
    }

    /// <summary>True for the request methods that can change something.</summary>
    public static bool IsMutating(string method)
        => method is "POST" or "PUT" or "PATCH" or "DELETE";

    /// <summary>
    /// The cross-site mutation gate, applied to mutating requests only.
    ///
    /// A browser labels every request it makes: <c>Sec-Fetch-Site</c> says how the initiating page
    /// relates to the target, and <c>Origin</c> names the initiating page. We accept a browser
    /// request only when it says same-origin AND its Origin is our own loopback origin. Everything
    /// else a browser can produce - a form on an attacker's page, a fetch from another localhost
    /// port (which the browser calls "same-site", not "same-origin", because a port does not make a
    /// new site) - is refused.
    ///
    /// A legitimate non-browser client (the command line, the launcher, a hook) sends NEITHER header,
    /// because only browsers add them, and carries a valid token instead. That is the one case where
    /// absence is meaningful: these headers cannot be stripped by the page that would want them gone.
    /// </summary>
    public static GuardVerdict CheckCrossSiteMutation(string method, string? secFetchSite, string? origin, int boundPort)
    {
        if (!IsMutating(method))
            return GuardVerdict.Allow;

        if (!string.IsNullOrWhiteSpace(secFetchSite)
            && !string.Equals(secFetchSite.Trim(), "same-origin", StringComparison.OrdinalIgnoreCase))
        {
            return GuardVerdict.Refuse(
                $"a browser reported this mutating request as Sec-Fetch-Site: {secFetchSite.Trim()}; only same-origin is accepted");
        }

        if (!string.IsNullOrWhiteSpace(origin) && !IsOwnLoopbackOrigin(origin.Trim(), boundPort))
        {
            return GuardVerdict.Refuse(
                $"the mutating request carried a foreign Origin '{origin.Trim()}'");
        }

        return GuardVerdict.Allow;
    }

    private static bool IsOwnLoopbackOrigin(string origin, int boundPort)
        => string.Equals(origin, $"http://127.0.0.1:{boundPort}", StringComparison.OrdinalIgnoreCase)
           || string.Equals(origin, $"http://localhost:{boundPort}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a session-child credential is allowed to do: read its OWN session, and the safe
    /// discovery set an agent's preamble needs. This is an ALLOW list, so anything the product grows
    /// later is denied to a child until somebody deliberately adds it here - the opposite of a deny
    /// list, where every new dangerous route is open until someone remembers.
    ///
    /// <paramref name="boundSessionId"/> is the id the token is signed for. A route that names a
    /// session must name THAT session; a child token presented for another session's id is refused
    /// exactly as if it were unauthenticated for that route.
    /// </summary>
    public static GuardVerdict CheckSessionChild(string method, string path, Func<string, string?> queryValue, Guid boundSessionId)
    {
        var p = (path ?? "").TrimEnd('/');
        if (p.Length == 0) p = "/";

        if (method == "GET")
        {
            switch (p.ToLowerInvariant())
            {
                // Liveness and the safe discovery set. The roster, the repositories and the worktrees
                // are what a fleet preamble and the cc-devthrottle read verbs need to orient an agent;
                // none of them changes anything and none of them carries a credential.
                case "/healthz":
                case "/fleet/sessions":
                case "/fleet/repositories":
                case "/fleet/worktrees":
                    return GuardVerdict.Allow;

                // The terminal scrollback carries whatever the agent typed, including secrets, so it is
                // readable ONLY for the child's own session.
                case "/fleet/buffer":
                    return SameSession(queryValue("sessionId"), boundSessionId, "GET /fleet/buffer");
            }
        }

        // The three own-session routes: the two preamble reads and the Claude session-pointer report.
        // Each is scoped to the {sid} in the path, which must be the token's bound session.
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 3 && string.Equals(segments[0], "sessions", StringComparison.OrdinalIgnoreCase))
        {
            var routeName = segments[2].ToLowerInvariant();
            var allowed = (method, routeName) switch
            {
                ("GET", "fleet-preamble") => true,
                ("GET", "fleet-preamble-hook-output") => true,
                ("POST", "claude-hook") => true,
                _ => false,
            };
            if (allowed)
                return SameSession(segments[1], boundSessionId, $"{method} {p}");
        }

        return GuardVerdict.Refuse(
            $"a session-child credential may not call {method} {p}; it may read its own session and the safe discovery set only");
    }

    private static GuardVerdict SameSession(string? presentedId, Guid boundSessionId, string what)
    {
        if (!Guid.TryParse(presentedId, out var asked))
            return GuardVerdict.Refuse($"{what} names no readable session id");

        return asked == boundSessionId
            ? GuardVerdict.Allow
            : GuardVerdict.Refuse($"{what} names session {asked}, but this credential is bound to session {boundSessionId}");
    }
}
