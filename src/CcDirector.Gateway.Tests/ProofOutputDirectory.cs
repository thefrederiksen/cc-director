namespace CcDirector.Gateway.Tests;

/// <summary>
/// Resolves an opt-in proof output directory into one this RUN owns (issue #1156).
///
/// THE COLLISION. Proof writers take a directory from an environment variable and write fixed file names
/// into it - <c>gateway-http-transcript.txt</c>, <c>wingman-text-qa.html</c>. Two runs given the same
/// directory therefore overwrite each other's artefacts, last writer wins, with no error and no sign that
/// anything was lost. Whoever reads the artefact afterwards cannot tell which run produced it, which is
/// worse than losing it: an artefact is evidence, and evidence attributed to the wrong run is misleading
/// rather than merely missing.
///
/// THE FIX. The variable names a PARENT directory. Each run gets a subdirectory beneath it, unique per test
/// process, so concurrent runs never contend and every artefact stays attributable to the run that made it.
/// A single run - the normal case - simply gets one subdirectory, and a proof collector globs the parent.
///
/// Opt-in is unchanged: with the variable unset nothing is created and nothing is written.
/// </summary>
internal static class ProofOutputDirectory
{
    /// <summary>
    /// Unique per test process, and shared by every proof writer within it, so one run's artefacts land
    /// together in one place rather than scattered.
    /// </summary>
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// The directory this run should write proof artefacts into, or null when the variable is unset (the
    /// ordinary run, which writes nothing). Creates the directory when it resolves one.
    /// </summary>
    internal static string? ResolveOrNull(string environmentVariable)
    {
        var parent = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(parent)) return null;

        var mine = Path.Combine(parent, "run-" + RunId);
        Directory.CreateDirectory(mine);
        return mine;
    }
}
