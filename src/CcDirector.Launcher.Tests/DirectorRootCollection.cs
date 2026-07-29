using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Test classes that pin the storage root through the <c>CC_DIRECTOR_ROOT</c> environment variable must not
/// run at the same time as each other.
///
/// An environment variable is PROCESS-WIDE. xUnit runs test classes in parallel by default, so two classes
/// each pointing the root at their own temporary directory overwrite one another's setting mid-test: one
/// class writes registration files under its root while the other has already repointed the root somewhere
/// else, and both then read an empty directory. The failures land in whichever class happens to be running,
/// which makes them look like defects in the code under test rather than interference between tests.
///
/// Serialising is the right answer HERE, and it is worth saying why, because it is the wrong answer in the
/// usual case. Elsewhere, two tests colliding on a shared name are fixed by giving each its own name - the
/// sharing is the defect. This collision is not about naming: the environment variable is a single slot that
/// the code under test reads from the process it runs in, and no amount of unique naming gives two classes
/// their own copy of it. The only way to keep both honest is to let one finish before the other starts.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DirectorRootCollection
{
    public const string Name = "DirectorRoot";
}
