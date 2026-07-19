namespace CcDirector.Setup.Engine;

/// <summary>One recommended prerequisite and whether the checker found it.</summary>
public sealed record CapabilityStatus(string Name, bool IsFound);

/// <summary>
/// Turns the recommended-prerequisite results into the one sentence the Complete screen shows.
///
/// The Prerequisites screen no longer blocks on Claude Code, Python or Node.js - none of them is
/// needed to install DevThrottle or to start it. But a user who skipped them has a real, named
/// gap, and the honest place to say so is at the END of the install, next to what they can do
/// about it - not as a wall on screen two.
///
/// This is deliberately UI-free so both wizards render the same words and one test can prove
/// them. The screen renders this verdict and never decides for itself what a missing item means.
/// </summary>
public static class CapabilityNotice
{
    /// <summary>What each recommended item costs the user when it is absent.</summary>
    private static string Consequence(string name) => name switch
    {
        "Claude Code" => "no coding agent is installed yet, so your board has nothing to run",
        "Python" => "your own Python scripts will not run (the cc-* tools bring their own Python)",
        "Node.js" => "MCP servers and the browser tools are unavailable",
        "Tailscale" => "phones and other computers cannot reach this gateway's Cockpit over a secure address "
            + "(everything on this machine still works)",
        _ => "some features are unavailable",
    };

    /// <summary>
    /// The Complete-screen notice, or null when nothing is missing. Order follows the caller's
    /// list so the wizard's own ordering is the one the user reads.
    /// </summary>
    public static string? Describe(IEnumerable<CapabilityStatus> recommended)
    {
        ArgumentNullException.ThrowIfNull(recommended);

        var missing = recommended.Where(r => !r.IsFound).ToList();
        if (missing.Count == 0)
            return null;

        var parts = missing.Select(m => $"{m.Name}: {Consequence(m.Name)}");
        return "Not installed - " + string.Join("; ", parts)
            + ". You can install any of these at any time and DevThrottle will pick them up.";
    }
}
