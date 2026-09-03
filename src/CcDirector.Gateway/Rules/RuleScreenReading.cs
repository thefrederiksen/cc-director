using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// THE SCREEN A RULE IS WRITTEN AGAINST, READ BY THE GATEWAY ITSELF (fix round D, ruling D2).
///
/// Until this type existed the draft route took a screen as a string in the request body, with the agent
/// and machine it supposedly came from beside it. An independent inspection found what that made of the
/// headline safety claim: the check was optional (an empty string skipped it, and the Cockpit always sent
/// an empty string), caller-asserted (nothing established that the text was a captured screen or that the
/// named session produced it), run against a DIFFERENT text than the model saw (the whole caller string
/// versus the last forty non-empty lines in the prompt), and defeatable by whitespace. Four holes, one
/// cause: a fact the Gateway holds was being accepted as a claim from outside.
///
/// So the route takes a SESSION ID and nothing else about the screen. The Gateway locates that session in
/// the caller's own account, reads its screen through the same tunnel read the evaluator uses, and takes
/// the agent and the machine from the roster row it already holds. There is no path that carries no
/// screen, so grounding cannot be skipped; there is no caller-supplied origin, so scope cannot be claimed.
///
/// <see cref="Excerpt"/> IS THE ONE TEXT. It is produced once, here, when the reading is made, and the
/// prompt shows it and the check runs against it - the same string, not two readings of one string that
/// happen to agree today.
/// </summary>
public sealed class RuleScreenReading
{
    /// <param name="sessionId">The session whose screen this is.</param>
    /// <param name="origin">The agent and machine the roster holds for that session.</param>
    /// <param name="screen">The screen as read. Only its excerpt is kept.</param>
    public RuleScreenReading(string? sessionId, RuleSessionOrigin? origin, string? screen)
    {
        SessionId = (sessionId ?? "").Trim();
        Origin = origin ?? RuleSessionOrigin.None;
        Excerpt = RuleScreenExcerpt.Of(screen);
    }

    /// <summary>The session whose screen this is.</summary>
    public string SessionId { get; }

    /// <summary>Which agent printed the screen and on which machine - facts from the roster, never from
    /// the caller.</summary>
    public RuleSessionOrigin Origin { get; }

    /// <summary>THE EXACT TEXT the model is shown and the trigger words are checked against.</summary>
    public string Excerpt { get; }
}

/// <summary>
/// The tail of a screen: the last lines that are not blank. A whole scrollback would bury the session's
/// own state in text that has nothing to do with it; the tail is where that state is printed. ONE
/// FUNCTION, so the prompt and the grounding check cannot see different lengths of the same screen.
/// </summary>
public static class RuleScreenExcerpt
{
    /// <summary>How many non-blank lines from the bottom of the screen count as the screen.</summary>
    public const int Lines = 40;

    /// <summary>The excerpt of a screen, or the empty string when there is nothing on it.</summary>
    public static string Of(string? screen)
    {
        var rows = (screen ?? "").ReplaceLineEndings("\n").Split('\n')
            .Select(r => r.TrimEnd())
            .Where(r => r.Length > 0)
            .ToList();
        var tail = rows.Count <= Lines ? rows : rows.GetRange(rows.Count - Lines, Lines);
        return string.Join("\n", tail);
    }
}

/// <summary>What reading a session's screen produced: the reading, or the reason there is none.</summary>
public sealed record RuleScreenResult(RuleScreenReading? Screen, string? Refusal)
{
    /// <summary>The screen was read.</summary>
    public static RuleScreenResult Read(RuleScreenReading screen) => new(screen, null);

    /// <summary>It could not be, and this is why, in words the account reads.</summary>
    public static RuleScreenResult Refused(string reason) => new(null, reason);
}

/// <summary>
/// The seam through which the authoring path reads a session's screen: the session is looked up IN THE
/// CALLER'S TENANT, its screen is read from the machine running it, and the agent and machine come from
/// the roster. Production wires the pushed roster and the tunnel read; a test wires a fake keyed by tenant
/// and session, which is what lets the cross-tenant probe run the draft route over HTTP.
/// </summary>
public delegate Task<RuleScreenResult> RuleScreenReader(TenantId tenant, string sessionId, CancellationToken ct);
