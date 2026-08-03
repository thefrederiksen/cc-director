using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// A test that cannot run without a working git on the machine, and SKIPS rather than fails when
/// there is not one.
///
/// This exists because of what it is testing. The whole point of the work behind these tests is that
/// a machine with no git is a SUPPORTED machine - so a suite that goes red on one is reporting a
/// product regression that has not happened, which is the same category of mistake as the defect
/// being fixed, committed by the person fixing it. An integration test that needs git is legitimate;
/// it just has to say so instead of failing.
///
/// Note what this does NOT do: it never skips the tests that prove the missing-git behaviour itself.
/// Those inject a command name that resolves nowhere, so they run everywhere and are exactly the
/// tests a git-less machine most needs to keep.
/// </summary>
public sealed class RequiresGitFactAttribute : FactAttribute
{
    public RequiresGitFactAttribute()
    {
        if (!GitOnThisMachine.IsAvailable)
            Skip = "no working git on this machine - this is an integration test, and a machine without git is supported";
    }
}

/// <summary>Whether this machine has a git that resolves and runs. Answered once per test run.</summary>
public static class GitOnThisMachine
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var presence = CcDirector.Core.Git.GitPresenceDetector.DetectAsync().GetAwaiter().GetResult();
            return presence.Availability == CcDirector.Core.Git.GitAvailability.Present;
        }
        catch
        {
            // If we cannot even ask, treat git as unavailable and skip. A test harness that throws
            // here must not turn into a product failure.
            return false;
        }
    });

    public static bool IsAvailable => Available.Value;
}
