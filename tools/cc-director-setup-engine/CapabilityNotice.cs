namespace CcDirector.Setup.Engine;

/// <summary>
/// The canonical prerequisite row names. Both wizards build their checklists from these and
/// <see cref="CapabilityNotice"/> keys off them, so a rename cannot silently detach a row from its
/// consequence text (which is what a plain magic string would do, with every test still green).
/// </summary>
public static class PrerequisiteNames
{
    public const string DotNetRuntime = ".NET 10 Runtime";
    public const string ClaudeCode = "Claude Code";
    public const string Python = "Python";
    public const string NodeJs = "Node.js";
    public const string Tailscale = "Tailscale";

    /// <summary>The rows that are recommended - checked and offered, but never gating.</summary>
    public static readonly IReadOnlyList<string> Recommended = [ClaudeCode, Python, NodeJs];
}

/// <summary>One recommended prerequisite and whether the checker accepted it.</summary>
/// <param name="Name">One of <see cref="PrerequisiteNames"/>.</param>
/// <param name="IsFound">
/// The checker's verdict, which means "present AND acceptable" - a Python 3.9 or a Node 18 is
/// reported as not found. The notice wording must therefore not assert "not installed".
/// </param>
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
/// them. A screen renders this verdict and never decides for itself what a missing item means.
/// </summary>
public static class CapabilityNotice
{
    /// <summary>
    /// What each recommended item costs the user when it is absent or too old.
    ///
    /// <paramref name="anotherAgentPresent"/> exists because the whole point of the
    /// re-classification is that DevThrottle runs eight agent command line tools. Telling a user
    /// who runs Codex or Gemini that their "board has nothing to run" would repeat, in words, the
    /// very mistake the classification change removed.
    /// </summary>
    private static string Consequence(string name, bool anotherAgentPresent) => name switch
    {
        PrerequisiteNames.ClaudeCode when anotherAgentPresent =>
            "the Claude agent is unavailable - your other coding agent still works",
        PrerequisiteNames.ClaudeCode =>
            "no coding agent is set up yet, so your board has nothing to run",
        PrerequisiteNames.Python =>
            "your own Python scripts will not run (the cc-* tools bring their own Python)",
        PrerequisiteNames.NodeJs =>
            "MCP servers and the browser tools are unavailable",
        _ => "some features are unavailable",
    };

    /// <summary>
    /// The Complete-screen notice, or null when nothing is missing. Order follows the caller's
    /// list so the wizard's own ordering is the one the user reads.
    /// </summary>
    /// <param name="recommended">
    /// The recommended rows only. Optional rows (Tailscale) must NOT be passed: they are a
    /// deliberate choice rather than a gap, and their own row already explains which part is not
    /// ready.
    /// </param>
    /// <param name="anotherAgentPresent">True when a non-Claude agent command line tool is on PATH.</param>
    public static string? Describe(IEnumerable<CapabilityStatus> recommended, bool anotherAgentPresent = false)
    {
        ArgumentNullException.ThrowIfNull(recommended);

        var missing = recommended.Where(r => !r.IsFound).ToList();
        if (missing.Count == 0)
            return null;

        // ONE LINE PER ITEM, never a semicolon-joined paragraph. Three missing items ran together
        // into a wall of amber text that had to be re-read to find where one item ended and the
        // next began - which is the opposite of the point, since each line is a separate thing the
        // user might act on.
        var lines = missing.Select(m => $"- {m.Name}: {Consequence(m.Name, anotherAgentPresent)}");

        // "Missing or out of date", never "Not installed": IsFound is false for a Python 3.9 that
        // is very much installed, and claiming otherwise would contradict the row the user just read.
        return "Missing or out of date:\n"
            + string.Join("\n", lines)
            + "\n\nYou can install any of these at any time and DevThrottle will pick them up.";
    }
}
