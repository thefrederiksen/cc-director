using System;

namespace CcDirector.Core.Tests;

/// <summary>
/// Decides whether a repository-relative path belongs to a TEST project.
///
/// WHY THIS EXISTS AS ONE THING. Four architecture guards - the loopback guard, the transcription
/// guard, the agent-plugin guard and the storage-root guard - each scan the repository and skip test
/// code, because a literal that is forbidden in production is ordinary in a fixture. Each of them
/// carried its OWN copy of that decision, written as <c>rel.Contains(".Tests/")</c>. Four copies is
/// four places to be wrong, and on 2 August all four were wrong at once: the Gateway test suite was
/// split and its fast half moved to <c>src/CcDirector.Gateway.UnitTests/</c>, whose path contains
/// <c>.UnitTests/</c> and therefore does NOT contain <c>.Tests/</c>. Roughly 2,750 test files became
/// "production" to every one of those guards in a single commit.
///
/// Two of the four then failed loudly, naming twelve files nobody had touched. **The other two passed
/// - not because they were satisfied, but because nothing among those 2,750 files happened to match
/// their patterns.** They were latent, and would have fired later on an unrelated change, presenting
/// as a red with no visible connection to a file move weeks earlier.
///
/// So the repair is one predicate, not four corrections.
///
/// WHAT IT DECIDES ON. The name of a DIRECTORY in the path, not a substring of the whole path. A test
/// project is one whose directory name ENDS WITH "Tests" - which is true of <c>CcDirector.Core.Tests</c>,
/// <c>CcDirector.Gateway.Tests</c> and <c>CcDirector.Gateway.UnitTests</c> alike, and stays true for any
/// future <c>.IntegrationTests</c> or <c>.ContractTests</c> without anybody remembering to come back
/// here. The file name itself is never consulted: <c>Foo.Tests.cs</c> sitting in a production project
/// is production code.
///
/// WHY NOT SIMPLY "ANY ANCESTOR DIRECTORY ENDING IN Tests". The first version of this helper did
/// exactly that, and an inspection caught two ways it is wrong - both of which SILENCE a guard, which
/// is worse than the narrowing it was written to replace:
///
///   * <c>src/CcDirector.Core/DiagnosticsTests/Probe.cs</c> is PRODUCTION code inside the production
///     project <c>CcDirector.Core</c>. Matching any ancestor makes it invisible to all four guards
///     while every one of them stays green.
///   * A caller passing an ABSOLUTE path would match an ancestor OUTSIDE the repository entirely. A
///     checkout living under any folder whose name ends in "Tests" would disable a guard completely -
///     a defect that depends on where somebody happened to clone, and appears nowhere in the code.
///
/// So the decision is the OWNING PROJECT directory, found by anchoring on a known source root: the
/// project is the segment DIRECTLY BELOW <c>src</c>, <c>tools</c> or <c>phone</c>. Anchoring on the
/// root is also what makes an absolute path harmless, because nothing above the root is ever read.
/// </summary>
internal static class TestProjectPath
{
    /// <summary>The source roots a project directory sits directly beneath.</summary>
    private static readonly string[] SourceRoots = { "src", "tools", "phone" };

    /// <summary>
    /// True when <paramref name="path"/> lies inside a test PROJECT. Repository-relative is the
    /// intended input; an absolute path is tolerated because the search anchors on a source root
    /// rather than counting segments from the beginning.
    /// </summary>
    internal static bool IsTestProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // The LAST source root wins, so a checkout that itself lives under a folder called "src"
        // cannot shift which segment gets read as the project.
        var rootIndex = -1;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (Array.Exists(SourceRoots, r => string.Equals(segments[i], r, StringComparison.OrdinalIgnoreCase)))
                rootIndex = i;
        }

        if (rootIndex < 0) return false;

        var projectIndex = rootIndex + 1;

        // A project directory must still have a file below it; a bare root is not a project.
        if (projectIndex >= segments.Length - 1) return false;

        return segments[projectIndex].EndsWith("Tests", StringComparison.OrdinalIgnoreCase);
    }
}
