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
/// </summary>
internal static class TestProjectPath
{
    /// <summary>
    /// True when <paramref name="repoRelativePath"/> - forward-slashed, relative to the repository
    /// root - lies inside a test project.
    /// </summary>
    internal static bool IsTestProject(string repoRelativePath)
    {
        if (string.IsNullOrWhiteSpace(repoRelativePath)) return false;

        var segments = repoRelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Every segment EXCEPT the last, which is the file name. A directory decides this, not a file.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
