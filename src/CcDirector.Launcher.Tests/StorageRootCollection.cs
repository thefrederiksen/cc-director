using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Serializes every test class that redirects the storage root by setting <c>CC_DIRECTOR_ROOT</c>.
///
/// That variable is PROCESS-wide while xUnit runs different test classes CONCURRENTLY, so two classes
/// each pointing it at their own temporary directory will read each other's - and the failure lands on
/// whichever test happened to look while the other held the variable, not on the one that changed it.
/// That is a test that fails for a reason nobody can see in it. Naming the constraint and joining this
/// collection is what makes it impossible rather than unlikely.
///
/// Any new test class here that sets <c>CC_DIRECTOR_ROOT</c> must carry
/// <c>[Collection(StorageRootCollection.Name)]</c>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class StorageRootCollection
{
    public const string Name = "storage-root";
}
